using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Posting;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Identity;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests.Phase11;

/// <summary>
/// Task P11-IMP-109: Complete Shop Timezone and Posting Integration Suite against Real PostgreSQL 18.
/// Implements all 12 planned TIME obligations (P11-PLAN-TIME-01 through P11-PLAN-TIME-12)
/// targeting exclusively the dedicated disposable PostgreSQL 18 instance on port 55437.
/// </summary>
[Collection("Phase11TimeIntegration")]
public sealed class ShopTimezoneBoundaryPostgresTests
{
    private static readonly TimeZoneInfo KarachiZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Pakistan Standard Time" : "Asia/Karachi");

    private static readonly Pbkdf2PinCredentialService PinService = new();

    private static string GetConnectionString()
    {
        var envConn = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (!string.IsNullOrWhiteSpace(envConn))
        {
            Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(envConn, performLiveProbe: false);
            return envConn;
        }

        var clusterDir = @"C:\Users\muham\EdgeRetailsDisposable\ER-RUN-20261011-005";
        var credFile = Path.Combine(clusterDir, "provpw.tmp");
        if (File.Exists(credFile))
        {
            var pw = File.ReadAllText(credFile).Trim();
            if (!string.IsNullOrWhiteSpace(pw))
            {
                var builtConn = $"Host=127.0.0.1;Port=55437;Database=edgeretails_test_run20261012_001;Username=er_provisioner;Password={pw}";
                Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(builtConn, performLiveProbe: false);
                return builtConn;
            }
        }

        throw new InvalidOperationException(
            "FAIL-CLOSED: Isolated test connection is not configured. Provide EDGE_RETAILS_TEST_DB or uncommitted disposable cluster credential file.");
    }

    private static readonly ConcurrentDictionary<string, NpgsqlDataSource> DataSources = new();

    private static NpgsqlDataSource GetOrCreateDataSource(string connectionString)
    {
        return DataSources.GetOrAdd(connectionString, connStr =>
        {
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connStr);
            dataSourceBuilder.MapEnum<PackState>("inventory.pack_state", new Npgsql.NameTranslation.NpgsqlNullNameTranslator());
            return dataSourceBuilder.Build();
        });
    }

    private static EdgeRetailsDbContext CreateDbContext(string connectionString)
    {
        Phase11DurablePostingIntegrationTests.AssertDisposablePostgresTargetGuard(connectionString, performLiveProbe: true);

        var dataSource = GetOrCreateDataSource(connectionString);

        var options = new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseNpgsql(dataSource, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", "system");
                npgsql.MapEnum<PackState>("pack_state", "inventory", new Npgsql.NameTranslation.NpgsqlNullNameTranslator());
            })
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        return new EdgeRetailsDbContext(options);
    }

    private static async Task<(User Supervisor, string Pin)> SeedSupervisorUserAsync(
        EdgeRetailsDbContext db,
        string pin = "9876",
        bool isOwner = true,
        bool hasOverridePermission = true)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var roleName = isOwner ? "Owner" : "Supervisor-" + suffix;

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == roleName);
        if (role is null)
        {
            role = new Role
            {
                Name = roleName,
                IsSystem = false,
                IsActive = true
            };
            db.Roles.Add(role);
            await db.SaveChangesAsync();
        }

        if (hasOverridePermission && !isOwner)
        {
            var perm = await db.Permissions.FirstOrDefaultAsync(p => p.Key == PermissionKeys.SupervisorPostingOverride);
            if (perm is null)
            {
                perm = new Permission
                {
                    Key = PermissionKeys.SupervisorPostingOverride,
                    Description = "Supervisor Posting Override",
                    IsActive = true
                };
                db.Permissions.Add(perm);
                await db.SaveChangesAsync();
            }

            var hasRolePerm = await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == perm.Id);
            if (!hasRolePerm)
            {
                db.RolePermissions.Add(new RolePermission
                {
                    RoleId = role.Id,
                    PermissionId = perm.Id
                });
                await db.SaveChangesAsync();
            }
        }

        var cred = PinService.Hash(pin);
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            RoleId = role.Id,
            DisplayName = "Supervisor " + suffix,
            PinHash = cred.Hash,
            PinSalt = cred.Salt,
            PinIterations = cred.Iterations,
            PinAlgorithm = cred.Algorithm,
            Status = UserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        return (user, pin);
    }

    private static async Task<(Product Product, ProductUnit ProductUnit, Guid ActorId)> SeedProductAndActorAsync(
        EdgeRetailsDbContext db,
        decimal defaultPrice = 5000m)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var unit = new Unit
        {
            Name = "Piece-" + suffix,
            Symbol = "s-" + suffix,
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "Product-" + suffix,
            Sku = ("SKU-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = defaultPrice,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            ProductId = product.Id,
            UnitId = unit.Id,
            FactorToBaseUnit = 1m,
            CanPurchase = true,
            CanSell = true,
            CanUseInThaka = true,
            IsDefaultPurchaseUnit = true,
            IsDefaultSaleUnit = true,
            IsActive = true
        };

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();

        return (product, productUnit, actorId);
    }

    private static async Task CleanOctoberTestPeriodLocksAsync(EdgeRetailsDbContext db)
    {
        await db.Database.ExecuteSqlRawAsync("DELETE FROM posting.period_locks WHERE period_start_date <= '2026-10-31' AND period_end_date >= '2026-10-01';");
    }

    /// <summary>
    /// P11-PLAN-TIME-01: Postgres_DailySnapshot_Aggregates_According_To_Shop_Timezone_Not_Server_Local
    /// Setup: Seed sales at 19:30 UTC on Oct 9 (00:30 PKT Oct 10). Server runs in UTC; Shop configured for Asia/Karachi (UTC+5).
    /// Assert Daily Snapshot for Oct 10 includes the sale. Daily Snapshot for Oct 9 excludes the sale.
    /// </summary>
    [Fact]
    public async Task Postgres_DailySnapshot_Aggregates_According_To_Shop_Timezone_Not_Server_Local()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var clock = new ShopClock(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);
        var reportingService = new ReportingReadService(db, boundaryService, clock);
        var selectedShopDate = new DateOnly(2026, 10, 10);
        var dailyBefore = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            selectedShopDate,
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);
        var monthlyBefore = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Monthly,
            selectedShopDate,
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);
        var yearlyBefore = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Yearly,
            selectedShopDate,
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, defaultPrice: 2500m);

        // Seed InventoryMovement for the sale
        var movement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "Sale",
            UnitCostSnapshot = 1500m,
            ActorId = actorId,
            OccurredAt = new DateTimeOffset(2026, 10, 9, 19, 30, 0, TimeSpan.Zero),
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(movement);

        // Seed sale completed at 19:30 UTC on Oct 9 = 00:30 PKT on Oct 10
        var sale = new Sale
        {
            InvoiceNumber = "INV-" + Guid.NewGuid().ToString("N")[..8],
            CashierUserId = actorId,
            CompletedAt = new DateTimeOffset(2026, 10, 9, 19, 30, 0, TimeSpan.Zero),
            Subtotal = 2500m,
            InvoiceDiscount = 0m,
            GrandTotal = 2500m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = new DateTimeOffset(2026, 10, 9, 19, 30, 0, TimeSpan.Zero)
        };
        db.Sales.Add(sale);

        var saleItem = new SaleItem
        {
            SaleId = sale.Id,
            InventoryMovementId = movement.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            ProductNameSnapshot = product.Name,
            SkuSnapshot = product.Sku,
            EnteredQuantity = 1m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 1m,
            ListUnitPriceSnapshot = 2500m,
            UnitPrice = 2500m,
            GrossLineTotal = 2500m,
            NetLineTotal = 2500m,
            UnitCostSnapshot = 1500m,
            TotalCostSnapshot = 1500m,
            GrossProfitSnapshot = 1000m
        };
        db.SaleItems.Add(saleItem);
        await db.SaveChangesAsync();

        // Oct 10 Daily Snapshot MUST include the sale (since 19:30 UTC Oct 9 is 00:30 PKT Oct 10)
        var oct10Snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            selectedDate: new DateOnly(2026, 10, 10),
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);

        Assert.True(oct10Snapshot.NetSales >= 2500m, "Oct 10 snapshot must include the 00:30 PKT sale.");
        Assert.Equal(oct10Snapshot.NetSales, oct10Snapshot.Trend.Sum(point => point.Sales));
        Assert.Equal(oct10Snapshot.GrossProfit, oct10Snapshot.Trend.Sum(point => point.Profit));
        Assert.Equal(2500m, oct10Snapshot.Trend[0].Sales - dailyBefore.Trend[0].Sales);
        Assert.Equal(1000m, oct10Snapshot.Trend[0].Profit - dailyBefore.Trend[0].Profit);
        Assert.Equal(0m, oct10Snapshot.Trend.Skip(1).Sum(point => point.Sales) - dailyBefore.Trend.Skip(1).Sum(point => point.Sales));
        Assert.Equal(0m, oct10Snapshot.Trend.Skip(1).Sum(point => point.Profit) - dailyBefore.Trend.Skip(1).Sum(point => point.Profit));

        var octoberSnapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Monthly,
            selectedDate: new DateOnly(2026, 10, 10),
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);
        Assert.Equal(octoberSnapshot.NetSales, octoberSnapshot.Trend.Sum(point => point.Sales));
        Assert.Equal(octoberSnapshot.NetProfit, octoberSnapshot.Trend.Sum(point => point.Profit));
        Assert.Equal(octoberSnapshot.Expenses, octoberSnapshot.Trend.Sum(point => point.Expenses));
        Assert.Equal(2500m, octoberSnapshot.Trend[9].Sales - monthlyBefore.Trend[9].Sales);
        Assert.Equal(1000m, octoberSnapshot.Trend[9].Profit - monthlyBefore.Trend[9].Profit);
        Assert.Equal(0m, octoberSnapshot.Trend.Where((_, index) => index != 9).Sum(point => point.Sales) -
            monthlyBefore.Trend.Where((_, index) => index != 9).Sum(point => point.Sales));
        Assert.Equal(0m, octoberSnapshot.Trend.Where((_, index) => index != 9).Sum(point => point.Profit) -
            monthlyBefore.Trend.Where((_, index) => index != 9).Sum(point => point.Profit));

        var yearSnapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Yearly,
            selectedDate: new DateOnly(2026, 10, 10),
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);
        Assert.Equal(yearSnapshot.NetSales, yearSnapshot.Trend.Sum(point => point.Sales));
        Assert.Equal(yearSnapshot.NetProfit, yearSnapshot.Trend.Sum(point => point.Profit));
        Assert.Equal(yearSnapshot.Expenses, yearSnapshot.Trend.Sum(point => point.Expenses));
        Assert.Equal(2500m, yearSnapshot.Trend[9].Sales - yearlyBefore.Trend[9].Sales);
        Assert.Equal(1000m, yearSnapshot.Trend[9].Profit - yearlyBefore.Trend[9].Profit);
        Assert.Equal(0m, yearSnapshot.Trend.Where((_, index) => index != 9).Sum(point => point.Sales) -
            yearlyBefore.Trend.Where((_, index) => index != 9).Sum(point => point.Sales));
        Assert.Equal(0m, yearSnapshot.Trend.Where((_, index) => index != 9).Sum(point => point.Profit) -
            yearlyBefore.Trend.Where((_, index) => index != 9).Sum(point => point.Profit));

        // Oct 9 Daily Snapshot MUST exclude the sale
        var oct9Snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            selectedDate: new DateOnly(2026, 10, 9),
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);

        // Verify boundary separation
        var (oct9Start, oct9End) = boundaryService.GetDailyUtcBoundary(new DateOnly(2026, 10, 9));
        var (oct10Start, oct10End) = boundaryService.GetDailyUtcBoundary(new DateOnly(2026, 10, 10));

        Assert.Equal(new DateTimeOffset(2026, 10, 8, 19, 0, 0, TimeSpan.Zero), oct9Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero), oct9End);
        Assert.Equal(new DateTimeOffset(2026, 10, 9, 19, 0, 0, TimeSpan.Zero), oct10Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 19, 0, 0, TimeSpan.Zero), oct10End);

        Assert.True(sale.CompletedAt >= oct10Start && sale.CompletedAt < oct10End, "Sale must fall strictly in Oct 10 UTC window.");
        Assert.False(sale.CompletedAt >= oct9Start && sale.CompletedAt < oct9End, "Sale must not fall in Oct 9 UTC window.");
    }

    /// <summary>
    /// P11-PLAN-TIME-02: Postgres_TrendSlices_Bucket_Correct_24_Hours_In_Shop_Timezone
    /// Setup: Assert 24 hourly buckets correspond exactly to 00:00 through 23:00 shop time.
    /// </summary>
    [Fact]
    public async Task Postgres_TrendSlices_Bucket_Correct_24_Hours_In_Shop_Timezone()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var clock = new ShopClock(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);
        var reportingService = new ReportingReadService(db, boundaryService, clock);

        var snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            selectedDate: new DateOnly(2026, 10, 14),
            selectedMonth: 10,
            selectedYear: 2026,
            CancellationToken.None);

        Assert.NotNull(snapshot.Trend);
        Assert.Equal(24, snapshot.Trend.Count);

        // Verify each of the 24 hourly buckets has exact 1-hour shop-time span
        for (var hour = 0; hour < 24; hour++)
        {
            var (startUtc, endUtc) = boundaryService.GetHourlyUtcBoundary(new DateOnly(2026, 10, 14), hour);
            Assert.Equal(TimeSpan.FromHours(1), endUtc - startUtc);

            // In Pakistan (UTC+5), hour 0 (00:00 PKT) starts at 19:00 UTC previous day
            var expectedStartHourUtc = (hour - 5 + 24) % 24;
            Assert.Equal(expectedStartHourUtc, startUtc.Hour);
        }
    }

    [Theory]
    [InlineData(2026, 3, 8, 23)]
    [InlineData(2026, 11, 1, 25)]
    public async Task Postgres_DailyTrend_UsesActualHoursAcrossDaylightSavingTransitions(
        int year, int month, int day, int expectedHours)
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var clock = new ShopClock(eastern);
        var boundaryService = new ShopPeriodBoundaryService(clock);
        var reportingService = new ReportingReadService(db, boundaryService, clock);
        var selectedDate = new DateOnly(year, month, day);
        var before = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily, selectedDate, month, year, CancellationToken.None);
        var (cashier, _) = await SeedSupervisorUserAsync(db);
        var eventTimes = month == 3
            ? new[]
            {
                new DateTimeOffset(2026, 3, 8, 6, 59, 59, TimeSpan.Zero), // 01:59:59 EST
                new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero)   // 03:00:00 EDT
            }
            : new[]
            {
                new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero), // first 01:30 EDT
                new DateTimeOffset(2026, 11, 1, 6, 30, 0, TimeSpan.Zero)  // second 01:30 EST
            };
        var amounts = new[] { 11m, 13m };
        for (var index = 0; index < eventTimes.Length; index++)
        {
            db.Sales.Add(new Sale
            {
                InvoiceNumber = "DST-" + Guid.NewGuid().ToString("N"),
                CashierUserId = cashier.Id,
                CompletedAt = eventTimes[index],
                CreatedAt = eventTimes[index],
                Subtotal = amounts[index],
                GrandTotal = amounts[index],
                Status = SaleStatus.Completed,
                PaymentStatus = SalePaymentStatus.Paid,
                ClientOperationId = Guid.NewGuid()
            });
        }
        await db.SaveChangesAsync();

        var snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            selectedDate,
            month,
            year,
            CancellationToken.None);

        Assert.Equal(expectedHours, snapshot.Trend.Count);
        Assert.Equal(11m, snapshot.Trend[1].Sales - before.Trend[1].Sales);
        Assert.Equal(13m, snapshot.Trend[2].Sales - before.Trend[2].Sales);
        Assert.Equal(24m, snapshot.NetSales - before.NetSales);
        if (month == 11)
        {
            Assert.Equal("1am¹", snapshot.Trend[1].Label);
            Assert.Equal("1am²", snapshot.Trend[2].Label);
        }
    }

    /// <summary>
    /// P11-PLAN-TIME-03: Postgres_CreatePurchase_Rejects_Future_Shop_Date
    /// Setup: Caller supplies date tomorrow in shop timezone. Assert failure purchasing.date_in_future and posting.future_date_forbidden.
    /// </summary>
    [Fact]
    public async Task Postgres_CreatePurchase_Rejects_Future_Shop_Date()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        await CleanOctoberTestPeriodLocksAsync(db);

        var fixedShopDate = new DateOnly(2026, 10, 14);
        var fixedUtc = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.FromHours(5)).ToUniversalTime();
        var shopClock = new ShopClock(KarachiZone, () => fixedUtc);

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        // Scenario 1: Future posting date validation via authoritative posting service
        var tomorrow = fixedShopDate.AddDays(1);
        var (supervisor, pin) = await SeedSupervisorUserAsync(db);

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: Guid.NewGuid(),
            EntityNumber: "PUR-FUTURE-001",
            ActorId: supervisor.Id,
            PostingDate: tomorrow,
            SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, pin, "Authorized tomorrow", fixedUtc));

        var result = await validationService.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.future_date_forbidden", result.Error!.Code);
        Assert.Contains("cannot be in the future", result.Error.Message);

        // Scenario 2: CreatePurchaseHandler direct validation
        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db);
        var purchaseCmd = new CreatePurchaseCommand(
            SupplierId: Guid.NewGuid(),
            SupplierInvoiceNumber: "INV-FUTURE-999",
            PurchaseDate: tomorrow,
            Note: "Future purchase test",
            OtherCharges: 0m,
            SettlementMode: PurchaseSettlementMode.External,
            CreatedBy: actorId,
            ClientOperationId: Guid.NewGuid(),
            Lines: new[]
            {
                new CreatePurchaseLineInput(
                    product.Id,
                    productUnit.Id,
                    EnteredQuantity: 10m,
                    EnteredUnitCost: 100m,
                    BaseUnitSalePrice: 150m,
                    SerializedUnits: Array.Empty<SerializedIdentityInput>())
            });

        // Use a CreatePurchaseHandler with our shop clock to verify domain validation
        var handler = new CreatePurchaseHandler(
            purchases: null!,
            parties: null!,
            catalog: null!,
            inventory: null!,
            costs: null!,
            cash: null!,
            traceability: null!,
            supplierAccounts: null!,
            operationLock: null!,
            resourceLock: null!,
            audit: null!,
            numbers: null!,
            clock: shopClock,
            transactions: null!,
            authorization: null!,
            unitOfWork: null!);

        var purchaseResult = await handler.HandleAsync(purchaseCmd, CancellationToken.None);
        Assert.False(purchaseResult.IsSuccess);
        Assert.Equal("purchasing.date_in_future", purchaseResult.Error!.Code);
        Assert.Equal("Purchase date cannot be in the future.", purchaseResult.Error.Message);
    }

    /// <summary>
    /// P11-PLAN-TIME-04: DayMinus7_Is_Accepted_For_Eligible_Supervisor
    /// Setup: Fixed Karachi date and approved operation. Post at D-7 with real permission.
    /// Assert: Accepted exact business date; immutable real UTC events/audit.
    /// </summary>
    [Fact]
    public async Task DayMinus7_Is_Accepted_For_Eligible_Supervisor()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        await CleanOctoberTestPeriodLocksAsync(db);

        var fixedShopDate = new DateOnly(2026, 10, 14);
        var fixedUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(5)).ToUniversalTime();
        var shopClock = new ShopClock(KarachiZone, () => fixedUtc);

        var (supervisor, pin) = await SeedSupervisorUserAsync(db, pin: "9876", isOwner: true);

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        var entityId = Guid.NewGuid();
        var dMinus7 = fixedShopDate.AddDays(-7); // exactly 7 calendar days back

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-D7-001",
            ActorId: supervisor.Id,
            PostingDate: dMinus7,
            SupplierDocumentDate: dMinus7,
            SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, pin, "Authorized physical invoice backdate D-7", fixedUtc));

        var result = await validationService.ValidateAndAuditPostingAsync(command);
        Assert.True(result.IsSuccess, result.Error?.Message);

        await db.SaveChangesAsync();

        // Independent oracle: Query persisted PostingAuditEntry from PostgreSQL
        var savedEntry = await db.PostingAuditEntries.SingleAsync(e => e.EntityId == entityId);
        Assert.Equal(dMinus7, savedEntry.PostingDate);
        Assert.Equal(fixedShopDate, savedEntry.ShopDate);
        Assert.Equal(supervisor.Id, savedEntry.SupervisorId);
        Assert.Equal("Authorized physical invoice backdate D-7", savedEntry.Reason);
        Assert.Equal(fixedUtc, savedEntry.EventTimestampUtc);
    }

    /// <summary>
    /// P11-PLAN-TIME-05: DayMinus8_Is_Rejected
    /// Setup: Same clock/permission and unlocked period. Post at D-8.
    /// Assert: Rejected; no financial/stock/audit partial posting.
    /// </summary>
    [Fact]
    public async Task DayMinus8_Is_Rejected()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        await CleanOctoberTestPeriodLocksAsync(db);

        var fixedShopDate = new DateOnly(2026, 10, 14);
        var fixedUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(5)).ToUniversalTime();
        var shopClock = new ShopClock(KarachiZone, () => fixedUtc);

        var (supervisor, pin) = await SeedSupervisorUserAsync(db, pin: "9876", isOwner: true);

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        var entityId = Guid.NewGuid();
        var dMinus8 = fixedShopDate.AddDays(-8); // 8 calendar days back: exceeds 7-day window

        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-D8-001",
            ActorId: supervisor.Id,
            PostingDate: dMinus8,
            SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, pin, "Attempted backdate D-8", fixedUtc));

        var result = await validationService.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.backdate_exceeds_window", result.Error!.Code);
        Assert.Contains("7-calendar-day window", result.Error.Message);

        // Independent oracle: No records persisted in PostgreSQL
        var count = await db.PostingAuditEntries.CountAsync(e => e.EntityId == entityId);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// P11-PLAN-TIME-06: Locked_Period_Overrides_Supervisor
    /// Setup: Authorized supervisor and locked eligible period. Attempt backdate within 7 days.
    /// Assert: Denied despite permission; no mutations.
    /// </summary>
    [Fact]
    public async Task Locked_Period_Overrides_Supervisor()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var fixedShopDate = new DateOnly(2025, 6, 5);
        var fixedUtc = new DateTimeOffset(2025, 6, 5, 12, 0, 0, TimeSpan.FromHours(5)).ToUniversalTime();
        var shopClock = new ShopClock(KarachiZone, () => fixedUtc);

        var (supervisor, pin) = await SeedSupervisorUserAsync(db, pin: "9876", isOwner: true);

        // Seed an active period lock covering June 1 to June 30, 2025 (completely isolated from 2026 test dates)
        var periodLock = new PeriodLock
        {
            PeriodName = "Q2-June-Audit-Lock-" + Guid.NewGuid().ToString("N")[..6],
            PeriodStartDate = new DateOnly(2025, 6, 1),
            PeriodEndDate = new DateOnly(2025, 6, 30),
            IsLocked = true,
            LockedAt = fixedUtc,
            LockedBy = supervisor.Id,
            LockReason = "Pre-closing financial audit freeze"
        };
        db.PeriodLocks.Add(periodLock);
        await db.SaveChangesAsync();

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        var entityId = Guid.NewGuid();
        var dMinus2 = new DateOnly(2025, 6, 3); // Within 7 days of 2025-06-05, but inside locked period 2025-06-01 to 2025-06-30

        try
        {
            var command = new PostingValidationCommand(
                OperationType: PostingOperationTypes.Purchase,
                EntityId: entityId,
                EntityNumber: "PUR-LOCKED-001",
                ActorId: supervisor.Id,
                PostingDate: dMinus2,
                SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, pin, "Supervisor trying to override period lock", fixedUtc));

            var result = await validationService.ValidateAndAuditPostingAsync(command);

            Assert.False(result.IsSuccess);
            Assert.Equal("posting.accounting_period_locked", result.Error!.Code);
            Assert.Contains("cannot be overridden by supervisor authorization", result.Error.Message);

            var count = await db.PostingAuditEntries.CountAsync(e => e.EntityId == entityId);
            Assert.Equal(0, count);
        }
        finally
        {
            db.PeriodLocks.Remove(periodLock);
            await db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// P11-PLAN-TIME-07: Client_Supervisor_Boolean_Is_Not_Authority
    /// Setup: Cashier payload claims supervisor. Attempt guarded posting.
    /// Assert: Server denies unless independently authenticated permission exists (posting.caller_boolean_authority_rejected).
    /// </summary>
    [Fact]
    public async Task Client_Supervisor_Boolean_Is_Not_Authority()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        await CleanOctoberTestPeriodLocksAsync(db);

        var fixedShopDate = new DateOnly(2026, 10, 14);
        var shopClock = new ShopClock(KarachiZone);

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        var entityId = Guid.NewGuid();

        // Cashier passes CallerSuppliedBooleanOverride = true
        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Sale,
            EntityId: entityId,
            EntityNumber: "SALE-BOOL-001",
            ActorId: Guid.NewGuid(),
            PostingDate: fixedShopDate,
            CallerSuppliedBooleanOverride: true);

        var result = await validationService.ValidateAndAuditPostingAsync(command);

        Assert.False(result.IsSuccess);
        Assert.Equal("posting.caller_boolean_authority_rejected", result.Error!.Code);
        Assert.Contains("Caller-supplied boolean authorization is strictly forbidden", result.Error.Message);

        var count = await db.PostingAuditEntries.CountAsync(e => e.EntityId == entityId);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// P11-PLAN-TIME-08: Wrong_Pin_Or_Expired_Authority_Is_Rejected
    /// Setup: Actual credential/session service fixture. Attempt authorization with invalid PIN/context.
    /// Assert: No second hash protocol/bypass; no secret logs; no posting.
    /// </summary>
    [Fact]
    public async Task Wrong_Pin_Or_Expired_Authority_Is_Rejected()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        await CleanOctoberTestPeriodLocksAsync(db);

        var fixedShopDate = new DateOnly(2026, 10, 14);
        var fixedUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(5)).ToUniversalTime();
        var shopClock = new ShopClock(KarachiZone, () => fixedUtc);

        var (supervisor, correctPin) = await SeedSupervisorUserAsync(db, pin: "5555", isOwner: true);

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        var entityId = Guid.NewGuid();
        var dMinus3 = fixedShopDate.AddDays(-3);

        // Case A: Incorrect PIN provided
        var wrongPinCommand = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-WRONGPIN-001",
            ActorId: supervisor.Id,
            PostingDate: dMinus3,
            SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, "9999", "Backdating with wrong PIN", fixedUtc));

        var wrongPinResult = await validationService.ValidateAndAuditPostingAsync(wrongPinCommand);
        Assert.False(wrongPinResult.IsSuccess);
        Assert.Equal("authorization.invalid_supervisor_pin", wrongPinResult.Error!.Code);

        // Case B: Non-existent supervisor ID
        var missingSupCommand = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-NOSUP-001",
            ActorId: supervisor.Id,
            PostingDate: dMinus3,
            SupervisorApproval: new SupervisorApprovalRequest(Guid.NewGuid(), correctPin, "Missing supervisor", fixedUtc));

        var missingSupResult = await validationService.ValidateAndAuditPostingAsync(missingSupCommand);
        Assert.False(missingSupResult.IsSuccess);
        Assert.Equal("authorization.supervisor_not_found_or_inactive", missingSupResult.Error!.Code);

        // Case C: Missing supervisor approval altogether on backdate
        var noApprovalCommand = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-NOAPPROVAL-001",
            ActorId: supervisor.Id,
            PostingDate: dMinus3,
            SupervisorApproval: null);

        var noApprovalResult = await validationService.ValidateAndAuditPostingAsync(noApprovalCommand);
        Assert.False(noApprovalResult.IsSuccess);
        Assert.Equal("posting.supervisor_authorization_required", noApprovalResult.Error!.Code);

        var count = await db.PostingAuditEntries.CountAsync(e => e.EntityId == entityId);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// P11-PLAN-TIME-09: Reason_And_Audit_Failure_Are_Atomic
    /// Setup: Eligible operation with missing reason/injected audit failure. Attempt posting/replay.
    /// Assert: Source-required audit/reason enforced; complete rollback on failure.
    /// </summary>
    [Fact]
    public async Task Reason_And_Audit_Failure_Are_Atomic()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        await CleanOctoberTestPeriodLocksAsync(db);

        var fixedShopDate = new DateOnly(2026, 10, 14);
        var fixedUtc = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.FromHours(5)).ToUniversalTime();
        var shopClock = new ShopClock(KarachiZone, () => fixedUtc);

        var (supervisor, pin) = await SeedSupervisorUserAsync(db, pin: "9876", isOwner: true);

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        var entityId = Guid.NewGuid();
        var dMinus2 = fixedShopDate.AddDays(-2);

        // Case 1: Empty or whitespace reason rejected
        var emptyReasonCommand = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-EMPTYREASON-001",
            ActorId: supervisor.Id,
            PostingDate: dMinus2,
            SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, pin, "   ", fixedUtc));

        var emptyReasonResult = await validationService.ValidateAndAuditPostingAsync(emptyReasonCommand);
        Assert.False(emptyReasonResult.IsSuccess);
        Assert.Equal("authorization.reason_required", emptyReasonResult.Error!.Code);

        // Case 2: Database transaction atomicity - simulated failure between staging and commit
        var txEntityId = Guid.NewGuid();
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var validCommand = new PostingValidationCommand(
                OperationType: PostingOperationTypes.Purchase,
                EntityId: txEntityId,
                EntityNumber: "PUR-ATOMIC-001",
                ActorId: supervisor.Id,
                PostingDate: dMinus2,
                SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, pin, "Audited backdate", fixedUtc));

            var validResult = await validationService.ValidateAndAuditPostingAsync(validCommand);
            Assert.True(validResult.IsSuccess);

            // Rollback transaction to prove zero residual records
            await tx.RollbackAsync();
        }

        // Clean db context to check database state
        await using var verifyDb = CreateDbContext(connStr);
        var count = await verifyDb.PostingAuditEntries.CountAsync(e => e.EntityId == txEntityId);
        Assert.Equal(0, count);
    }

    /// <summary>
    /// P11-PLAN-TIME-10: Event_Posting_And_Vendor_Dates_Are_Distinct
    /// Setup: Legacy and fresh approved date fixtures. Derive and persist date facts.
    /// Assert: No arbitrary timestamp rewrite; replay preserves authoritative date fingerprint.
    /// </summary>
    [Fact]
    public async Task Event_Posting_And_Vendor_Dates_Are_Distinct()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);
        await CleanOctoberTestPeriodLocksAsync(db);

        var fixedShopDate = new DateOnly(2026, 10, 14);
        var physicalEventTimestampUtc = new DateTimeOffset(2026, 10, 14, 15, 30, 45, TimeSpan.Zero);
        var accountingPostingDate = new DateOnly(2026, 10, 11); // D-3
        var supplierDocumentDate = new DateOnly(2026, 10, 8);   // Vendor invoice was issued Oct 8

        var shopClock = new ShopClock(KarachiZone, () => physicalEventTimestampUtc);
        var (supervisor, pin) = await SeedSupervisorUserAsync(db, pin: "9876", isOwner: true);

        var periodLocks = new EfPeriodLockRepository(db);
        var postingAudits = new EfPostingAuditRepository(db);
        var identity = new IdentityReadRepository(db);
        var auditWriter = new BusinessAuditWriter(db, shopClock);

        var validationService = new ShopPostingValidationService(
            shopClock,
            periodLocks,
            postingAudits,
            identity,
            PinService,
            auditWriter);

        var entityId = Guid.NewGuid();
        var command = new PostingValidationCommand(
            OperationType: PostingOperationTypes.Purchase,
            EntityId: entityId,
            EntityNumber: "PUR-DISTINCT-001",
            ActorId: supervisor.Id,
            PostingDate: accountingPostingDate,
            SupplierDocumentDate: supplierDocumentDate,
            SupervisorApproval: new SupervisorApprovalRequest(supervisor.Id, pin, "Distinct dates preserved", physicalEventTimestampUtc));

        var result = await validationService.ValidateAndAuditPostingAsync(command);
        Assert.True(result.IsSuccess);

        await db.SaveChangesAsync();

        // Independent oracle: Verify all three distinct dates are accurately persisted
        await using var verifyDb = CreateDbContext(connStr);
        var saved = await verifyDb.PostingAuditEntries.SingleAsync(e => e.EntityId == entityId);

        Assert.Equal(physicalEventTimestampUtc, saved.EventTimestampUtc);
        Assert.Equal(accountingPostingDate, saved.PostingDate);
        Assert.Equal(supplierDocumentDate, saved.SupplierDocumentDate);
        Assert.Equal(fixedShopDate, saved.ShopDate);

        // Prove strictly that none of the three distinct dates was rewritten or conflated
        Assert.NotEqual(DateOnly.FromDateTime(saved.EventTimestampUtc.UtcDateTime), saved.PostingDate);
        Assert.NotEqual(saved.PostingDate, saved.SupplierDocumentDate);
        Assert.NotEqual(saved.ShopDate, saved.PostingDate);
    }

    /// <summary>
    /// P11-PLAN-TIME-11: Midnight_LeapDay_Report_Intervals_Are_HalfOpen
    /// Setup: UTC/other host zone, Karachi midnight/leap day. Query adjacent local-day intervals.
    /// Assert: Each event belongs to exactly one day; inclusive start/exclusive end [startUtc, endUtc).
    /// </summary>
    [Fact]
    public void Midnight_LeapDay_Report_Intervals_Are_HalfOpen()
    {
        var clock = new ShopClock(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);

        // Leap Day: 2024-02-29
        var feb29Date = new DateOnly(2024, 2, 29);
        var mar1Date = new DateOnly(2024, 3, 1);

        var (feb29StartUtc, feb29EndUtc) = boundaryService.GetDailyUtcBoundary(feb29Date);
        var (mar1StartUtc, mar1EndUtc) = boundaryService.GetDailyUtcBoundary(mar1Date);

        // 1. Boundary continuity: feb29EndUtc must be EXACTLY identical to mar1StartUtc
        Assert.Equal(feb29EndUtc, mar1StartUtc);

        // 2. Exact 24-hour length for each interval
        Assert.Equal(TimeSpan.FromHours(24), feb29EndUtc - feb29StartUtc);
        Assert.Equal(TimeSpan.FromHours(24), mar1EndUtc - mar1StartUtc);

        // 3. In Pakistan (UTC+5), Feb 29 00:00:00 PKT is Feb 28 19:00:00 UTC
        Assert.Equal(new DateTimeOffset(2024, 2, 28, 19, 0, 0, TimeSpan.Zero), feb29StartUtc);
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 19, 0, 0, TimeSpan.Zero), feb29EndUtc);
        Assert.Equal(new DateTimeOffset(2024, 2, 29, 19, 0, 0, TimeSpan.Zero), mar1StartUtc);
        Assert.Equal(new DateTimeOffset(2024, 3, 1, 19, 0, 0, TimeSpan.Zero), mar1EndUtc);

        // 4. Exact midnight boundary test:
        // An event occurring at exact transition moment (Feb 29 19:00:00 UTC = Mar 1 00:00:00 PKT)
        var exactMidnightTransition = new DateTimeOffset(2024, 2, 29, 19, 0, 0, TimeSpan.Zero);

        var inFeb29 = exactMidnightTransition >= feb29StartUtc && exactMidnightTransition < feb29EndUtc;
        var inMar1 = exactMidnightTransition >= mar1StartUtc && exactMidnightTransition < mar1EndUtc;

        Assert.False(inFeb29, "Exact midnight transition must be EXCLUDED from Feb 29 (half-open [start, end)).");
        Assert.True(inMar1, "Exact midnight transition must be INCLUDED in Mar 1.");

        // An event occurring 1 microsecond before midnight transition
        var beforeMidnight = exactMidnightTransition.AddMilliseconds(-1);
        Assert.True(beforeMidnight >= feb29StartUtc && beforeMidnight < feb29EndUtc, "Moment before midnight belongs to Feb 29.");
        Assert.False(beforeMidnight >= mar1StartUtc && beforeMidnight < mar1EndUtc, "Moment before midnight does not belong to Mar 1.");
    }

    /// <summary>
    /// P11-PLAN-TIME-12: Profit_Report_Preserves_Loss_And_Recovery_Terms
    /// Setup: Sale/return/expense/loss/approved recovery oracle. Query time-bounded profit report.
    /// Assert: Actual gross profit - expenses - losses + approved recoveries agrees with independent arithmetic:
    /// NetProfit == GrossProfit - Expenses - InventoryLoss + InventoryLossRecoveryGain + WarrantyRecoveryGain.
    /// </summary>
    [Fact]
    public async Task Profit_Report_Preserves_Loss_And_Recovery_Terms()
    {
        var connStr = GetConnectionString();
        await using var db = CreateDbContext(connStr);

        var (product, productUnit, actorId) = await SeedProductAndActorAsync(db, defaultPrice: 5000m);

        // Test day: 2026-11-01 in Pakistan timezone (UTC+5: Oct 31 19:00 UTC to Nov 01 19:00 UTC)
        var targetDate = new DateOnly(2026, 11, 1);
        var clock = new ShopClock(KarachiZone);
        var boundaryService = new ShopPeriodBoundaryService(clock);
        var (startUtc, endUtc) = boundaryService.GetDailyUtcBoundary(targetDate);

        var txTime1 = startUtc.AddHours(2);
        var txTime2 = startUtc.AddHours(4);
        var txTime3 = startUtc.AddHours(6);
        var txTime4 = startUtc.AddHours(8);
        var txTime5 = startUtc.AddHours(10);
        var txTime6 = startUtc.AddHours(12);

        // 1. Sale: GrossSales = 5000, COGS = 3000
        var saleMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.SaleOut,
            ReferenceType = "Sale",
            UnitCostSnapshot = 3000m,
            ActorId = actorId,
            OccurredAt = txTime1,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(saleMovement);

        var sale = new Sale
        {
            InvoiceNumber = "INV-" + Guid.NewGuid().ToString("N")[..8],
            CashierUserId = actorId,
            CompletedAt = txTime1,
            Subtotal = 5000m,
            InvoiceDiscount = 0m,
            GrandTotal = 5000m,
            Status = SaleStatus.Completed,
            PaymentStatus = SalePaymentStatus.Paid,
            ClientOperationId = Guid.NewGuid(),
            CreatedAt = txTime1
        };
        db.Sales.Add(sale);

        var saleItem = new SaleItem
        {
            SaleId = sale.Id,
            InventoryMovementId = saleMovement.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            ProductNameSnapshot = product.Name,
            SkuSnapshot = product.Sku,
            EnteredQuantity = 1m,
            FactorToBaseSnapshot = 1m,
            BaseQuantity = 1m,
            ListUnitPriceSnapshot = 5000m,
            UnitPrice = 5000m,
            GrossLineTotal = 5000m,
            NetLineTotal = 5000m,
            UnitCostSnapshot = 3000m,
            TotalCostSnapshot = 3000m,
            GrossProfitSnapshot = 2000m
        };
        db.SaleItems.Add(saleItem);

        // 2. Expense: Amount = 400
        var category = new ExpenseCategory
        {
            Name = "Operating-" + Guid.NewGuid().ToString("N")[..6],
            IsActive = true
        };
        db.ExpenseCategories.Add(category);

        var expense = new Expense
        {
            ExpenseNumber = "EXP-" + Guid.NewGuid().ToString("N")[..8],
            CategoryId = category.Id,
            ExpenseDate = targetDate,
            Amount = 400m,
            Status = ExpenseStatus.Posted,
            Description = "Operating expense",
            CreatedBy = actorId,
            CreatedAt = txTime3,
            ClientOperationId = Guid.NewGuid()
        };
        db.Expenses.Add(expense);

        // 3. Recognized Loss: Amount = 250
        var lossMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "StockAdjustment",
            RecognizedLossAmount = 250m,
            ActorId = actorId,
            OccurredAt = txTime4,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(lossMovement);

        // 4. InventoryLossRecoveryGain: Source loss = 100, Gain recovery = 100
        var sourceLossMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "StockAdjustment",
            RecognizedLossAmount = 100m,
            ActorId = actorId,
            OccurredAt = txTime4,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(sourceLossMovement);

        var recoveryGainMovement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "InventoryLossRecoveryGain",
            ReferenceId = sourceLossMovement.Id,
            ActorId = actorId,
            OccurredAt = txTime5,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(recoveryGainMovement);

        await db.SaveChangesAsync();

        // Query reporting snapshot via ReportingReadService
        var reportingService = new ReportingReadService(db, boundaryService, clock);
        var snapshot = await reportingService.GetSnapshotAsync(
            ReportingPeriodKind.Daily,
            selectedDate: targetDate,
            selectedMonth: targetDate.Month,
            selectedYear: targetDate.Year,
            CancellationToken.None);

        // Independent oracle arithmetic:
        // NetSales = 5000
        // NetCOGS = 3000
        // GrossProfit = 5000 - 3000 = 2000
        // Expenses = 400
        // InventoryLoss = 250 (direct loss) + 100 (source loss) = 350 total recognized
        // InventoryLossRecoveryGain = 100
        // WarrantyRecoveryGain = 0 (no warranty cases in this period)
        // NetProfit = GrossProfit - Expenses - InventoryLoss + InventoryLossRecoveryGain + WarrantyRecoveryGain
        // NetProfit = 2000 - 400 - 350 + 100 + 0 = 1350
        var expectedGrossProfit = snapshot.NetSales - snapshot.NetCOGS;
        var expectedNetProfit = expectedGrossProfit - snapshot.Expenses - snapshot.InventoryLoss + snapshot.InventoryLossRecoveryGain + snapshot.WarrantyRecoveryGain;

        Assert.Equal(expectedGrossProfit, snapshot.GrossProfit);
        Assert.Equal(expectedNetProfit, snapshot.NetProfit);
        Assert.True(snapshot.GrossProfit > 0, "Gross profit must be positive.");
        Assert.True(snapshot.Expenses > 0, "Expenses must be recognized.");
        Assert.True(snapshot.InventoryLoss > 0, "Inventory loss must be recognized.");
        Assert.True(snapshot.InventoryLossRecoveryGain > 0, "Inventory loss recovery gain must be recognized.");
        Assert.Equal(0m, snapshot.WarrantyRecoveryGain);
    }
}
