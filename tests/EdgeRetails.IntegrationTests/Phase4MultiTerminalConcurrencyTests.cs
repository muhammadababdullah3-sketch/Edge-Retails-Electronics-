using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Application.Production.Licensing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase4MultiTerminalConcurrencyTests
{
    #region Test Provider & License Double Infrastructure

    private static ServiceProvider BuildProvider(int maxTerminals = 2)
    {
        var connectionString = Environment.GetEnvironmentVariable("EDGE_RETAILS_TEST_DB");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "EDGE_RETAILS_TEST_DB must point to an isolated PostgreSQL integration-test database.");
        }

        var services = new ServiceCollection();
        services.AddEdgeRetailsInfrastructure(connectionString);

        var licenseStore = new TestLicenseStore();
        var licenseValidator = new TestLicenseValidator(maxTerminals);
        var licenseService = new RuntimeLicenseService(licenseStore, licenseValidator);
        services.AddSingleton(licenseService);

        return services.BuildServiceProvider();
    }

    private sealed class TestLicenseStore : ILicenseStore
    {
        public Task<bool> ExistsAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<string?> ReadRawAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("{}");
        public Task<bool> TryPersistInitialRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task PersistRawAsync(string signedLicenseJson, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestLicenseValidator : ILicenseValidator
    {
        private readonly int _maxTerminals;
        public TestLicenseValidator(int maxTerminals) => _maxTerminals = maxTerminals;

        public Task<LicenseValidationResult> ValidateAsync(string signedLicenseJson, CancellationToken cancellationToken = default)
        {
            var payload = new LicensePayload(
                LicenseId: "LIC-PHASE4-TEST",
                CustomerName: "Edge Retails Concurrency Store",
                StoreName: "Multi-Terminal Lab",
                Plan: "Enterprise",
                IssueDate: DateTimeOffset.UtcNow.AddDays(-30),
                ExpiryDate: DateTimeOffset.UtcNow.AddYears(1),
                DeviceId: "DEV-PHASE4-TEST",
                MaxTerminals: _maxTerminals,
                EnabledModules: ["sales", "terminals", "inventory", "purchasing", "finance", "warranty"]);

            return Task.FromResult(new LicenseValidationResult(
                LicenseValidationStatus.Valid,
                payload,
                "License is valid."));
        }
    }

    #endregion

    #region Race 1: Concurrent Terminal Registration Racing Against MaxTerminals Quota

    [Fact]
    public async Task Race01_ConcurrentTerminalRegistration_RacingAgainstMaxTerminalsQuota_AllowsExactQuotaCapacity()
    {
        // Quota is set to exactly 2 terminals. Terminal 1 is already registered and active.
        await using var provider = BuildProvider(maxTerminals: 2);
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var initialTerminalCode = "TERM-INITIAL-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        setupDb.Terminals.Add(new Terminal
        {
            Id = Guid.CreateVersion7(),
            TerminalCode = initialTerminalCode,
            Name = "Initial Counter Terminal",
            Status = TerminalStatus.Active,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        });
        await setupDb.SaveChangesAsync();

        var initialActiveCount = await setupDb.Terminals.CountAsync(t => t.Status == TerminalStatus.Active);
        Assert.Equal(1, initialActiveCount);

        var termCodeA = "TERM-RACE-A-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var termCodeB = "TERM-RACE-B-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var barrier = new Barrier(2);

        // Terminal A attempts to register
        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<RegisterTerminalHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new RegisterTerminalCommand(
                    termCodeA,
                    "Terminal A Registration",
                    "HW-FINGERPRINT-A",
                    TerminalProtocol.CurrentProtocolVersion,
                    "192.168.1.101",
                    "SecretA123"),
                CancellationToken.None);
        });

        // Terminal B attempts to register concurrently
        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<RegisterTerminalHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new RegisterTerminalCommand(
                    termCodeB,
                    "Terminal B Registration",
                    "HW-FINGERPRINT-B",
                    TerminalProtocol.CurrentProtocolVersion,
                    "192.168.1.102",
                    "SecretB123"),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        // Exactly one terminal succeeds and one is rejected due to capacity exceeded
        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        var failedResult = results.Single(r => !r.IsSuccess);
        Assert.Equal("terminals.capacity_exceeded", failedResult.Error?.Code);

        // Verify in database: exactly 2 active terminals exist (never 3)
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var totalActive = await verifyDb.Terminals.CountAsync(t => t.Status == TerminalStatus.Active);
        Assert.Equal(2, totalActive);

        var winningCode = results.Single(r => r.IsSuccess).Value!.TerminalCode;
        var winningInDb = await verifyDb.Terminals.SingleOrDefaultAsync(t => t.TerminalCode == winningCode);
        Assert.NotNull(winningInDb);
        Assert.Equal(TerminalStatus.Active, winningInDb.Status);
    }

    #endregion

    #region Race 2: Concurrent Serialized Sales Competing for Last Unit (Exactly 1 Winner)

    [Fact]
    public async Task Race02_ConcurrentSerializedSales_TerminalAAndBCompetingForLastUnit_AllowsExactlyOneWinner()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb);

        var serial = "SN-P4-RACE-" + Guid.NewGuid().ToString("N")[..8];
        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);

        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-P4-SERIAL-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [
                    new CreatePurchaseLineInput(
                        fixture.ProductId,
                        fixture.ProductUnitId,
                        1m,
                        3000m,
                        5000m,
                        [new SerializedIdentityInput(serial)])
                ],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess);

        setupDb.ChangeTracker.Clear();
        var unit = await setupDb.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);

        var barrier = new Barrier(2);

        // Terminal A sale attempt
        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    5000m,
                    "TERMINAL-A",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 5000m, [unit.Id])]),
                CancellationToken.None);
        });

        // Terminal B sale attempt
        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    5000m,
                    "TERMINAL-B",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 5000m, [unit.Id])]),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        // Verify in database: exactly 1 sale item, unit status Sold, sellable balance 0
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var saleItems = await verifyDb.SaleItems.Where(i => i.ProductId == fixture.ProductId).ToListAsync();
        Assert.Single(saleItems);

        var updatedUnit = await verifyDb.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.Sold, updatedUnit.Status);

        var stock = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, stock.SellableQty);
    }

    #endregion

    #region Race 3: Concurrent Quantity Sales Exceeding Lot Balance (Zero Oversell)

    [Fact]
    public async Task Race03_ConcurrentQuantitySales_TerminalAAndBExceedingLotBalance_GuaranteesZeroOversell()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db: setupDb, defaultSalePrice: 100m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-P4-QTY-STOCK",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 5m, 80m, 100m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        var barrier = new Barrier(2);

        // Terminal A attempts to buy 4 units
        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    400m,
                    "TERMINAL-A",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 4m, 100m, [])]),
                CancellationToken.None);
        });

        // Terminal B attempts to buy 4 units concurrently (total 8 > 5)
        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    400m,
                    "TERMINAL-B",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 4m, 100m, [])]),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        var failed = results.Single(r => !r.IsSuccess);
        Assert.Equal("sales.insufficient_stock", failed.Error?.Code);

        // Verify stock never becomes negative and exactly 1 unit remains sellable
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var stock = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(1m, stock.SellableQty);
    }

    #endregion

    #region Race 4: Concurrent Cash Session Mutation vs Close from Another Terminal

    [Fact]
    public async Task Race04_ConcurrentCashSessionMutationVsClose_FromSeparateTerminals_PreservesConsistencyAndSerializesCleanly()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(setupDb);
        var session = await Phase2PostgresTestHarness.SeedOpenCashSessionAsync(setupDb, actorId, openingCash: 10000m);

        var barrier = new Barrier(2);

        // Terminal A attempts to record a cash movement (+500 cash in)
        var mutationTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var movementHandler = ActivatorUtilities.CreateInstance<RecordManualCashMovementHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await movementHandler.HandleAsync(
                new RecordManualCashMovementCommand(
                    CashMovementDirection.In,
                    500m,
                    actorId,
                    "Terminal A Cash Drop",
                    "Race test midday movement"),
                CancellationToken.None);
        });

        // Terminal B attempts to close the cash session
        var closeTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var closeHandler = ActivatorUtilities.CreateInstance<CloseCashSessionHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await closeHandler.HandleAsync(
                new CloseCashSessionCommand(
                    session.Id,
                    10000m,
                    actorId,
                    "Terminal B Shift Close"),
                CancellationToken.None);
        });

        var mutationResult = await mutationTask;
        var closeResult = await closeTask;

        // Close must succeed
        Assert.True(closeResult.IsSuccess, closeResult.Error?.Message);

        // Database verification: session is closed and movements match expectations
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var closedSession = await verifyDb.CashSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(CashSessionStatus.Closed, closedSession.Status);
        Assert.NotNull(closedSession.ClosedAt);

        if (mutationResult.IsSuccess)
        {
            // If mutation committed before close, the movement was recorded and close calculated expected cash accordingly
            var movementExists = await verifyDb.CashMovements.AnyAsync(m => m.CashSessionId == session.Id && m.Amount == 500m);
            Assert.True(movementExists);
            Assert.Equal(10500m, closedSession.ExpectedClosingCash);
        }
        else
        {
            // If close committed before mutation, mutation was rejected because cash session was already closed
            Assert.Equal("cash.session_required", mutationResult.Error?.Code);
        }
    }

    #endregion

    #region Race 5: Concurrent Supplier Khata Payment Exceeding Payable

    [Fact]
    public async Task Race05_ConcurrentSupplierKhataPayment_FromMultipleTerminalsExceedingPayable_GuaranteesNoOverSettlement()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var supplier = await Phase2PostgresTestHarness.SeedSupplierAsync(setupDb, "P4 Khata Supplier");
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(setupDb);

        // Seed 10,000 payable
        setupDb.SupplierAccountEntries.Add(new SupplierAccountEntry
        {
            EntryNumber = "SAE-P4-INIT",
            SupplierId = supplier.Id,
            EntryType = SupplierAccountEntryType.Purchase,
            Direction = SupplierAccountDirection.IncreasePayable,
            Amount = 10000m,
            ReferenceType = "ManualSeed",
            ReferenceId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await setupDb.SaveChangesAsync();

        var barrier = new Barrier(2);

        // Terminal A pays 7,000
        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var paymentHandler = ActivatorUtilities.CreateInstance<CreateSupplierPaymentHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await paymentHandler.HandleAsync(
                new CreateSupplierPaymentCommand(
                    supplier.Id,
                    7000m,
                    SupplierPaymentPurpose.Settlement,
                    SupplierSettlementMethod.External,
                    actorId,
                    Guid.CreateVersion7(),
                    null,
                    "Terminal A settlement"),
                CancellationToken.None);
        });

        // Terminal B pays 7,000 concurrently (total 14,000 > 10,000)
        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var paymentHandler = ActivatorUtilities.CreateInstance<CreateSupplierPaymentHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await paymentHandler.HandleAsync(
                new CreateSupplierPaymentCommand(
                    supplier.Id,
                    7000m,
                    SupplierPaymentPurpose.Settlement,
                    SupplierSettlementMethod.External,
                    actorId,
                    Guid.CreateVersion7(),
                    null,
                    "Terminal B settlement"),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        var failedResult = results.Single(r => !r.IsSuccess);
        Assert.Equal("supplier.payment_exceeds_payable", failedResult.Error?.Code);

        // Final balance in database must be strictly 3,000 (never negative)
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var entries = await verifyDb.SupplierAccountEntries.Where(x => x.SupplierId == supplier.Id).ToListAsync();
        var finalPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(3000m, finalPayable);
    }

    #endregion

    #region Race 6: Concurrent Warranty Claims on Same Serialized Unit

    [Fact]
    public async Task Race06_ConcurrentWarrantyClaims_OnSameSerializedUnitFromTwoTerminals_AllowsExactlyOneClaim()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb, defaultSalePrice: 6000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(setupDb, "P4 Warranty Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(setupScope.ServiceProvider);

        var serial = "SN-P4-WCLAIM-" + Guid.NewGuid().ToString("N")[..8];
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-P4-WCLAIM",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 4000m, 6000m, [new SerializedIdentityInput(serial)])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        setupDb.ChangeTracker.Clear();
        var unit = await setupDb.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);

        var saleResult = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                6000m,
                "BANK-WCLAIM",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 6000m, [unit.Id])]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess);
        var saleId = saleResult.Value!.SaleId;

        setupDb.ChangeTracker.Clear();
        var saleItem = await setupDb.SaleItems.SingleAsync(x => x.SaleId == saleId);

        var barrier = new Barrier(2);

        // Terminal A warranty claim
        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var claimHandler = ActivatorUtilities.CreateInstance<CreateWarrantyClaimHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await claimHandler.HandleAsync(
                new CreateWarrantyClaimCommand(
                    customer.Id,
                    saleId,
                    fixture.SupplierId,
                    fixture.ActorId,
                    [
                        new WarrantyClaimItemInput(
                            fixture.ProductId,
                            1m,
                            "Terminal A failure report",
                            saleItem.Id,
                            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                            [new WarrantyClaimUnitInput(unit.Id, serial)])
                    ], Guid.CreateVersion7()),
                CancellationToken.None);
        });

        // Terminal B warranty claim concurrently
        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var claimHandler = ActivatorUtilities.CreateInstance<CreateWarrantyClaimHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await claimHandler.HandleAsync(
                new CreateWarrantyClaimCommand(
                    customer.Id,
                    saleId,
                    fixture.SupplierId,
                    fixture.ActorId,
                    [
                        new WarrantyClaimItemInput(
                            fixture.ProductId,
                            1m,
                            "Terminal B failure report",
                            saleItem.Id,
                            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                            [new WarrantyClaimUnitInput(unit.Id, serial)])
                    ], Guid.CreateVersion7()),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        var failedResult = results.Single(r => !r.IsSuccess);
        Assert.Equal("warranty.active_claim_exists", failedResult.Error?.Code);

        // Database verification: exactly 1 active claim exists
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var claimUnits = await verifyDb.WarrantyClaimItemUnits.Where(cu => cu.OriginalInventoryUnitId == unit.Id).ToListAsync();
        Assert.Single(claimUnits);
    }

    #endregion

    #region Race 7: Concurrent Purchase Return vs Sale on Same Inventory Lot

    [Fact]
    public async Task Race07_ConcurrentPurchaseReturnVsSale_OnSameInventoryLot_GuaranteesZeroOversellAndSerializesProductLock()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(setupDb, defaultSalePrice: 100m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(setupDb, "PR vs Sale Race Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        var purchaseOpId = Guid.CreateVersion7();

        // Stock 5 units via purchase
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-P4-LOT-RACE",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                purchaseOpId,
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 5m, 60m, 100m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);
        var purchaseId = purchaseResult.Value!.PurchaseId;

        setupDb.ChangeTracker.Clear();
        var purchaseItem = await setupDb.PurchaseItems.SingleAsync(x => x.PurchaseId == purchaseId);

        var barrier = new Barrier(2);

        // Terminal A attempts to complete a sale of 4 units
        var saleTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    customer.Id,
                    0m,
                    SalePaymentMethod.Bank,
                    400m,
                    "BANK-P4-LOT",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 4m, 100m, [])]),
                CancellationToken.None);
        });

        // Terminal B attempts to create a purchase return of 3 units (total 4 + 3 = 7 > 5)
        var returnTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var returnHandler = ActivatorUtilities.CreateInstance<CreatePurchaseReturnHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            try
            {
                return await returnHandler.HandleAsync(
                    new CreatePurchaseReturnCommand(
                        purchaseId,
                        "Defective Batch",
                        null,
                        PurchaseReturnSettlementMode.External,
                        fixture.ActorId,
                        Guid.CreateVersion7(),
                        [new PurchaseReturnLineInput(purchaseItem.Id, 3m, null, [])]),
                    CancellationToken.None);
            }
            catch (BusinessRuleException ex)
            {
                return EdgeRetails.Application.Common.Result<CreatePurchaseReturnResult>.Failure(ex.Code, ex.Message);
            }
        });

        var saleResult = await saleTask;
        var returnResult = await returnTask;

        // Exactly one operation succeeds and the other fails due to insufficient quantity
        var successCount = (saleResult.IsSuccess ? 1 : 0) + (returnResult.IsSuccess ? 1 : 0);
        var failureCount = (!saleResult.IsSuccess ? 1 : 0) + (!returnResult.IsSuccess ? 1 : 0);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        // Verify remaining stock never goes negative
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var stock = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);

        Assert.True(stock.SellableQty >= 0m, "Sellable inventory must never be negative.");
        // If sale won: 5 - 4 = 1. If return won: 5 - 3 = 2.
        Assert.True(stock.SellableQty == 1m || stock.SellableQty == 2m);
    }

    #endregion

    #region Race 8: Concurrent Stocktake Lock vs Sale Mutation (Blocked by Stocktake Lock)

    [Fact]
    public async Task Race08_ConcurrentStocktakeLockVsSaleMutation_BlocksSaleDuringCounting()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(setupDb, defaultSalePrice: 100m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(setupDb, "P4 Stocktake Blocked Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-P4-STK-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 50m, 100m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        var stocktake = new Stocktake
        {
            Id = Guid.CreateVersion7(),
            Scope = StocktakeScope.FullShop,
            Status = StocktakeStatus.Draft,
            CreatedBy = fixture.ActorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        setupDb.Stocktakes.Add(stocktake);
        await setupDb.SaveChangesAsync();

        var barrier = new Barrier(2);

        // Terminal A starts stocktake (transitions to Counting)
        var startStocktakeTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var startHandler = ActivatorUtilities.CreateInstance<StartStocktakeHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await startHandler.HandleAsync(new StartStocktakeCommand(stocktake.Id), CancellationToken.None);
        });

        // Terminal B concurrently attempts a sale on the counting product
        var saleTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await saleHandler.HandleAsync(
                new CompleteSaleCommand(
                    Guid.CreateVersion7(),
                    null,
                    fixture.ActorId,
                    customer.Id,
                    0m,
                    SalePaymentMethod.Bank,
                    200m,
                    "BANK-STK-RACE",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 100m, [])]),
                CancellationToken.None);
        });

        var startResult = await startStocktakeTask;
        var saleResult = await saleTask;

        Assert.True(startResult.IsSuccess, startResult.Error?.Message);

        // If the sale committed before the stocktake lock engaged, verify that subsequent sale attempts are strictly blocked
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var subsequentSaleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(verifyScope.ServiceProvider);
        var blockedSaleResult = await subsequentSaleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                100m,
                "BANK-STK-ENFORCE",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 100m, [])]),
            CancellationToken.None);

        Assert.False(blockedSaleResult.IsSuccess);
        Assert.Equal("inventory.stocktake_blocks_product", blockedSaleResult.Error?.Code);

        var cancelHandler = ActivatorUtilities.CreateInstance<CancelStocktakeHandler>(verifyScope.ServiceProvider);
        var cancelResult = await cancelHandler.HandleAsync(new CancelStocktakeCommand(stocktake.Id), CancellationToken.None);
        Assert.True(cancelResult.IsSuccess, cancelResult.Error?.Message);
    }

    #endregion

    #region Race 9: Concurrent Unknown-Outcome Replay vs Fresh Request with Duplicate ClientOperationId

    [Fact]
    public async Task Race09_ConcurrentUnknownOutcomeReplayVsFreshRequest_WithDuplicateClientOperationId_ExecutesExactlyOnce()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(setupDb, defaultSalePrice: 200m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(setupDb, "Idempotent Replay Customer");

        // Stock 10 units via purchase
        var purchasing = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        await purchasing.HandleAsync(new CreatePurchaseCommand(
            fixture.SupplierId,
            "INV-P4-IDEMP-STOCK",
            DateOnly.FromDateTime(DateTime.UtcNow),
            "Initial stock",
            0m,
            PurchaseSettlementMode.External,
            fixture.ActorId,
            Guid.CreateVersion7(),
            [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 100m, 200m, [])],
            0m), CancellationToken.None);

        // Same client operation ID shared between duplicate/replay callers
        var sharedClientOperationId = Guid.CreateVersion7();

        var barrier = new Barrier(2);

        // Task 1 simulates fresh request
        var task1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new CompleteSaleCommand(
                    sharedClientOperationId,
                    customer.Id,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    400m,
                    "BANK-IDEMP-1",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 200m, [])]),
                CancellationToken.None);
        });

        // Task 2 simulates concurrent replay/retry with identical ClientOperationId
        var task2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var handler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await handler.HandleAsync(
                new CompleteSaleCommand(
                    sharedClientOperationId,
                    customer.Id,
                    fixture.ActorId,
                    null,
                    0m,
                    SalePaymentMethod.Bank,
                    400m,
                    "BANK-IDEMP-2",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 200m, [])]),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        // Both calls succeed without error
        Assert.True(results[0].IsSuccess, results[0].Error?.Message);
        Assert.True(results[1].IsSuccess, results[1].Error?.Message);

        // Exactly one created the record (WasExisting == false), and one safely recovered it (WasExisting == true)
        var newCount = results.Count(r => !r.Value!.WasExisting);
        var existingCount = results.Count(r => r.Value!.WasExisting);
        Assert.Equal(1, newCount);
        Assert.Equal(1, existingCount);

        // Both received the exact same SaleId and InvoiceNumber
        Assert.Equal(results[0].Value!.SaleId, results[1].Value!.SaleId);
        Assert.Equal(results[0].Value!.InvoiceNumber, results[1].Value!.InvoiceNumber);
        Assert.Equal(400m, results[0].Value!.GrandTotal);

        // Verify in database: exactly 1 Sale, 1 Item, 1 Movement, stock deducted exactly once (from 10 to 8)
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var salesCount = await verifyDb.Sales.CountAsync(s => s.ClientOperationId == sharedClientOperationId);
        Assert.Equal(1, salesCount);

        var sale = await verifyDb.Sales.SingleAsync(s => s.ClientOperationId == sharedClientOperationId);
        var itemsCount = await verifyDb.SaleItems.CountAsync(i => i.SaleId == sale.Id);
        Assert.Equal(1, itemsCount);

        var movementsCount = await verifyDb.InventoryMovements.CountAsync(m => m.ReferenceType == "SALE" && m.ReferenceId == sale.Id);
        Assert.Equal(1, movementsCount);

        var stock = await verifyDb.StockBalances.SingleAsync(s => s.ProductId == fixture.ProductId);
        Assert.Equal(8m, stock.SellableQty);
    }

    #endregion

    #region Race 10: Concurrent Terminal Heartbeat / Status Revocation vs In-Flight Transaction

    [Fact]
    public async Task Race10_ConcurrentTerminalHeartbeatOrTransactionVsRevocation_RevokedTerminalDeniedMutation()
    {
        await using var provider = BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(setupDb);
        var terminalCode = "TERM-REV-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

        var terminal = new Terminal
        {
            Id = Guid.CreateVersion7(),
            TerminalCode = terminalCode,
            Name = "Terminal Subject To Revocation",
            Status = TerminalStatus.Active,
            RegisteredAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow
        };
        setupDb.Terminals.Add(terminal);
        await setupDb.SaveChangesAsync();

        var barrier = new Barrier(2);

        // Terminal B (Admin) revokes Terminal A
        var revocationTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var updateHandler = ActivatorUtilities.CreateInstance<UpdateTerminalStatusHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await updateHandler.HandleAsync(
                new UpdateTerminalStatusCommand(terminal.Id, TerminalStatus.Revoked, actorId),
                CancellationToken.None);
        });

        // Terminal A concurrently sends heartbeat
        var heartbeatTask = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            var heartbeatHandler = ActivatorUtilities.CreateInstance<TerminalHeartbeatHandler>(scope.ServiceProvider);
            barrier.SignalAndWait();
            return await heartbeatHandler.HandleAsync(
                new TerminalHeartbeatCommand(terminal.Id, TerminalProtocol.CurrentProtocolVersion, "192.168.1.150"),
                CancellationToken.None);
        });

        var revokeResult = await revocationTask;
        var heartbeatResult = await heartbeatTask;

        Assert.True(revokeResult.IsSuccess, revokeResult.Error?.Message);
        Assert.Equal(TerminalStatus.Revoked, revokeResult.Value!.Status);

        // Database verification: terminal is marked Revoked permanently
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var dbTerminal = await verifyDb.Terminals.SingleAsync(t => t.Id == terminal.Id);
        Assert.Equal(TerminalStatus.Revoked, dbTerminal.Status);
        Assert.False(dbTerminal.CanMutate);

        // Any subsequent heartbeat or revalidation attempt by Terminal A is strictly denied
        var recheckHeartbeatHandler = ActivatorUtilities.CreateInstance<TerminalHeartbeatHandler>(verifyScope.ServiceProvider);
        var subsequentHeartbeat = await recheckHeartbeatHandler.HandleAsync(
            new TerminalHeartbeatCommand(terminal.Id, TerminalProtocol.CurrentProtocolVersion, "192.168.1.150"),
            CancellationToken.None);

        Assert.False(subsequentHeartbeat.IsSuccess);
        Assert.Equal("terminals.revoked", subsequentHeartbeat.Error?.Code);

        // Revalidation query is also rejected
        var revalidationHandler = ActivatorUtilities.CreateInstance<AuthoritativeRevalidationHandler>(verifyScope.ServiceProvider);
        var revalidationResult = await revalidationHandler.HandleAsync(
            new AuthoritativeRevalidationQuery(terminal.Id),
            CancellationToken.None);

        Assert.False(revalidationResult.IsSuccess);
        Assert.Equal("terminal.revoked", revalidationResult.Error?.Code);
    }

    #endregion
}
