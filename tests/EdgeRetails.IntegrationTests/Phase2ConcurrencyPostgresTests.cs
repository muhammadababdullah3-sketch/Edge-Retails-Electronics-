using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EdgeRetails.IntegrationTests;

public sealed class Phase2ConcurrencyPostgresTests
{
    [Fact]
    public async Task SupplierPayment_ConcurrentSettlementRace_PreventsOverSettlement_ViaAdvisoryLocks()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var supplier = await Phase2PostgresTestHarness.SeedSupplierAsync(setupDb, "Concurrent Khata Supplier");
        var actorId = await IntegrationIdentitySeeder.CreateActorAsync(setupDb);

        // Seed 10,000 payable
        setupDb.SupplierAccountEntries.Add(new SupplierAccountEntry
        {
            EntryNumber = "SAE-INIT-01",
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

        // Exactly one must succeed, the other must be rejected
        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        var failedResult = results.Single(r => !r.IsSuccess);
        Assert.Equal("supplier.payment_exceeds_payable", failedResult.Error?.Code);

        // Final balance in database must be exactly 3,000 (never negative)
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var entries = await verifyDb.SupplierAccountEntries.Where(x => x.SupplierId == supplier.Id).ToListAsync();
        var finalPayable = entries.Sum(e => e.Direction == SupplierAccountDirection.IncreasePayable ? e.Amount : -e.Amount);
        Assert.Equal(3000m, finalPayable);
    }

    [Fact]
    public async Task SameSerializedUnit_ConcurrentSaleRace_AllowsExactlyOneSale()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb);

        var serial = "SN-RACE-" + Guid.NewGuid().ToString("N")[..8];
        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);

        var purchase = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-RACE-01",
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

        Assert.True(purchase.IsSuccess);

        setupDb.ChangeTracker.Clear();
        var unit = await setupDb.InventoryUnits.SingleAsync(x => x.ProductId == fixture.ProductId);

        var barrier = new Barrier(2);

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
                    "TERMINAL-1",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 5000m, [unit.Id])]),
                CancellationToken.None);
        });

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
                    "TERMINAL-2",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 5000m, [unit.Id])]),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        // Verification in database
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        var saleItems = await verifyDb.SaleItems.Where(i => i.ProductId == fixture.ProductId).ToListAsync();
        Assert.Single(saleItems);

        var updatedUnit = await verifyDb.InventoryUnits.SingleAsync(x => x.Id == unit.Id);
        Assert.Equal(InventoryUnitStatus.Sold, updatedUnit.Status);

        var stock = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(0m, stock.SellableQty);
    }

    [Fact]
    public async Task FinalQuantity_ConcurrentSaleRace_PreventsNegativeStock()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db: setupDb, defaultSalePrice: 100m);

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-STOCK-RACE",
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
                    "CASHIER-A",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 4m, 100m, [])]),
                CancellationToken.None);
        });

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
                    "CASHIER-B",
                    null,
                    [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 4m, 100m, [])]),
                CancellationToken.None);
        });

        var results = await Task.WhenAll(task1, task2);

        var successCount = results.Count(r => r.IsSuccess);
        var failureCount = results.Count(r => !r.IsSuccess);

        Assert.Equal(1, successCount);
        Assert.Equal(1, failureCount);

        // Verify stock never becomes negative
        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var stock = await verifyDb.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(1m, stock.SellableQty);
    }

    [Fact]
    public async Task CustomerWarrantyClaim_ConcurrentClaimsOnSameUnit_AllowsExactlyOne()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var setupScope = provider.CreateAsyncScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(setupDb);
        var fixture = await Phase2PostgresTestHarness.SeedSerializedProductAsync(setupDb, defaultSalePrice: 6000m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(setupDb, "Claim Race Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(setupScope.ServiceProvider);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(setupScope.ServiceProvider);

        var serial = "SN-CLAIM-RACE-" + Guid.NewGuid().ToString("N")[..8];
        await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-CLAIM-RACE",
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
                "BANK-CLAIM",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 6000m, [unit.Id])]),
            CancellationToken.None);

        Assert.True(saleResult.IsSuccess);
        var saleId = saleResult.Value!.SaleId;

        setupDb.ChangeTracker.Clear();
        var saleItem = await setupDb.SaleItems.SingleAsync(x => x.SaleId == saleId);

        var barrier = new Barrier(2);

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
                            "Agent 1 report",
                            saleItem.Id,
                            DateOnly.FromDateTime(DateTime.UtcNow.AddYears(1)),
                            [new WarrantyClaimUnitInput(unit.Id, serial)])
                    ], Guid.CreateVersion7()),
                CancellationToken.None);
        });

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
                            "Agent 2 report",
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
    }

    [Fact]
    public async Task ActiveStocktake_BlocksConflictingMutations_UntilStocktakeComplete()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();

        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var fixture = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 100m);
        var customer = await Phase2PostgresTestHarness.SeedCustomerAsync(db, "Stocktake Blocked Customer");

        var purchaseHandler = ActivatorUtilities.CreateInstance<CreatePurchaseHandler>(services);
        var saleHandler = ActivatorUtilities.CreateInstance<CompleteSaleHandler>(services);
        var startStocktakeHandler = ActivatorUtilities.CreateInstance<StartStocktakeHandler>(services);
        var cancelStocktakeHandler = ActivatorUtilities.CreateInstance<CancelStocktakeHandler>(services);

        // 1. Ingest 10 units
        var purchaseResult = await purchaseHandler.HandleAsync(
            new CreatePurchaseCommand(
                fixture.SupplierId,
                "INV-STK-01",
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.CreateVersion7(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 10m, 50m, 100m, [])],
                InitialPaymentAmount: 0m),
            CancellationToken.None);

        Assert.True(purchaseResult.IsSuccess, purchaseResult.Error?.Message);

        // 2. Create and start a full stocktake (transitions to Counting)
        var stocktake = new Stocktake
        {
            Id = Guid.CreateVersion7(),
            Scope = StocktakeScope.FullShop,
            Status = StocktakeStatus.Draft,
            CreatedBy = fixture.ActorId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Stocktakes.Add(stocktake);
        await db.SaveChangesAsync();

        var startResult = await startStocktakeHandler.HandleAsync(
            new StartStocktakeCommand(stocktake.Id),
            CancellationToken.None);

        Assert.True(startResult.IsSuccess, startResult.Error?.Message);

        // 3. Attempting to complete a sale on the counting product must be BLOCKED
        var saleAttempt = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                200m,
                "BANK-STK-BLOCK",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 100m, [])]),
            CancellationToken.None);

        Assert.False(saleAttempt.IsSuccess);
        Assert.Equal("inventory.stocktake_blocks_product", saleAttempt.Error?.Code);

        // 4. Cancel the stocktake to unblock mutations
        var cancelResult = await cancelStocktakeHandler.HandleAsync(
            new CancelStocktakeCommand(stocktake.Id),
            CancellationToken.None);

        Assert.True(cancelResult.IsSuccess, cancelResult.Error?.Message);

        // 5. Sale now succeeds cleanly
        var retrySale = await saleHandler.HandleAsync(
            new CompleteSaleCommand(
                Guid.CreateVersion7(),
                null,
                fixture.ActorId,
                customer.Id,
                0m,
                SalePaymentMethod.Bank,
                200m,
                "BANK-STK-SUCCESS",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 2m, 100m, [])]),
            CancellationToken.None);

        Assert.True(retrySale.IsSuccess, retrySale.Error?.Message);

        // Final verification: exactly 8 units remain sellable
        db.ChangeTracker.Clear();
        var finalStock = await db.StockBalances.SingleAsync(x => x.ProductId == fixture.ProductId);
        Assert.Equal(8m, finalStock.SellableQty);
    }
}
