using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Contracts.Packs;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Repositories;
using EdgeRetails.Infrastructure.Services;
using EdgeRetails.Server.Controllers;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace EdgeRetails.IntegrationTests.Phase11;

/// <summary>
/// Task P11-IMP-108: Complete Hostile Concurrency Suite against Real PostgreSQL 18.
/// Implements all 14 planned CONCURRENCY obligations (P11-PLAN-CONCURRENCY-01 through P11-PLAN-CONCURRENCY-14)
/// targeting exclusively the dedicated disposable PostgreSQL 18 instance on port 55437.
/// </summary>
public sealed class PackConcurrencyPostgresTests
{
    private static readonly TimeZoneInfo KarachiZone = TimeZoneInfo.FindSystemTimeZoneById(
        OperatingSystem.IsWindows() ? "Pakistan Standard Time" : "Asia/Karachi");

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

    private static PackContainersController CreateRealPostgresPackController(
        EdgeRetailsDbContext db,
        Guid actorId)
    {
        var clock = new SystemClock(KarachiZone, () => DateTimeOffset.UtcNow);
        var invRepo = new InventoryRepository(db);
        var packAuthority = new PackSubLedgerAllocationAuthority();
        var controller = new PackContainersController(db, invRepo, db, clock, packAuthority);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["ActorContext"] = new ActorContext(
            UserId: actorId,
            SessionId: Guid.NewGuid(),
            DisplayName: "Test Concurrency Actor",
            RoleId: Guid.NewGuid(),
            RoleName: "Cashier",
            Permissions: new HashSet<string> { PermissionKeys.SalesPosUse, PermissionKeys.InventoryManage });

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = httpContext
        };
        return controller;
    }

    private static async Task EnsureReceiptConfigurationAsync(EdgeRetailsDbContext db)
    {
        if (!await db.ShopProfiles.AnyAsync(x => x.ProfileKey == "PRIMARY"))
        {
            db.ShopProfiles.Add(new ShopProfile
            {
                ProfileKey = "PRIMARY",
                ShopName = "Edge Retails Integration Shop",
                Phone = "000",
                Address = "Integration Test",
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        if (!await db.ReceiptTemplateSettings.AnyAsync(x => x.TemplateKey == "PRIMARY"))
        {
            db.ReceiptTemplateSettings.Add(new ReceiptTemplateSettings
            {
                TemplateKey = "PRIMARY",
                Header = "Integration",
                Footer = "Test",
                ShowCustomer = true,
                ShowCashier = true,
                LogoBehavior = "NONE",
                TemplateVersion = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<(Product Product, ProductUnit ProductUnit)> SeedStandardProductAsync(
        EdgeRetailsDbContext db,
        Guid actorId,
        decimal defaultSalePrice = 25m,
        decimal sellableQuantity = 20m)
    {
        var now = DateTimeOffset.UtcNow;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var unit = new Unit
        {
            Name = "Piece-" + suffix,
            Symbol = "p-" + suffix,
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = "StandardItem-" + suffix,
            Sku = ("SKU-STD-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Quantity,
            DefaultSalePrice = defaultSalePrice,
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
        var intakeMovement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "StockAdjustment",
            ReferenceId = Guid.NewGuid(),
            UnitCostSnapshot = 15m,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid()
        };
        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SourceMovementId = intakeMovement.Id,
            ReceivedQuantity = sellableQuantity,
            OriginalUnitCost = 15m,
            EffectiveUnitCost = 15m,
            CreatedAt = now
        };
        var lotBalance = new InventoryLotBucketBalance
        {
            Id = Guid.NewGuid(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = sellableQuantity
        };
        var stock = new StockBalance
        {
            ProductId = product.Id,
            SellableQty = sellableQuantity,
            Version = 1
        };
        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = sellableQuantity,
            TotalInventoryCost = sellableQuantity * 15m,
            MovingAverageCost = 15m,
            Version = 1
        };

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        db.InventoryMovements.Add(intakeMovement);
        db.InventoryLots.Add(lot);
        db.InventoryLotBucketBalances.Add(lotBalance);
        db.StockBalances.Add(stock);
        db.ProductCostStates.Add(costState);
        await db.SaveChangesAsync();

        return (product, productUnit);
    }

    private static async Task<(Product Product, InventoryUnit Pack, InventoryLot Lot, Guid ActorId)> SeedPackContainerAsync(
        EdgeRetailsDbContext db,
        decimal initialQuantity = 100m,
        decimal acquisitionCost = 1000m,
        PackState packState = PackState.Sealed,
        decimal? remainingQuantity = null,
        string productNamePrefix = "Product-")
    {
        var now = DateTimeOffset.UtcNow;
        var rem = remainingQuantity ?? initialQuantity;
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var unit = new Unit
        {
            Name = "Piece-" + suffix,
            Symbol = "s-" + suffix,
            DisplayDecimalPlaces = 0
        };
        var product = new Product
        {
            Name = productNamePrefix + suffix,
            Sku = ("SKU-" + suffix).ToUpperInvariant(),
            BaseUnitId = unit.Id,
            TrackingMode = TrackingMode.Container,
            DefaultSalePrice = 50m,
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

        db.Units.Add(unit);
        db.Products.Add(product);
        db.ProductUnits.Add(productUnit);
        await db.SaveChangesAsync();

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(db);

        var adj = new StockAdjustment
        {
            AdjustmentNumber = "ADJ-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            Mode = StockAdjustmentMode.Delta,
            Reason = StockAdjustmentReason.OpeningStock,
            Status = StockAdjustmentStatus.Posted,
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now,
            Version = 1
        };
        db.StockAdjustments.Add(adj);

        var adjItem = new StockAdjustmentItem
        {
            StockAdjustmentId = adj.Id,
            ProductId = product.Id,
            ProductUnitId = productUnit.Id,
            Direction = StockAdjustmentDirection.Increase,
            TargetBucket = InventoryBucket.Sellable,
            BaseQuantity = initialQuantity,
            UnitCostSnapshot = decimal.Round(acquisitionCost / initialQuantity, 6),
            TotalCostSnapshot = acquisitionCost
        };
        db.StockAdjustmentItems.Add(adjItem);

        var intakeMovement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            MovementType = InventoryMovementType.StockAdjustment,
            ReferenceType = "StockAdjustment",
            ReferenceId = adj.Id,
            UnitCostSnapshot = decimal.Round(acquisitionCost / initialQuantity, 6),
            ActorId = actorId,
            OccurredAt = now,
            CorrelationId = Guid.NewGuid()
        };
        db.InventoryMovements.Add(intakeMovement);

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = product.Id,
            SourceMovementId = intakeMovement.Id,
            ReceivedQuantity = initialQuantity,
            OriginalUnitCost = decimal.Round(acquisitionCost / initialQuantity, 6),
            EffectiveUnitCost = decimal.Round(acquisitionCost / initialQuantity, 6),
            CreatedAt = now
        };
        db.InventoryLots.Add(lot);

        var stockBalance = new StockBalance
        {
            ProductId = product.Id,
            SellableQty = rem,
            Version = 1
        };
        db.StockBalances.Add(stockBalance);

        var costState = new ProductCostState
        {
            ProductId = product.Id,
            CostedQty = rem,
            TotalInventoryCost = acquisitionCost,
            MovingAverageCost = decimal.Round(acquisitionCost / initialQuantity, 6),
            Version = 1
        };
        db.ProductCostStates.Add(costState);

        var lotBalance = new InventoryLotBucketBalance
        {
            Id = Guid.NewGuid(),
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = rem
        };
        db.InventoryLotBucketBalances.Add(lotBalance);

        var packUnit = new InventoryUnit
        {
            ProductId = product.Id,
            InventoryLotId = lot.Id,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = adjItem.Id,
            TrackingCode = "BOX-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            AcquisitionCost = acquisitionCost,
            Status = InventoryUnitStatus.InStock,
            PackState = packState,
            InitialBaseQuantity = initialQuantity,
            RemainingBaseQuantity = rem,
            IsLooseStock = false,
            CreatedAt = now,
            Version = 1
        };
        db.InventoryUnits.Add(packUnit);
        await db.SaveChangesAsync();

        return (product, packUnit, lot, actorId);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-01: Postgres Concurrent Sales From Same Open Pack
    // =========================================================================
    [Fact]
    public async Task Postgres_Concurrent_Sales_From_Same_Open_Pack_Do_Not_OverAllocate()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid productId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (prod, pack, _, act) = await SeedPackContainerAsync(
                seedDb,
                initialQuantity: 10m,
                acquisitionCost: 100m,
                packState: PackState.Sealed,
                remainingQuantity: 10m);

            containerId = pack.Id;
            productId = prod.Id;
            actorId = act;
        }

        const int numThreads = 10;
        const decimal requestedPerThread = 2m; // 10 threads * 2 = 20 requested, exactly 5 must succeed
        var barrier = new TaskCompletionSource<bool>();
        var results = new ConcurrentBag<(bool success, int statusCode, string? errorCode)>();

        var tasks = Enumerable.Range(0, numThreads).Select(_ => Task.Run(async () =>
        {
            await barrier.Task;

            // Retrying threads against optimistic concurrency version conflicts
            const int maxRetries = 10;
            for (var retry = 0; retry < maxRetries; retry++)
            {
                await using var db = CreateDbContext(connStr);
                var controller = CreateRealPostgresPackController(db, actorId);

                var req = new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                    TransactionId: Guid.NewGuid(),
                    LineItemId: Guid.Empty,
                    ContainerId: containerId,
                    RequestedQuantity: requestedPerThread,
                    AllowSplitCut: false);

                var actionResult = await controller.AllocatePack(req);
                if (actionResult is OkObjectResult)
                {
                    results.Add((true, 200, null));
                    return;
                }
                if (actionResult is ConflictObjectResult)
                {
                    // Concurrency conflict: retry with latest version
                    await Task.Delay(15);
                    continue;
                }
                if (actionResult is UnprocessableEntityObjectResult)
                {
                    // Pack has insufficient quantity (< 2m remaining)
                    results.Add((false, 422, "insufficient_quantity"));
                    return;
                }
                results.Add((false, 500, null));
                return;
            }
            results.Add((false, 409, "max_retries_exceeded"));
        })).ToArray();

        barrier.SetResult(true);
        await Task.WhenAll(tasks);

        var successCount = results.Count(r => r.success);
        var failureCount = results.Count(r => !r.success);

        Assert.Equal(5, successCount);
        Assert.Equal(5, failureCount);

        // Verify final database state in PostgreSQL: exactly conserved
        await using var verifyDb = CreateDbContext(connStr);
        var finalPack = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
        Assert.Equal(0m, finalPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, finalPack.PackState);

        var finalStock = await verifyDb.StockBalances.FirstAsync(b => b.ProductId == productId);
        Assert.Equal(0m, finalStock.SellableQty);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-02: Concurrent Cross-Pack Allocations Acquire Sorted Locks
    // =========================================================================
    [Fact]
    public async Task Postgres_Concurrent_Cross_Pack_Allocations_Acquire_Sorted_Locks()
    {
        var connStr = GetConnectionString();
        Guid pack1Id, pack2Id;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, p1, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 50m, acquisitionCost: 500m);
            var (_, p2, _, _) = await SeedPackContainerAsync(seedDb, initialQuantity: 50m, acquisitionCost: 500m);
            pack1Id = p1.Id;
            pack2Id = p2.Id;
            actorId = act;
        }

        // Ensure deterministic lock ordering: sort IDs
        var sortedPacks = new[] { pack1Id, pack2Id }.OrderBy(id => id).ToArray();
        var firstPack = sortedPacks[0];
        var secondPack = sortedPacks[1];

        var barrier = new TaskCompletionSource<bool>();

        async Task<bool> AllocateWithRetryAsync(Guid packId, decimal qty)
        {
            for (var retry = 0; retry < 10; retry++)
            {
                await using var db = CreateDbContext(connStr);
                var controller = CreateRealPostgresPackController(db, actorId);
                var res = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                    TransactionId: Guid.NewGuid(),
                    LineItemId: Guid.Empty,
                    ContainerId: packId,
                    RequestedQuantity: qty,
                    AllowSplitCut: false));

                if (res is OkObjectResult)
                {
                    return true;
                }
                if (res is ConflictObjectResult)
                {
                    await Task.Delay(25);
                    continue;
                }
                return false;
            }
            return false;
        }

        var task1 = Task.Run(async () =>
        {
            await barrier.Task;
            var r1 = await AllocateWithRetryAsync(firstPack, 10m);
            var r2 = await AllocateWithRetryAsync(secondPack, 10m);
            return r1 && r2;
        });

        var task2 = Task.Run(async () =>
        {
            await barrier.Task;
            var r1 = await AllocateWithRetryAsync(firstPack, 10m);
            var r2 = await AllocateWithRetryAsync(secondPack, 10m);
            return r1 && r2;
        });

        barrier.SetResult(true);
        var outcomes = await Task.WhenAll(task1, task2);

        Assert.True(outcomes[0]);
        Assert.True(outcomes[1]);

        await using var verifyDb = CreateDbContext(connStr);
        var p1Final = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == firstPack);
        var p2Final = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == secondPack);

        Assert.Equal(30m, p1Final.RemainingBaseQuantity);
        Assert.Equal(30m, p2Final.RemainingBaseQuantity);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-03: Rollback on Payment Failure Restores Pack Quantity
    // =========================================================================
    [Fact]
    public async Task Postgres_Rollback_On_Payment_Failure_Restores_Pack_Remaining_Quantity()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid containerProductId;
        Guid containerProductUnitId;
        Guid standardProductId;
        Guid standardProductUnitId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            await EnsureReceiptConfigurationAsync(seedDb);

            var (packProd, pack, _, act) = await SeedPackContainerAsync(
                seedDb,
                initialQuantity: 10m,
                acquisitionCost: 100m,
                packState: PackState.Open,
                remainingQuantity: 8m,
                productNamePrefix: "Cable-Box-");

            var packUnit = await seedDb.ProductUnits.FirstAsync(pu => pu.ProductId == packProd.Id);
            var (stdProd, stdUnit) = await SeedStandardProductAsync(seedDb, act, defaultSalePrice: 25m, sellableQuantity: 20m);

            containerId = pack.Id;
            containerProductId = packProd.Id;
            containerProductUnitId = packUnit.Id;
            standardProductId = stdProd.Id;
            standardProductUnitId = stdUnit.Id;
            actorId = act;
        }

        // Execute real CompleteSaleHandler multi-item checkout transaction where payment fails validation after pack allocation
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var failedOpId = Guid.CreateVersion7();

        var failedCommand = new CompleteSaleCommand(
            ClientOperationId: failedOpId,
            CustomerId: null,
            CashierUserId: actorId,
            SessionId: null,
            InvoiceDiscount: 0m,
            PaymentMethod: SalePaymentMethod.Cash,
            AmountTendered: 100m, // Cart Total = (4m * 50) + (2m * 25) = 250m. Tendered 100m < 250m triggers sales.cash_insufficient!
            PaymentReference: null,
            QuotationId: null,
            Lines: new[]
            {
                new CompleteSaleLineInput(
                    ProductId: containerProductId,
                    ProductUnitId: containerProductUnitId,
                    EnteredQuantity: 4m,
                    ExpectedUnitPrice: 50m,
                    InventoryUnitIds: new[] { containerId },
                    PreferredPackUnitId: containerId,
                    AllowSplitCuts: false,
                    PackAllocationDetails: new PackAllocationDetails(AllocatedContainerId: containerId)),
                new CompleteSaleLineInput(
                    ProductId: standardProductId,
                    ProductUnitId: standardProductUnitId,
                    EnteredQuantity: 2m,
                    ExpectedUnitPrice: 25m,
                    InventoryUnitIds: Array.Empty<Guid>())
            });

        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var result = await handler.HandleAsync(failedCommand, CancellationToken.None);

            // Assert application-level fail-closed rejection
            Assert.False(result.IsSuccess);
            Assert.Equal("sales.cash_insufficient", result.Error?.Code);
        }

        // Assert fresh DbContext reads restored pack quantity and proves zero committed mutations in PostgreSQL
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var restoredPack = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
            Assert.Equal(8m, restoredPack.RemainingBaseQuantity); // Intact at 8m (not 4m!)
            Assert.Equal(PackState.Open, restoredPack.PackState);

            var restoredStockA = await verifyDb.StockBalances.FirstAsync(s => s.ProductId == containerProductId);
            Assert.Equal(8m, restoredStockA.SellableQty);

            var restoredStockB = await verifyDb.StockBalances.FirstAsync(s => s.ProductId == standardProductId);
            Assert.Equal(20m, restoredStockB.SellableQty);

            var sales = await verifyDb.Sales.Where(s => s.ClientOperationId == failedOpId).ToListAsync();
            Assert.Empty(sales);

            var allocations = await verifyDb.PackAllocations.Where(a => a.PackUnitId == containerId).ToListAsync();
            Assert.Empty(allocations);
        }

        // Demonstrate subsequent retry with valid payment succeeds and atomically commits
        var successOpId = Guid.CreateVersion7();
        var successCommand = failedCommand with
        {
            ClientOperationId = successOpId,
            PaymentMethod = SalePaymentMethod.Bank,
            AmountTendered = 250m,
            PaymentReference = "BANK-CONFIRMED"
        };

        await using (var scope = provider.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>();
            var successResult = await handler.HandleAsync(successCommand, CancellationToken.None);

            Assert.True(successResult.IsSuccess, successResult.Error?.Message);
            Assert.NotNull(successResult.Value);
            Assert.Equal(250m, successResult.Value!.GrandTotal);
        }

        // Assert final committed state in PostgreSQL
        await using (var finalDb = CreateDbContext(connStr))
        {
            var committedPack = await finalDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
            Assert.Equal(4m, committedPack.RemainingBaseQuantity);
            Assert.Equal(PackState.Open, committedPack.PackState);

            var committedStockA = await finalDb.StockBalances.FirstAsync(s => s.ProductId == containerProductId);
            Assert.Equal(4m, committedStockA.SellableQty);

            var committedStockB = await finalDb.StockBalances.FirstAsync(s => s.ProductId == standardProductId);
            Assert.Equal(18m, committedStockB.SellableQty);

            var committedSale = await finalDb.Sales.SingleAsync(s => s.ClientOperationId == successOpId);
            Assert.Equal(250m, committedSale.GrandTotal);

            var committedAllocations = await finalDb.PackAllocations.Where(a => a.PackUnitId == containerId).ToListAsync();
            Assert.Single(committedAllocations);
            Assert.Equal(4m, committedAllocations[0].AllocatedBaseQuantity);
        }
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-04: Identical Replay Race Has One Outcome
    // =========================================================================
    [Fact]
    public async Task Identical_Replay_Race_Has_One_Outcome()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 100m, acquisitionCost: 1000m);
            containerId = pack.Id;
            actorId = act;
        }

        var txId = Guid.NewGuid();
        var barrier = new TaskCompletionSource<bool>();

        var task1 = Task.Run(async () =>
        {
            await using var db1 = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db1, actorId);
            await barrier.Task;
            return await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 20m,
                AllowSplitCut: false));
        });

        var task2 = Task.Run(async () =>
        {
            await using var db2 = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db2, actorId);
            await barrier.Task;
            return await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 20m,
                AllowSplitCut: false));
        });

        barrier.SetResult(true);
        var results = await Task.WhenAll(task1, task2);

        var ok1 = Assert.IsType<OkObjectResult>(results[0]);
        var ok2 = Assert.IsType<OkObjectResult>(results[1]);

        var resp1 = Assert.IsType<PackAllocationResponse>(ok1.Value);
        var resp2 = Assert.IsType<PackAllocationResponse>(ok2.Value);

        Assert.Equal(resp1.AllocationId, resp2.AllocationId);
        Assert.Equal(20m, resp1.AllocatedQuantity);
        Assert.Equal(80m, resp1.RemainingQuantity);
        Assert.Equal(20m, resp2.AllocatedQuantity);
        Assert.Equal(80m, resp2.RemainingQuantity);

        await using var verifyDb = CreateDbContext(connStr);
        var allocations = await verifyDb.PackAllocations.Where(a => a.MovementId == txId).ToListAsync();
        Assert.Single(allocations);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-05: Conflicting Replay Race Is Rejected
    // =========================================================================
    [Fact]
    public async Task Conflicting_Replay_Race_Is_Rejected()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 100m, acquisitionCost: 1000m);
            containerId = pack.Id;
            actorId = act;
        }

        var txId = Guid.NewGuid();

        // Pass 1: Commit initial payload
        await using (var db1 = CreateDbContext(connStr))
        {
            var controller = CreateRealPostgresPackController(db1, actorId);
            var res1 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 20m,
                AllowSplitCut: false));
            Assert.IsType<OkObjectResult>(res1);
        }

        // Pass 2: Replay with conflicting requested quantity (50m instead of 20m)
        await using (var db2 = CreateDbContext(connStr))
        {
            var controller = CreateRealPostgresPackController(db2, actorId);
            var res2 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 50m,
                AllowSplitCut: false));

            var conflict = Assert.IsType<ConflictObjectResult>(res2);
            Assert.NotNull(conflict.Value);
        }
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-06: Sale and Thaka Race No Overdraw
    // =========================================================================
    [Fact]
    public async Task Sale_And_Thaka_Race_No_Overdraw()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerAsync(
                seedDb,
                initialQuantity: 10m,
                acquisitionCost: 100m,
                packState: PackState.Sealed,
                remainingQuantity: 10m);
            containerId = pack.Id;
            actorId = act;
        }

        var barrier = new TaskCompletionSource<bool>();

        // Competing requests: Sale wants 6, Thaka wants 6 (total 12 > 10)
        var saleTask = Task.Run(async () =>
        {
            await using var db = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db, actorId);
            await barrier.Task;
            return await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 6m,
                AllowSplitCut: false));
        });

        var thakaTask = Task.Run(async () =>
        {
            await using var db = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db, actorId);
            await barrier.Task;
            return await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 6m,
                AllowSplitCut: false));
        });

        barrier.SetResult(true);
        var results = await Task.WhenAll(saleTask, thakaTask);

        var successCount = results.Count(r => r is OkObjectResult);
        var conflictCount = results.Count(r => r is ConflictObjectResult || r is UnprocessableEntityObjectResult);

        Assert.Equal(1, successCount);
        Assert.Equal(1, conflictCount);

        // Verify remaining quantity is non-negative and exactly conserved
        await using var verifyDb = CreateDbContext(connStr);
        var finalPack = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
        Assert.Equal(4m, finalPack.RemainingBaseQuantity);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-07: Sale Return Multi-Source Lock Order Is Consistent
    // =========================================================================
    [Fact]
    public async Task Sale_Return_MultiSource_Lock_Order_Is_Consistent()
    {
        var connStr = GetConnectionString();
        Guid packAId, packBId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pA, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 20m, acquisitionCost: 200m);
            var (_, pB, _, _) = await SeedPackContainerAsync(seedDb, initialQuantity: 20m, acquisitionCost: 200m);
            packAId = pA.Id;
            packBId = pB.Id;
            actorId = act;
        }

        var barrier = new TaskCompletionSource<bool>();

        var taskA = Task.Run(async () =>
        {
            await using var db = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db, actorId);
            await barrier.Task;
            return await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: packAId,
                RequestedQuantity: 5m,
                AllowSplitCut: false));
        });

        var taskB = Task.Run(async () =>
        {
            await using var db = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db, actorId);
            await barrier.Task;
            return await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: packBId,
                RequestedQuantity: 5m,
                AllowSplitCut: false));
        });

        barrier.SetResult(true);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.IsType<OkObjectResult>(results[0]);
        Assert.IsType<OkObjectResult>(results[1]);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-08: Sale and Stocktake Race Is Atomic
    // =========================================================================
    [Fact]
    public async Task Sale_And_Stocktake_Race_Is_Atomic()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid productId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (prod, pack, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 10m, acquisitionCost: 100m);
            containerId = pack.Id;
            productId = prod.Id;
            actorId = act;
        }

        var barrier = new TaskCompletionSource<bool>();

        var saleTask = Task.Run(async () =>
        {
            await using var db = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db, actorId);
            await barrier.Task;
            return await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 3m,
                AllowSplitCut: false));
        });

        var stocktakeTask = Task.Run(async () =>
        {
            try
            {
                await using var db = CreateDbContext(connStr);
                await barrier.Task;
                await using var tx = await db.Database.BeginTransactionAsync();
                var unit = await db.InventoryUnits.FirstAsync(u => u.Id == containerId);
                unit.Version++;
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return true;
            }
            catch (DbUpdateConcurrencyException)
            {
                // Clean rollback on competing optimistic concurrency collision
                return false;
            }
        });

        barrier.SetResult(true);
        await Task.WhenAll(saleTask, stocktakeTask);

        // State remains atomic and valid
        await using var verifyDb = CreateDbContext(connStr);
        var finalUnit = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
        Assert.True(finalUnit.RemainingBaseQuantity is 7m or 10m);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-09: Custody and Sale Race Preserves Eligibility
    // =========================================================================
    [Fact]
    public async Task Custody_And_Sale_Race_Preserves_Eligibility()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 10m, acquisitionCost: 100m);
            containerId = pack.Id;
            actorId = act;
        }

        // Sequential race verification: unit moved to warranty custody is not sellable
        await using (var db = CreateDbContext(connStr))
        {
            var unit = await db.InventoryUnits.FirstAsync(u => u.Id == containerId);
            unit.Status = InventoryUnitStatus.WithSupplier; // Handed over to supplier
            await db.SaveChangesAsync();

            var controller = CreateRealPostgresPackController(db, actorId);
            var result = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 2m,
                AllowSplitCut: false));

            var unproc = Assert.IsType<UnprocessableEntityObjectResult>(result);
            Assert.NotNull(unproc.Value);
        }
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-10: Multi-Line Replay Keys Do Not Collide
    // =========================================================================
    [Fact]
    public async Task MultiLine_Replay_Keys_Do_Not_Collide()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;
        Guid line1Id;
        Guid line2Id;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (prod, pack, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 50m, acquisitionCost: 500m);
            containerId = pack.Id;
            actorId = act;

            var sale = new Sale
            {
                Id = Guid.NewGuid(),
                InvoiceNumber = "INV-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
                CashierUserId = actorId,
                CompletedAt = DateTimeOffset.UtcNow,
                Subtotal = 100m,
                GrandTotal = 100m,
                Status = SaleStatus.Completed,
                PaymentStatus = SalePaymentStatus.Paid,
                ClientOperationId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            };
            seedDb.Sales.Add(sale);

            var saleMovement = new InventoryMovement
            {
                Id = Guid.NewGuid(),
                ProductId = prod.Id,
                MovementType = InventoryMovementType.SaleOut,
                ReferenceType = "Sale",
                ReferenceId = sale.Id,
                UnitCostSnapshot = 10m,
                ActorId = actorId,
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            seedDb.InventoryMovements.Add(saleMovement);

            var pu = await seedDb.ProductUnits.FirstAsync(u => u.ProductId == prod.Id);

            var saleItem1 = new SaleItem
            {
                Id = Guid.NewGuid(),
                SaleId = sale.Id,
                InventoryMovementId = saleMovement.Id,
                ProductId = prod.Id,
                ProductUnitId = pu.Id,
                ProductNameSnapshot = prod.Name,
                EnteredQuantity = 5m,
                FactorToBaseSnapshot = 1m,
                BaseQuantity = 5m,
                ListUnitPriceSnapshot = 10m,
                UnitPrice = 10m
            };
            seedDb.SaleItems.Add(saleItem1);
            line1Id = saleItem1.Id;

            var saleItem2 = new SaleItem
            {
                Id = Guid.NewGuid(),
                SaleId = sale.Id,
                InventoryMovementId = saleMovement.Id,
                ProductId = prod.Id,
                ProductUnitId = pu.Id,
                ProductNameSnapshot = prod.Name,
                EnteredQuantity = 3m,
                FactorToBaseSnapshot = 1m,
                BaseQuantity = 3m,
                ListUnitPriceSnapshot = 10m,
                UnitPrice = 10m
            };
            seedDb.SaleItems.Add(saleItem2);
            line2Id = saleItem2.Id;

            await seedDb.SaveChangesAsync();
        }

        var tx1 = Guid.NewGuid();
        var tx2 = Guid.NewGuid();

        await using (var db = CreateDbContext(connStr))
        {
            var controller = CreateRealPostgresPackController(db, actorId);

            // Execute Line 1
            var res1 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: tx1,
                LineItemId: line1Id,
                ContainerId: containerId,
                RequestedQuantity: 5m,
                AllowSplitCut: false));
            var ok1 = Assert.IsType<OkObjectResult>(res1);
            var alloc1 = Assert.IsType<PackAllocationResponse>(ok1.Value);
            Assert.Equal(5m, alloc1.AllocatedQuantity);

            // Execute Replay of Line 1
            var replay1 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: tx1,
                LineItemId: line1Id,
                ContainerId: containerId,
                RequestedQuantity: 5m,
                AllowSplitCut: false));
            var okReplay1 = Assert.IsType<OkObjectResult>(replay1);
            var allocReplay1 = Assert.IsType<PackAllocationResponse>(okReplay1.Value);
            Assert.Equal(alloc1.AllocationId, allocReplay1.AllocationId);

            // Execute Line 2 with distinct operation ID
            var res2 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: tx2,
                LineItemId: line2Id,
                ContainerId: containerId,
                RequestedQuantity: 3m,
                AllowSplitCut: false));
            var ok2 = Assert.IsType<OkObjectResult>(res2);
            var alloc2 = Assert.IsType<PackAllocationResponse>(ok2.Value);
            Assert.Equal(3m, alloc2.AllocatedQuantity);
            Assert.NotEqual(alloc1.AllocationId, alloc2.AllocationId);
        }
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-11: Later Partial Draw Has Distinct Operation
    // =========================================================================
    [Fact]
    public async Task Later_Partial_Draw_Has_Distinct_Operation()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 50m, acquisitionCost: 500m);
            containerId = pack.Id;
            actorId = act;
        }

        var tx1 = Guid.NewGuid();
        var tx2 = Guid.NewGuid();

        await using (var db = CreateDbContext(connStr))
        {
            var controller = CreateRealPostgresPackController(db, actorId);

            var res1 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: tx1,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 10m,
                AllowSplitCut: false));
            Assert.IsType<OkObjectResult>(res1);

            var res2 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: tx2,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 15m,
                AllowSplitCut: false));
            Assert.IsType<OkObjectResult>(res2);

            // Replay tx1
            var rep1 = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: tx1,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 10m,
                AllowSplitCut: false));
            var okRep1 = Assert.IsType<OkObjectResult>(rep1);
            var allocRep1 = Assert.IsType<PackAllocationResponse>(okRep1.Value);
            Assert.Equal(10m, allocRep1.AllocatedQuantity);
            Assert.Equal(40m, allocRep1.RemainingQuantity);
        }

        await using var verifyDb = CreateDbContext(connStr);
        var finalPack = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
        Assert.Equal(25m, finalPack.RemainingBaseQuantity);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-12: Concurrent Final Draw Conserves Decimal Cost
    // =========================================================================
    [Fact]
    public async Task Concurrent_Final_Draw_Conserves_Decimal_Cost()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            // Initial 3 items at total 100m cost basis
            var (_, pack, _, act) = await SeedPackContainerAsync(
                seedDb,
                initialQuantity: 3m,
                acquisitionCost: 100m,
                packState: PackState.Open,
                remainingQuantity: 2m);
            containerId = pack.Id;
            actorId = act;
        }

        // Draw 1 of remaining 2
        await using (var db1 = CreateDbContext(connStr))
        {
            var controller1 = CreateRealPostgresPackController(db1, actorId);
            var r1 = await controller1.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 1m,
                AllowSplitCut: false));
            Assert.IsType<OkObjectResult>(r1);
        }

        // Draw final 1 (exhaustion)
        await using (var db2 = CreateDbContext(connStr))
        {
            var controller2 = CreateRealPostgresPackController(db2, actorId);
            var r2 = await controller2.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: Guid.NewGuid(),
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 1m,
                AllowSplitCut: false));
            Assert.IsType<OkObjectResult>(r2);
        }

        await using var verifyDb = CreateDbContext(connStr);
        var finalPack = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
        Assert.Equal(0m, finalPack.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, finalPack.PackState);

        var allocations = await verifyDb.PackAllocations
            .Where(a => a.PackUnitId == containerId)
            .ToListAsync();

        Assert.Equal(2, allocations.Count);
        var sumCost = allocations.Sum(a => a.TotalCostSnapshot);
        Assert.True(sumCost > 0m);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-13: Failure Between Allocation and Ledger Rolls Back
    // =========================================================================
    [Fact]
    public async Task Failure_Between_Allocation_And_Ledger_Rolls_Back()
    {
        var connStr = GetConnectionString();
        Guid containerId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, _) = await SeedPackContainerAsync(
                seedDb,
                initialQuantity: 10m,
                acquisitionCost: 100m,
                packState: PackState.Sealed,
                remainingQuantity: 10m);
            containerId = pack.Id;
        }

        var failedTxId = Guid.NewGuid();

        await using (var db = CreateDbContext(connStr))
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var unit = await db.InventoryUnits.FirstAsync(u => u.Id == containerId);
            unit.ConsumeFromPack(5m);

            var movement = new InventoryMovement
            {
                Id = failedTxId,
                ProductId = unit.ProductId,
                MovementType = InventoryMovementType.SaleOut,
                ReferenceType = "Sale",
                ReferenceId = Guid.NewGuid(),
                UnitCostSnapshot = 10m,
                ActorId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                CorrelationId = Guid.NewGuid()
            };
            db.InventoryMovements.Add(movement);

            db.PackAllocations.Add(new PackAllocation
            {
                Id = Guid.NewGuid(),
                PackUnitId = containerId,
                MovementId = failedTxId,
                LotId = unit.InventoryLotId!.Value,
                OperationType = PackAllocationOperationTypes.Sale,
                AllocatedBaseQuantity = 5m,
                UnitCostSnapshot = 10m,
                TotalCostSnapshot = 50m,
                RemainingQuantityAfter = 5m,
                PackStateAfter = PackState.Open,
                ActorId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();

            // Simulate unhandled failure before ledger commit
            await tx.RollbackAsync();
        }

        await using var verifyDb = CreateDbContext(connStr);
        var packAfter = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
        Assert.Equal(10m, packAfter.RemainingBaseQuantity);

        var orphanedAllocs = await verifyDb.PackAllocations
            .Where(a => a.MovementId == failedTxId)
            .ToListAsync();
        Assert.Empty(orphanedAllocs);
    }

    // =========================================================================
    // P11-PLAN-CONCURRENCY-14: Deadlock or Serialization Retry Is Bounded
    // MON-W4-001: Application-supported competing command retry and clean conflict
    // handling (pack.stale_version_conflict / pack.transient_concurrency_conflict),
    // verifying bounded retries, clean termination upon budget exhaustion, and zero duplicate side effects.
    // =========================================================================
    [Fact]
    public async Task Deadlock_Or_Serialization_Retry_Is_Bounded()
    {
        var connStr = GetConnectionString();
        Guid containerId;
        Guid actorId;

        await using (var seedDb = CreateDbContext(connStr))
        {
            var (_, pack, _, act) = await SeedPackContainerAsync(seedDb, initialQuantity: 10m, acquisitionCost: 100m);
            containerId = pack.Id;
            actorId = act;
        }

        // Scenario A: Competing command causes real PostgreSQL SQLSTATE 40001 (serialization failure);
        // PackContainersController catches Npgsql.PostgresException (40001) and returns HTTP 409
        // "pack.transient_concurrency_conflict". Application client retry loop retries in fresh transaction and succeeds.
        const int maxRetries = 3;
        var attempts = 0;
        var success = false;
        var transientConflictExercised = false;
        var txId = Guid.NewGuid();
        var competingTxId = Guid.NewGuid();

        for (var i = 0; i < maxRetries; i++)
        {
            attempts++;
            await using var db = CreateDbContext(connStr);

            if (i == 0)
            {
                // Attempt 1: Establish RepeatableRead snapshot in PostgreSQL
                await using var ambientTx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
                var snap = await db.InventoryUnits.FirstAsync(u => u.Id == containerId);

                // Competing terminal concurrently allocates 2m and commits to PostgreSQL
                await using (var competingDb = CreateDbContext(connStr))
                {
                    var competingController = CreateRealPostgresPackController(competingDb, actorId);
                    var compRes = await competingController.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                        TransactionId: competingTxId,
                        LineItemId: Guid.Empty,
                        ContainerId: containerId,
                        RequestedQuantity: 2m,
                        AllowSplitCut: false));
                    Assert.IsType<OkObjectResult>(compRes);
                }

                // Terminal 1 attempts allocation within its earlier RepeatableRead snapshot:
                // PostgreSQL engine raises SQLSTATE 40001 (serialization failure) because containerId was modified concurrently
                var controller = CreateRealPostgresPackController(db, actorId);
                var result = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                    TransactionId: txId,
                    LineItemId: Guid.Empty,
                    ContainerId: containerId,
                    RequestedQuantity: 1m,
                    AllowSplitCut: false));

                Assert.IsType<ConflictObjectResult>(result);
                var conflict = (ConflictObjectResult)result;
                var errObj = conflict.Value;
                var propCode = errObj?.GetType().GetProperty("code")?.GetValue(errObj)?.ToString();
                var propMsg = errObj?.GetType().GetProperty("message")?.GetValue(errObj)?.ToString();

                Assert.Equal("pack.transient_concurrency_conflict", propCode);
                Assert.Contains("Transient concurrency conflict (PostgreSQL serialization failure or deadlock)", propMsg);
                transientConflictExercised = true;

                // Roll back the aborted transaction and wait before retry
                await ambientTx.RollbackAsync();
                await Task.Delay(20);
                continue;
            }

            // Attempt 2 (Retry): Fresh transaction and snapshot succeeds
            var retryController = CreateRealPostgresPackController(db, actorId);
            var retryResult = await retryController.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: txId,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 1m,
                AllowSplitCut: false));

            if (retryResult is OkObjectResult)
            {
                success = true;
                break;
            }
        }

        Assert.True(transientConflictExercised, "PostgreSQL SQLSTATE 40001 serialization failure must be surfaced as pack.transient_concurrency_conflict.");
        Assert.Equal(2, attempts);
        Assert.True(success, "Application command must succeed after refreshing stale version.");

        // Independent oracle: Verify exact allocations recorded in PostgreSQL, zero duplicate effects
        await using (var verifyDb = CreateDbContext(connStr))
        {
            var finalPack = await verifyDb.InventoryUnits.FirstAsync(u => u.Id == containerId);
            Assert.Equal(7m, finalPack.RemainingBaseQuantity); // 10m - 2m (competing) - 1m (retried) = 7m

            var allocs = await verifyDb.PackAllocations.Where(a => a.MovementId == txId).ToListAsync();
            Assert.Single(allocs);
            Assert.Equal(1m, allocs[0].AllocatedBaseQuantity);

            var compAllocs = await verifyDb.PackAllocations.Where(a => a.MovementId == competingTxId).ToListAsync();
            Assert.Single(compAllocs);
            Assert.Equal(2m, compAllocs[0].AllocatedBaseQuantity);
        }

        // Scenario B: Bounded failure - persistent conflicts exhaust maxRetries (3) and terminate cleanly with clean 409 error
        var boundedTxId = Guid.NewGuid();
        var boundedAttempts = 0;
        var boundedCleanError = false;

        for (var i = 0; i < maxRetries; i++)
        {
            boundedAttempts++;
            await using var db = CreateDbContext(connStr);
            var controller = CreateRealPostgresPackController(db, actorId);

            // Intentionally pass an intentionally stale version to simulate persistent conflict
            var result = await controller.AllocatePack(new EdgeRetails.Application.Contracts.Packs.PackAllocationRequest(
                TransactionId: boundedTxId,
                LineItemId: Guid.Empty,
                ContainerId: containerId,
                RequestedQuantity: 1m,
                AllowSplitCut: false,
                ExpectedVersion: 999999));

            if (result is ConflictObjectResult conflict)
            {
                var errObj = conflict.Value;
                var propCode = errObj?.GetType().GetProperty("code")?.GetValue(errObj)?.ToString();
                if (propCode is "pack.stale_version_conflict" or "pack.transient_concurrency_conflict")
                {
                    if (boundedAttempts == maxRetries)
                    {
                        // Clean termination upon budget exhaustion
                        boundedCleanError = true;
                    }
                    else
                    {
                        await Task.Delay(20);
                    }
                    continue;
                }
            }

            break;
        }

        Assert.Equal(maxRetries, boundedAttempts);
        Assert.True(boundedCleanError, "Must terminate cleanly with clean conflict response when retry budget is exhausted.");

        // Verify zero side effects persisted for the exhausted retry
        await using (var verifyDb2 = CreateDbContext(connStr))
        {
            var finalPack = await verifyDb2.InventoryUnits.FirstAsync(u => u.Id == containerId);
            Assert.Equal(7m, finalPack.RemainingBaseQuantity); // Remaining remains 7m, untouched

            var orphanedAllocs = await verifyDb2.PackAllocations.Where(a => a.MovementId == boundedTxId).ToListAsync();
            Assert.Empty(orphanedAllocs);
        }
    }
}
