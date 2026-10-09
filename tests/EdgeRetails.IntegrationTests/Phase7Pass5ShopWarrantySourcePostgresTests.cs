using System.Text.Json;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Application.Features.Sales;
using EdgeRetails.Application.Features.Warranty;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.IntegrationTests;

[Collection("Phase2PostgresIntegration")]
public sealed class Phase7Pass5ShopWarrantySourcePostgresTests
{
    // NEW_COVERAGE: source purchase cost and remaining pooled carrying value are distinct persisted facts.
    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task OriginalRemainingLotCostDiffersFromCarryingPoolAfterMixedCostSale(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedCarryingConflictAsync(provider, mode, 100m, 200m, sendBeforeSecondPurchase: false);
        await SellOneAsync(provider, f);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
        var consumption = await db.InventoryLotConsumptions.AsNoTracking().SingleAsync(x => x.MovementId == saleItem.InventoryMovementId);
        Assert.Equal(f.LotA, consumption.LotId);
        Assert.Equal(150m, consumption.TotalCostSnapshot);
        var remaining = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.Id == f.LotB);
        Assert.Equal(200m, remaining.OriginalUnitCost);
        Assert.Equal(200m, remaining.EffectiveUnitCost);
        Assert.Equal(1m, await db.InventoryLotBucketBalances.AsNoTracking()
            .Where(x => x.LotId == f.LotB && x.StockBucket == InventoryBucket.Sellable).SumAsync(x => x.Quantity));
        var cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
        Assert.Equal(1m, cost.CostedQty);
        Assert.Equal(150m, cost.TotalInventoryCost);
        Assert.NotEqual(remaining.EffectiveUnitCost, cost.TotalInventoryCost);
    }

    // AUTHORIZED_ASSERTION_ALIGNMENT: Resolution05 mandates product-wide MWA carrying value (55m), not permanent 100 continuity.
    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task OtherBulkSaleMustNotConsumeCarryingValueOfSupplierHeldOriginalLot(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedCarryingConflictAsync(provider, mode, 100m, 10m, sendBeforeSecondPurchase: true);
        await SellOneAsync(provider, f);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var item = await db.SaleItems.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
        var consumed = await db.InventoryLotConsumptions.AsNoTracking().SingleAsync(x => x.MovementId == item.InventoryMovementId);
        Assert.Equal(f.LotB, consumed.LotId);
        Assert.Equal(1m, await db.InventoryLotBucketBalances.AsNoTracking()
            .Where(x => x.LotId == f.LotA && x.StockBucket == InventoryBucket.WithSupplier).SumAsync(x => x.Quantity));
        var original = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.Id == f.LotA);
        Assert.Equal(100m, original.OriginalUnitCost);
        Assert.Equal(100m, original.EffectiveUnitCost);
        var pool = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
        Assert.Equal(1m, pool.CostedQty);
        Assert.Equal(55m, consumed.TotalCostSnapshot); // current pooled economics after 100+10 receipts
        Assert.Equal(55m, pool.TotalInventoryCost); // Resolution05 authoritative carrying value
    }

    private sealed record CarryingFixture(Guid Product, Guid ProductUnit, Guid Actor, Guid LotA, Guid LotB);

    private static async Task<CarryingFixture> SeedCarryingConflictAsync(
        ServiceProvider provider, TrackingMode mode, decimal firstCost, decimal secondCost, bool sendBeforeSecondPurchase)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 350m);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        if (mode == TrackingMode.Length)
        {
            (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).DisplayDecimalPlaces = 4;
        }
        await db.SaveChangesAsync();
        async Task<(Guid Item, Guid Lot)> PurchaseAsync(decimal cost)
        {
            var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                seed.SupplierId, "D13-carry-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, cost, 350m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess, purchase.Error?.Message);
            db.ChangeTracker.Clear();
            var item = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);
            var lot = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.PurchaseItemId == item.Id);
            return (item.Id, lot.Id);
        }
        var first = await PurchaseAsync(firstCost);
        if (sendBeforeSecondPurchase)
        {
            var damage = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
                new(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, seed.ActorId, "Carrying conflict proof"), default);
            Assert.True(damage.IsSuccess, damage.Error?.Message);
            var sent = await services.GetRequiredService<SendShopStockToSupplierWarrantyHandler>().HandleAsync(
                new(seed.ProductId, InventoryBucket.Damaged, 1m, seed.SupplierId, first.Item, "Carrying conflict proof", seed.ActorId, Guid.NewGuid(), []), default);
            Assert.True(sent.IsSuccess, sent.Error?.Message);
            db.ChangeTracker.Clear();
            var state = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == seed.ProductId);
            Assert.Equal(1m, state.CostedQty);
            Assert.Equal(100m, state.TotalInventoryCost);
            Assert.Equal(1m, await db.InventoryLotBucketBalances.AsNoTracking()
                .Where(x => x.LotId == first.Lot && x.StockBucket == InventoryBucket.WithSupplier).SumAsync(x => x.Quantity));
        }
        var second = await PurchaseAsync(secondCost);
        return new(seed.ProductId, seed.ProductUnitId, seed.ActorId, first.Lot, second.Lot);
    }

    private static async Task SellOneAsync(ServiceProvider provider, CarryingFixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        var sale = await scope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
            Guid.NewGuid(), null, f.Actor, null, 0m, SalePaymentMethod.Bank, 350m, "D13-carry", null,
            [new CompleteSaleLineInput(f.Product, f.ProductUnit, 1m, 350m, [])]), default);
        Assert.True(sale.IsSuccess, sale.Error?.Message);
    }

    // NEW_COVERAGE: canonical real PostgreSQL receipts and condition changes, no fabricated lot allocation.
    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task SendUsesSelectedPurchaseOriginalLotInsteadOfOlderOtherSupplierLot(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var original = await OriginalAsync(provider, f);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
            .HandleAsync(Command(f, f.SupplierA, f.ItemA, 1m), default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var warrantyCase = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == result.Value);
        Assert.Equal(f.ItemA, warrantyCase.SourcePurchaseItemId);
        Assert.Equal(f.SupplierA, warrantyCase.SupplierId);
        Assert.Equal(ShopWarrantyCaseStatus.WithSupplier, warrantyCase.Status);
        var allocated = await WithSupplierAsync(provider, f);
        Assert.Equal(0m, allocated.B);
        Assert.Equal(1m, allocated.A);
        Assert.Equal(original, await OriginalAsync(provider, f));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task SendRefusesSelectedSourceExhaustionDespiteOtherSupplierBucketStock(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        Assert.Equal(2m, await DamagedForLotAsync(provider, f.LotA));
        Assert.Equal(4m, await DamagedForLotAsync(provider, f.LotB));
        var before = await StateAsync(provider, f);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
            .HandleAsync(Command(f, f.SupplierA, f.ItemA, 3m), default);
        Assert.False(result.IsSuccess, "D13-2: source A has only two damaged units; supplier B stock is not source A capacity.");
        Assert.Equal(before, await StateAsync(provider, f));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task WrongSupplierIsRefusedWithoutEconomicMutation(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var before = await StateAsync(provider, f);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
            .HandleAsync(Command(f, f.SupplierB, f.ItemA, 1m), default);
        Assert.False(result.IsSuccess);
        Assert.Equal("warranty.wrong_supplier", result.Error?.Code);
        Assert.Equal(before, await StateAsync(provider, f));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task CorrectOldestSourceSendPreservesReceiptValueAndCommittedReplay(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, mode);
        var original = await OriginalAsync(provider, f);
        var command = Command(f, f.SupplierB, f.ItemB, 1m);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
            .HandleAsync(command, default);
        Assert.True(result.IsSuccess, result.Error?.Message);
        var allocated = await WithSupplierAsync(provider, f);
        Assert.Equal(1m, allocated.B);
        Assert.Equal(0m, allocated.A);
        Assert.Equal(original, await OriginalAsync(provider, f));
        var beforeReplay = await StateAsync(provider, f);
        await using var replayScope = provider.CreateAsyncScope();
        var replay = await replayScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
            .HandleAsync(command, default);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(result.Value, replay.Value);
        Assert.Equal(beforeReplay, await StateAsync(provider, f));
    }

    private sealed record Fixture(Guid Product, Guid Actor, Guid SupplierA, Guid SupplierB,
        Guid ItemA, Guid ItemB, Guid LotA, Guid LotB);

    private static SendShopStockToSupplierWarrantyCommand Command(Fixture f, Guid supplier, Guid item, decimal quantity) =>
        new(f.Product, InventoryBucket.Damaged, quantity, supplier, item, "D13-2 source proof", f.Actor, Guid.NewGuid(), []);

    private static async Task<Fixture> SeedAsync(ServiceProvider provider, TrackingMode mode)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        if (mode == TrackingMode.Length)
        {
            (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).DisplayDecimalPlaces = 4;
        }
        await db.SaveChangesAsync();
        var supplierB = await Phase2PostgresTestHarness.SeedSupplierAsync(db, "D13-2 older B", "WB");
        async Task<(Guid Item, Guid Lot)> PurchaseAsync(Guid supplier, decimal quantity, decimal cost)
        {
            var result = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                supplier, "D13-2-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
                [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, quantity, cost, 150m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(result.IsSuccess, result.Error?.Message);
            db.ChangeTracker.Clear();
            var item = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == result.Value!.PurchaseId);
            var lot = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.PurchaseItemId == item.Id);
            Assert.Equal(quantity, lot.ReceivedQuantity);
            Assert.Equal(cost, lot.OriginalUnitCost);
            return (item.Id, lot.Id);
        }
        var b = await PurchaseAsync(supplierB.Id, 4m, 80m);
        var a = await PurchaseAsync(seed.SupplierId, 2m, 120m);
        Assert.True((await db.InventoryLots.AsNoTracking().SingleAsync(x => x.Id == b.Lot)).CreatedAt <
            (await db.InventoryLots.AsNoTracking().SingleAsync(x => x.Id == a.Lot)).CreatedAt);
        var damaged = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 6m, seed.ActorId, "D13-2 damaged source"), default);
        Assert.True(damaged.IsSuccess, damaged.Error?.Message);
        return new(seed.ProductId, seed.ActorId, seed.SupplierId, supplierB.Id, a.Item, b.Item, a.Lot, b.Lot);
    }

    private static async Task<decimal> DamagedForLotAsync(ServiceProvider provider, Guid lot)
    {
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>().InventoryLotBucketBalances
            .AsNoTracking().Where(x => x.LotId == lot && x.StockBucket == InventoryBucket.Damaged).SumAsync(x => x.Quantity);
    }

    private static async Task<(decimal A, decimal B)> WithSupplierAsync(ServiceProvider provider, Fixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var a = await db.InventoryLotBucketBalances.AsNoTracking()
            .Where(x => x.LotId == f.LotA && x.StockBucket == InventoryBucket.WithSupplier).SumAsync(x => x.Quantity);
        var b = await db.InventoryLotBucketBalances.AsNoTracking()
            .Where(x => x.LotId == f.LotB && x.StockBucket == InventoryBucket.WithSupplier).SumAsync(x => x.Quantity);
        return (a, b);
    }

    private static async Task<string> OriginalAsync(ServiceProvider provider, Fixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var movements = db.InventoryMovements.Where(x => x.ProductId == f.Product).Select(x => x.Id);
        return JsonSerializer.Serialize(new
        {
            Lots = await db.InventoryLots.AsNoTracking().Where(x => x.ProductId == f.Product).OrderBy(x => x.Id).ToArrayAsync(),
            PurchaseItems = await db.PurchaseItems.AsNoTracking().Where(x => x.Id == f.ItemA || x.Id == f.ItemB).OrderBy(x => x.Id).ToArrayAsync(),
            Consumptions = await db.InventoryLotConsumptions.AsNoTracking().Where(x => movements.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            Cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.Product)
        });
    }

    private static async Task<string> StateAsync(ServiceProvider provider, Fixture f)
    {
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var lotIds = db.InventoryLots.Where(x => x.ProductId == f.Product).Select(x => x.Id);
        var movementIds = db.InventoryMovements.Where(x => x.ProductId == f.Product).Select(x => x.Id);
        return JsonSerializer.Serialize(new
        {
            Original = await OriginalAsync(provider, f),
            Cases = await db.ShopStockWarrantyCases.AsNoTracking().Where(x => x.ProductId == f.Product).OrderBy(x => x.Id).ToArrayAsync(),
            Movements = await db.InventoryMovements.AsNoTracking().Where(x => x.ProductId == f.Product).OrderBy(x => x.Id).ToArrayAsync(),
            Effects = await db.InventoryMovementEffects.AsNoTracking().Where(x => movementIds.Contains(x.MovementId)).OrderBy(x => x.Id).ToArrayAsync(),
            LotBalances = await db.InventoryLotBucketBalances.AsNoTracking().Where(x => lotIds.Contains(x.LotId)).OrderBy(x => x.Id).ToArrayAsync(),
            Balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == f.Product)
        });
    }

    private sealed record SimpleFixture(Guid Product, Guid ProductUnit, Guid Actor, Guid Supplier, Guid PurchaseItem, Guid Lot);

    private static async Task<SimpleFixture> SeedSimpleAsync(ServiceProvider provider, TrackingMode mode, decimal quantity, decimal cost)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);
        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        if (mode == TrackingMode.Length)
        {
            (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).DisplayDecimalPlaces = 4;
        }
        await db.SaveChangesAsync();
        var purchase = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId, "D13-2-simple-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
            PurchaseSettlementMode.External, seed.ActorId, Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, quantity, cost, 150m, [])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchase.IsSuccess, purchase.Error?.Message);
        db.ChangeTracker.Clear();
        var item = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == purchase.Value!.PurchaseId);
        var lot = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.PurchaseItemId == item.Id);
        var damaged = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, quantity, seed.ActorId, "D13-2 simple damaged"), default);
        Assert.True(damaged.IsSuccess, damaged.Error?.Message);
        return new(seed.ProductId, seed.ProductUnitId, seed.ActorId, seed.SupplierId, item.Id, lot.Id);
    }

    [Fact]
    public async Task SendRecordsProvenanceSnapshotsAndSupportsFractionalLength()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, TrackingMode.Length);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
            .HandleAsync(Command(f, f.SupplierA, f.ItemA, 1.250m), default);
        Assert.True(result.IsSuccess, result.Error?.Message);

        var db = scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        var allocation = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync(x => x.CaseId == result.Value);
        Assert.Equal(1.250m, allocation.BaseQuantity);
        Assert.Equal(f.LotA, allocation.OriginalInventoryLotId);
        Assert.Equal(120m, allocation.SourceUnitCostSnapshot);
        Assert.True(allocation.SendTimeMwaUnitCostSnapshot > 0);
        Assert.Equal(decimal.Round(allocation.SendTimeMwaUnitCostSnapshot * 1.250m, 6), allocation.SendTimeCarryingValueSnapshot);
    }

    [Fact]
    public async Task SendFailsWhenSourcePurchaseItemDoesNotMatchProduct()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, TrackingMode.Quantity);
        await using var scope = provider.CreateAsyncScope();
        var result = await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
            .HandleAsync(Command(f, f.SupplierA, Guid.NewGuid(), 1m), default);
        Assert.False(result.IsSuccess);
        Assert.Equal("warranty.source_purchase_invalid", result.Error?.Code);
    }

    [Fact]
    public async Task SendRaceSerializesSourceCapacity()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedAsync(provider, TrackingMode.Quantity);
        var barrier = new Barrier(2);
        var t1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(Command(f, f.SupplierA, f.ItemA, 2m), default);
        });
        var t2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return await scope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(Command(f, f.SupplierA, f.ItemA, 2m), default);
        });
        var results = await Task.WhenAll(t1, t2);
        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => !r.IsSuccess && r.Error?.Code == "warranty.source_capacity_exceeded");

        await using var verifyScope = provider.CreateAsyncScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await db.ShopWarrantySendAllocations.CountAsync(x => x.OriginalInventoryLotId == f.LotA));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task PartialResolutionSequenceCanonical(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedSimpleAsync(provider, mode, 10m, 100m);

        Guid caseId;
        Guid sendAllocId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    f.Product, InventoryBucket.Damaged, 10m, f.Supplier, f.PurchaseItem, "Send 10", f.Actor, Guid.NewGuid(), []), default);
            Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
            caseId = sendResult.Value;

            var db = sendScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sendAlloc = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync(x => x.CaseId == caseId);
            sendAllocId = sendAlloc.Id;
        }

        // Resolution 1: 4 Repaired -> Remaining 6
        await using (var r1Scope = provider.CreateAsyncScope())
        {
            var r1 = await r1Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Repaired, f.Actor, null, null, "R1", Guid.NewGuid(), ResolvedQuantity: 4m), default);
            Assert.True(r1.IsSuccess, r1.Error?.Message);

            var db = r1Scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var c = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            Assert.Equal(ShopWarrantyCaseStatus.WithSupplier, c.Status);
            Assert.Null(c.ClosedAt);

            var alloc1 = await db.ShopWarrantyResolutionAllocations.AsNoTracking().SingleAsync(x => x.SendAllocationId == sendAllocId && x.ResolvedBaseQuantity == 4m);
            Assert.Equal(WarrantyResolutionType.Repaired, alloc1.ResolutionOutcome);
            Assert.Equal(0m, alloc1.ActualResolvedCarryingValue);
            Assert.Null(alloc1.ResolutionTimeMwaUnitCostSnapshot);
            Assert.Null(alloc1.SupplierCreditAmount);
        }

        // Resolution 2: 3 Credited (300 credit) -> Remaining 3
        await using (var r2Scope = provider.CreateAsyncScope())
        {
            var r2 = await r2Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Credited, f.Actor, null, null, "R2", Guid.NewGuid(),
                    SupplierCreditAmount: 300m, ResolvedQuantity: 3m), default);
            Assert.True(r2.IsSuccess, r2.Error?.Message);

            var db = r2Scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var c = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            Assert.Equal(ShopWarrantyCaseStatus.WithSupplier, c.Status);
            Assert.Null(c.ClosedAt);

            var alloc2 = await db.ShopWarrantyResolutionAllocations.AsNoTracking().SingleAsync(x => x.SendAllocationId == sendAllocId && x.ResolutionOutcome == WarrantyResolutionType.Credited);
            Assert.Equal(3m, alloc2.ResolvedBaseQuantity);
            Assert.Equal(300m, alloc2.ActualResolvedCarryingValue);
            Assert.Equal(100m, alloc2.ResolutionTimeMwaUnitCostSnapshot);
            Assert.Equal(300m, alloc2.SupplierCreditAmount);

            // ASSERTION_CHANGE: canonical §§216.1/219.4 credit source is the immutable resolution.
            var entry = await db.SupplierAccountEntries.AsNoTracking().SingleAsync(x => x.ReferenceId == alloc2.ResolutionMovementId);
            Assert.Equal("WarrantyResolution", entry.ReferenceType);
            Assert.Equal(300m, entry.Amount);
        }

        // Resolution 3: 3 Scrapped -> Remaining 0 -> Case closed / written off
        await using (var r3Scope = provider.CreateAsyncScope())
        {
            var r3 = await r3Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Scrapped, f.Actor, null, null, "R3", Guid.NewGuid(), ResolvedQuantity: 3m), default);
            Assert.True(r3.IsSuccess, r3.Error?.Message);

            var db = r3Scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var c = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            Assert.Equal(ShopWarrantyCaseStatus.WrittenOff, c.Status);
            Assert.NotNull(c.ClosedAt);

            var alloc3 = await db.ShopWarrantyResolutionAllocations.AsNoTracking().SingleAsync(x => x.SendAllocationId == sendAllocId && x.ResolutionOutcome == WarrantyResolutionType.Scrapped);
            Assert.Equal(3m, alloc3.ResolvedBaseQuantity);
            Assert.Equal(300m, alloc3.ActualResolvedCarryingValue);
            Assert.Equal(100m, alloc3.ResolutionTimeMwaUnitCostSnapshot);
            Assert.Null(alloc3.SupplierCreditAmount);
        }

        // Resolution 4 attempt: 1 Repaired -> Must reject
        await using (var r4Scope = provider.CreateAsyncScope())
        {
            var r4 = await r4Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Repaired, f.Actor, null, null, "R4", Guid.NewGuid(), ResolvedQuantity: 1m), default);
            Assert.False(r4.IsSuccess);
            Assert.Equal("warranty.shop_case_not_with_supplier", r4.Error?.Code);
        }

        await using (var finalScope = provider.CreateAsyncScope())
        {
            var db = finalScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var totalResolved = await db.ShopWarrantyResolutionAllocations.AsNoTracking()
                .Where(x => x.SendAllocationId == sendAllocId)
                .SumAsync(x => x.ResolvedBaseQuantity);
            Assert.Equal(10m, totalResolved);
        }
    }

    [Fact]
    public async Task PartialResolutionFractionalLengthSequence()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedSimpleAsync(provider, TrackingMode.Length, 5m, 100m);

        Guid caseId;
        Guid sendAllocId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    f.Product, InventoryBucket.Damaged, 3.375m, f.Supplier, f.PurchaseItem, "Send 3.375", f.Actor, Guid.NewGuid(), []), default);
            Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
            caseId = sendResult.Value;

            var db = sendScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sendAlloc = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync(x => x.CaseId == caseId);
            sendAllocId = sendAlloc.Id;
        }

        // Resolution 1: 1.125 Rejected -> Remaining 2.250
        await using (var r1Scope = provider.CreateAsyncScope())
        {
            var r1 = await r1Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Rejected, f.Actor, null, null, "R1", Guid.NewGuid(), ResolvedQuantity: 1.125m), default);
            Assert.True(r1.IsSuccess, r1.Error?.Message);

            var db = r1Scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var c = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            Assert.Equal(ShopWarrantyCaseStatus.WithSupplier, c.Status);
        }

        // Resolution 2: 2.250 Replaced -> Remaining 0 -> Closed
        await using (var r2Scope = provider.CreateAsyncScope())
        {
            var r2 = await r2Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Replaced, f.Actor, null, null, "R2", Guid.NewGuid(), ResolvedQuantity: 2.250m), default);
            Assert.True(r2.IsSuccess, r2.Error?.Message);

            var db = r2Scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var c = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            Assert.Equal(ShopWarrantyCaseStatus.Closed, c.Status);
            Assert.NotNull(c.ClosedAt);
        }

        // Attempt 3: 0.125 -> Must reject
        await using (var r3Scope = provider.CreateAsyncScope())
        {
            var r3 = await r3Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Repaired, f.Actor, null, null, "R3", Guid.NewGuid(), ResolvedQuantity: 0.125m), default);
            Assert.False(r3.IsSuccess);
            Assert.Equal("warranty.shop_case_not_with_supplier", r3.Error?.Code);
        }

        await using (var finalScope = provider.CreateAsyncScope())
        {
            var db = finalScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var totalResolved = await db.ShopWarrantyResolutionAllocations.AsNoTracking()
                .Where(x => x.SendAllocationId == sendAllocId)
                .SumAsync(x => x.ResolvedBaseQuantity);
            Assert.Equal(3.375m, totalResolved);
        }
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task RepairedAndRejectedCustodyNeutrality(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedSimpleAsync(provider, mode, 4m, 100m);

        Guid caseId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    f.Product, InventoryBucket.Damaged, 2m, f.Supplier, f.PurchaseItem, "Send 2", f.Actor, Guid.NewGuid(), []), default);
            Assert.True(sendResult.IsSuccess);
            caseId = sendResult.Value;
        }

        // Repaired 1
        await using (var rScope = provider.CreateAsyncScope())
        {
            var r = await rScope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Repaired, f.Actor, null, null, "Repaired", Guid.NewGuid(), ResolvedQuantity: 1m), default);
            Assert.True(r.IsSuccess);
        }

        // Rejected 1
        await using (var rScope = provider.CreateAsyncScope())
        {
            var r = await rScope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Rejected, f.Actor, null, null, "Rejected", Guid.NewGuid(), ResolvedQuantity: 1m), default);
            Assert.True(r.IsSuccess);
        }

        await using (var verifyScope = provider.CreateAsyncScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
            Assert.Equal(4m, cost.CostedQty);
            Assert.Equal(400m, cost.TotalInventoryCost);

            var balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
            Assert.Equal(1m, balance.SellableQty);
            Assert.Equal(1m, balance.DefectiveQty);
            Assert.Equal(0m, balance.WithSupplierQty);
        }
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task TerminalEconomicsScrapAndCreditDerecognizeAtCurrentMwa(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedSimpleAsync(provider, mode, 2m, 100m);

        Guid caseId;
        Guid sendAllocId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    f.Product, InventoryBucket.Damaged, 2m, f.Supplier, f.PurchaseItem, "Send 2", f.Actor, Guid.NewGuid(), []), default);
            Assert.True(sendResult.IsSuccess);
            caseId = sendResult.Value;

            var db = sendScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sendAlloc = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync(x => x.CaseId == caseId);
            sendAllocId = sendAlloc.Id;
        }

        // Second purchase: 2 @ 50m -> Total = 300m, CostedQty = 4, MWA = 75m
        await using (var buyScope = provider.CreateAsyncScope())
        {
            var purchase = await buyScope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                f.Supplier, "D13-2-second-" + Guid.NewGuid(), DateOnly.FromDateTime(DateTime.UtcNow), null, 0m,
                PurchaseSettlementMode.External, f.Actor, Guid.NewGuid(),
                [new CreatePurchaseLineInput(f.Product, f.ProductUnit, 2m, 50m, 150m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(purchase.IsSuccess);
        }

        // Scrap 1 at resolution MWA (75m)
        await using (var scrapScope = provider.CreateAsyncScope())
        {
            var scrapResult = await scrapScope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Scrapped, f.Actor, null, null, "Scrap 1", Guid.NewGuid(), ResolvedQuantity: 1m), default);
            Assert.True(scrapResult.IsSuccess);

            var db = scrapScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
            Assert.Equal(3m, cost.CostedQty);
            Assert.Equal(225m, cost.TotalInventoryCost);

            var alloc = await db.ShopWarrantyResolutionAllocations.AsNoTracking().SingleAsync(x => x.SendAllocationId == sendAllocId && x.ResolutionOutcome == WarrantyResolutionType.Scrapped);
            Assert.Equal(75m, alloc.ActualResolvedCarryingValue);
            Assert.Equal(75m, alloc.ResolutionTimeMwaUnitCostSnapshot);
        }

        // Credit 1 with credit = 90m at current MWA (75m)
        await using (var creditScope = provider.CreateAsyncScope())
        {
            var creditResult = await creditScope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Credited, f.Actor, null, null, "Credit 1", Guid.NewGuid(),
                    SupplierCreditAmount: 90m, ResolvedQuantity: 1m), default);
            Assert.True(creditResult.IsSuccess);

            var db = creditScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var cost = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == f.Product);
            Assert.Equal(2m, cost.CostedQty);
            Assert.Equal(150m, cost.TotalInventoryCost);

            var alloc = await db.ShopWarrantyResolutionAllocations.AsNoTracking().SingleAsync(x => x.SendAllocationId == sendAllocId && x.ResolutionOutcome == WarrantyResolutionType.Credited);
            Assert.Equal(75m, alloc.ActualResolvedCarryingValue);
            Assert.Equal(75m, alloc.ResolutionTimeMwaUnitCostSnapshot);
            Assert.Equal(90m, alloc.SupplierCreditAmount);
        }
    }

    [Fact]
    public async Task ResolutionReplayAndPayloadMismatch()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedSimpleAsync(provider, TrackingMode.Quantity, 2m, 100m);

        Guid caseId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    f.Product, InventoryBucket.Damaged, 2m, f.Supplier, f.PurchaseItem, "Send 2", f.Actor, Guid.NewGuid(), []), default);
            Assert.True(sendResult.IsSuccess);
            caseId = sendResult.Value;
        }

        var opId = Guid.NewGuid();
        var cmd = new ReceiveShopStockWarrantyCommand(
            caseId, WarrantyResolutionType.Repaired, f.Actor, null, null, "Replay test", opId, ResolvedQuantity: 1m);

        await using (var r1Scope = provider.CreateAsyncScope())
        {
            var r1 = await r1Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(cmd, default);
            Assert.True(r1.IsSuccess);
        }

        // Replay same payload
        await using (var r2Scope = provider.CreateAsyncScope())
        {
            var r2 = await r2Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(cmd, default);
            Assert.True(r2.IsSuccess);

            var db = r2Scope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            Assert.Equal(1, await db.ShopWarrantyResolutionAllocations.CountAsync(x => x.ClientOperationId == opId));
        }

        // Replay different payload
        var tamperedCmd = cmd with { Note = "tampered note" };
        await using (var r3Scope = provider.CreateAsyncScope())
        {
            var r3 = await r3Scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(tamperedCmd, default);
            Assert.False(r3.IsSuccess);
            Assert.Equal("warranty.replay_reconciliation_required", r3.Error?.Code);
        }
    }

    [Fact]
    public async Task ResolutionRaceSerializesRemainingCapacity()
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();
        var f = await SeedSimpleAsync(provider, TrackingMode.Quantity, 2m, 100m);

        Guid caseId;
        Guid sendAllocId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    f.Product, InventoryBucket.Damaged, 1m, f.Supplier, f.PurchaseItem, "Send 1", f.Actor, Guid.NewGuid(), []), default);
            Assert.True(sendResult.IsSuccess);
            caseId = sendResult.Value;

            var sendDb = sendScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sendAlloc = await sendDb.ShopWarrantySendAllocations.AsNoTracking().SingleAsync(x => x.CaseId == caseId);
            sendAllocId = sendAlloc.Id;
        }

        var barrier = new Barrier(2);
        var t1 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Repaired, f.Actor, null, null, "Race 1", Guid.NewGuid(), ResolvedQuantity: 1m), default);
        });
        var t2 = Task.Run(async () =>
        {
            await using var scope = provider.CreateAsyncScope();
            barrier.SignalAndWait();
            return await scope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>()
                .HandleAsync(new ReceiveShopStockWarrantyCommand(
                    caseId, WarrantyResolutionType.Repaired, f.Actor, null, null, "Race 2", Guid.NewGuid(), ResolvedQuantity: 1m), default);
        });
        var results = await Task.WhenAll(t1, t2);
        Assert.Single(results, r => r.IsSuccess);
        Assert.Single(results, r => !r.IsSuccess && (r.Error?.Code == "warranty.resolution_quantity_exceeds_remaining" || r.Error?.Code == "warranty.shop_case_not_with_supplier"));

        await using var verifyScope = provider.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
        Assert.Equal(1, await verifyDb.ShopWarrantyResolutionAllocations.CountAsync(x => x.SendAllocationId == sendAllocId));
    }

    // =====================================================================
    // CANONICAL MWA55 SEQUENCE CLOSURE (GOVERNANCE RESOLUTION 05 / SECTION 9)
    // =====================================================================

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task CanonicalMwa55SequenceSupplierCreditResolvesAtCurrentMwaWithRecoveryGain(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();

        // 1. Receive A1 @ 100
        var fixture = await SeedCanonicalMwa55FixtureAsync(provider, mode);

        // 2. Send A1 to Shop Warranty -> assert send-time snapshot 100
        Guid caseId;
        Guid sendAllocId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    fixture.ProductId,
                    InventoryBucket.Damaged,
                    1m,
                    fixture.SupplierId,
                    fixture.PurchaseItemAId,
                    "Canonical MWA55 send A1",
                    fixture.ActorId,
                    Guid.NewGuid(),
                    []), default);
            Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
            caseId = sendResult.Value;

            var db = sendScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sendAlloc = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync(x => x.CaseId == caseId);
            sendAllocId = sendAlloc.Id;

            // Assert send-time snapshot 100
            Assert.Equal(100m, sendAlloc.SourceUnitCostSnapshot);
            Assert.Equal(100m, sendAlloc.SendTimeMwaUnitCostSnapshot);
            Assert.Equal(100m, sendAlloc.SendTimeCarryingValueSnapshot);
            Assert.Equal(1m, sendAlloc.BaseQuantity);
            Assert.Equal(fixture.LotAId, sendAlloc.OriginalInventoryLotId);

            var costStateBeforeB = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            Assert.Equal(1m, costStateBeforeB.CostedQty);
            Assert.Equal(100m, costStateBeforeB.TotalInventoryCost);
            Assert.Equal(100m, costStateBeforeB.MovingAverageCost);
        }

        // 3. Receive B1 @ 10 -> assert OwnedQty = 2, ProductCostState value = 110, MWA = 55
        Guid lotBId;
        await using (var buyBScope = provider.CreateAsyncScope())
        {
            var buyB = await buyBScope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                fixture.SupplierId,
                "MWA55-credit-buy-B-" + Guid.NewGuid(),
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.NewGuid(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 10m, 350m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(buyB.IsSuccess, buyB.Error?.Message);

            var db = buyBScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var purchaseItemB = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == buyB.Value!.PurchaseId);
            var lotB = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.PurchaseItemId == purchaseItemB.Id);
            lotBId = lotB.Id;

            var costState = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            var balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);

            // Assert OwnedQty = 2, ProductCostState value = 110, MWA = 55
            Assert.Equal(2m, costState.CostedQty);
            Assert.Equal(2m, balance.SellableQty + balance.WithSupplierQty);
            Assert.Equal(110m, costState.TotalInventoryCost);
            Assert.Equal(55m, costState.MovingAverageCost);
        }

        // 4. Sell one ordinary bulk unit -> assert COGS = 55, Remaining OwnedQty = 1, Remaining InventoryValue = 55
        await using (var saleScope = provider.CreateAsyncScope())
        {
            var saleResult = await saleScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
                Guid.NewGuid(),
                null,
                fixture.ActorId,
                null,
                0m,
                SalePaymentMethod.Bank,
                350m,
                "MWA55-credit-sale",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 350m, [])]), default);
            Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);

            var db = saleScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            var consumption = await db.InventoryLotConsumptions.AsNoTracking().SingleAsync(x => x.MovementId == saleItem.InventoryMovementId);

            // Assert COGS = 55
            Assert.Equal(55m, consumption.TotalCostSnapshot);
            Assert.Equal(lotBId, consumption.LotId);

            var costState = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            // Assert Remaining OwnedQty = 1, Remaining InventoryValue = 55
            Assert.Equal(1m, costState.CostedQty);
            Assert.Equal(55m, costState.TotalInventoryCost);
            Assert.Equal(55m, costState.MovingAverageCost);

            var balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            Assert.Equal(0m, balance.SellableQty);
            Assert.Equal(1m, balance.WithSupplierQty);
        }

        // 5. Then SupplierCredit100 -> assert ActualResolvedCarryingValue = 55, SupplierCredit = 100, WarrantyRecoveryGain = 45, SalesRevenue = 0
        await using (var creditScope = provider.CreateAsyncScope())
        {
            var creditResult = await creditScope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                new ReceiveShopStockWarrantyCommand(
                    caseId,
                    WarrantyResolutionType.Credited,
                    fixture.ActorId,
                    null,
                    null,
                    "Canonical MWA55 Credit 100",
                    Guid.NewGuid(),
                    SupplierCreditAmount: 100m,
                    ResolvedQuantity: 1m), default);
            Assert.True(creditResult.IsSuccess, creditResult.Error?.Message);

            var db = creditScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var warrantyCase = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            var resolutionAlloc = await db.ShopWarrantyResolutionAllocations.AsNoTracking().SingleAsync(x => x.SendAllocationId == sendAllocId);

            // Assert ActualResolvedCarryingValue = 55
            Assert.Equal(55m, resolutionAlloc.ActualResolvedCarryingValue);
            Assert.Equal(55m, warrantyCase.InventoryCarryingCostResolved);
            Assert.Equal(55m, resolutionAlloc.ResolutionTimeMwaUnitCostSnapshot);

            // Assert SupplierCredit = 100
            Assert.Equal(100m, resolutionAlloc.SupplierCreditAmount);
            Assert.Equal(100m, warrantyCase.SupplierCreditAmount);

            // ASSERTION_CHANGE: preserve MWA55 economics and require canonical resolution source.
            var accountEntry = await db.SupplierAccountEntries.AsNoTracking().SingleAsync(x => x.ReferenceId == resolutionAlloc.ResolutionMovementId);
            Assert.Equal("WarrantyResolution", accountEntry.ReferenceType);
            Assert.Equal(100m, accountEntry.Amount);
            Assert.Equal(SupplierAccountDirection.DecreasePayable, accountEntry.Direction);
            Assert.Equal(SupplierAccountEntryType.WarrantyCredit, accountEntry.EntryType);

            // Assert WarrantyRecoveryGain = 45
            Assert.Equal(45m, warrantyCase.RecoveryDifference);

            // Assert SalesRevenue = 0 (Warranty recovery never creates sales revenue; only 1 original sale exists in database)
            var totalSalesRevenue = await db.SaleItems.AsNoTracking()
                .Where(x => x.ProductId == fixture.ProductId)
                .SumAsync(x => x.NetLineTotal);
            Assert.Equal(350m, totalSalesRevenue);
            var saleCount = await db.SaleItems.AsNoTracking()
                .Where(x => x.ProductId == fixture.ProductId)
                .Select(x => x.SaleId)
                .Distinct()
                .CountAsync();
            Assert.Equal(1, saleCount);

            // Assert inventory pool fully extinguished
            var costState = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            Assert.Equal(0m, costState.CostedQty);
            Assert.Equal(0m, costState.TotalInventoryCost);
            Assert.Equal(0m, costState.MovingAverageCost);

            var balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            Assert.Equal(0m, balance.SellableQty);
            Assert.Equal(0m, balance.WithSupplierQty);

            Assert.Equal(ShopWarrantyCaseStatus.Closed, warrantyCase.Status);
            Assert.NotNull(warrantyCase.ClosedAt);
        }
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)]
    [InlineData(TrackingMode.Length)]
    public async Task CanonicalMwa55SequenceScrapResolvesAtCurrentMwaWithLoss(TrackingMode mode)
    {
        await using var provider = Phase2PostgresTestHarness.BuildProvider();

        // 1. Receive A1 @ 100
        var fixture = await SeedCanonicalMwa55FixtureAsync(provider, mode);

        // 2. Send A1 to Shop Warranty -> assert send-time snapshot 100
        Guid caseId;
        Guid sendAllocId;
        await using (var sendScope = provider.CreateAsyncScope())
        {
            var sendResult = await sendScope.ServiceProvider.GetRequiredService<SendShopStockToSupplierWarrantyHandler>()
                .HandleAsync(new SendShopStockToSupplierWarrantyCommand(
                    fixture.ProductId,
                    InventoryBucket.Damaged,
                    1m,
                    fixture.SupplierId,
                    fixture.PurchaseItemAId,
                    "Canonical MWA55 send A1 scrap",
                    fixture.ActorId,
                    Guid.NewGuid(),
                    []), default);
            Assert.True(sendResult.IsSuccess, sendResult.Error?.Message);
            caseId = sendResult.Value;

            var db = sendScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var sendAlloc = await db.ShopWarrantySendAllocations.AsNoTracking().SingleAsync(x => x.CaseId == caseId);
            sendAllocId = sendAlloc.Id;

            // Assert send-time snapshot 100
            Assert.Equal(100m, sendAlloc.SourceUnitCostSnapshot);
            Assert.Equal(100m, sendAlloc.SendTimeMwaUnitCostSnapshot);
            Assert.Equal(100m, sendAlloc.SendTimeCarryingValueSnapshot);
            Assert.Equal(1m, sendAlloc.BaseQuantity);
            Assert.Equal(fixture.LotAId, sendAlloc.OriginalInventoryLotId);
        }

        // 3. Receive B1 @ 10 -> assert OwnedQty = 2, ProductCostState value = 110, MWA = 55
        Guid lotBId;
        await using (var buyBScope = provider.CreateAsyncScope())
        {
            var buyB = await buyBScope.ServiceProvider.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
                fixture.SupplierId,
                "MWA55-scrap-buy-B-" + Guid.NewGuid(),
                DateOnly.FromDateTime(DateTime.UtcNow),
                null,
                0m,
                PurchaseSettlementMode.External,
                fixture.ActorId,
                Guid.NewGuid(),
                [new CreatePurchaseLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 10m, 350m, [])],
                InitialPaymentAmount: 0m), default);
            Assert.True(buyB.IsSuccess, buyB.Error?.Message);

            var db = buyBScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var purchaseItemB = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == buyB.Value!.PurchaseId);
            var lotB = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.PurchaseItemId == purchaseItemB.Id);
            lotBId = lotB.Id;

            var costState = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            var balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);

            // Assert OwnedQty = 2, ProductCostState value = 110, MWA = 55
            Assert.Equal(2m, costState.CostedQty);
            Assert.Equal(2m, balance.SellableQty + balance.WithSupplierQty);
            Assert.Equal(110m, costState.TotalInventoryCost);
            Assert.Equal(55m, costState.MovingAverageCost);
        }

        // 4. Sell one ordinary bulk unit -> assert COGS = 55, Remaining OwnedQty = 1, Remaining InventoryValue = 55
        await using (var saleScope = provider.CreateAsyncScope())
        {
            var saleResult = await saleScope.ServiceProvider.GetRequiredService<CompleteSaleHandler>().HandleAsync(new CompleteSaleCommand(
                Guid.NewGuid(),
                null,
                fixture.ActorId,
                null,
                0m,
                SalePaymentMethod.Bank,
                350m,
                "MWA55-scrap-sale",
                null,
                [new CompleteSaleLineInput(fixture.ProductId, fixture.ProductUnitId, 1m, 350m, [])]), default);
            Assert.True(saleResult.IsSuccess, saleResult.Error?.Message);

            var db = saleScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var saleItem = await db.SaleItems.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            var consumption = await db.InventoryLotConsumptions.AsNoTracking().SingleAsync(x => x.MovementId == saleItem.InventoryMovementId);

            // Assert COGS = 55
            Assert.Equal(55m, consumption.TotalCostSnapshot);
            Assert.Equal(lotBId, consumption.LotId);

            var costState = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            // Assert Remaining OwnedQty = 1, Remaining InventoryValue = 55
            Assert.Equal(1m, costState.CostedQty);
            Assert.Equal(55m, costState.TotalInventoryCost);
            Assert.Equal(55m, costState.MovingAverageCost);
        }

        // 5. Separate Scrap case: carrying removal = 55, loss = 55
        await using (var scrapScope = provider.CreateAsyncScope())
        {
            var scrapResult = await scrapScope.ServiceProvider.GetRequiredService<ReceiveShopStockWarrantyHandler>().HandleAsync(
                new ReceiveShopStockWarrantyCommand(
                    caseId,
                    WarrantyResolutionType.Scrapped,
                    fixture.ActorId,
                    null,
                    null,
                    "Canonical MWA55 Scrap 55",
                    Guid.NewGuid(),
                    ResolvedQuantity: 1m), default);
            Assert.True(scrapResult.IsSuccess, scrapResult.Error?.Message);

            var db = scrapScope.ServiceProvider.GetRequiredService<EdgeRetailsDbContext>();
            var warrantyCase = await db.ShopStockWarrantyCases.AsNoTracking().SingleAsync(x => x.Id == caseId);
            var resolutionAlloc = await db.ShopWarrantyResolutionAllocations.AsNoTracking().SingleAsync(x => x.SendAllocationId == sendAllocId);
            var scrapMovement = await db.InventoryMovements.AsNoTracking().SingleAsync(x => x.Id == resolutionAlloc.ResolutionMovementId);

            // Assert carrying removal = 55
            Assert.Equal(55m, resolutionAlloc.ActualResolvedCarryingValue);
            Assert.Equal(55m, warrantyCase.InventoryCarryingCostResolved);
            Assert.Equal(55m, resolutionAlloc.ResolutionTimeMwaUnitCostSnapshot);

            // Assert loss = 55
            Assert.Equal(55m, scrapMovement.RecognizedLossAmount);
            Assert.Equal(-55m, warrantyCase.RecoveryDifference);

            // Assert gain = 0 and no supplier credit
            Assert.Null(resolutionAlloc.SupplierCreditAmount);
            Assert.Null(warrantyCase.SupplierCreditAmount);
            // ASSERTION_CHANGE: exclude credits through both legacy case and canonical movement sources.
            Assert.False(await db.SupplierAccountEntries.AsNoTracking().AnyAsync(x => x.ReferenceId == caseId ||
                db.InventoryMovements.Any(m => m.Id == x.ReferenceId && m.ReferenceType == "SHOP_WARRANTY" && m.ReferenceId == caseId)));

            // Assert inventory pool fully extinguished
            var costState = await db.ProductCostStates.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            Assert.Equal(0m, costState.CostedQty);
            Assert.Equal(0m, costState.TotalInventoryCost);
            Assert.Equal(0m, costState.MovingAverageCost);

            var balance = await db.StockBalances.AsNoTracking().SingleAsync(x => x.ProductId == fixture.ProductId);
            Assert.Equal(0m, balance.SellableQty);
            Assert.Equal(0m, balance.WithSupplierQty);
            Assert.Equal(1m, balance.ScrapQty);

            Assert.Equal(ShopWarrantyCaseStatus.WrittenOff, warrantyCase.Status);
            Assert.NotNull(warrantyCase.ClosedAt);
        }
    }

    private sealed record CanonicalMwa55Fixture(
        Guid ProductId,
        Guid ProductUnitId,
        Guid ActorId,
        Guid SupplierId,
        Guid PurchaseItemAId,
        Guid LotAId);

    private static async Task<CanonicalMwa55Fixture> SeedCanonicalMwa55FixtureAsync(
        ServiceProvider provider,
        TrackingMode mode)
    {
        await using var scope = provider.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<EdgeRetailsDbContext>();
        await Phase2PostgresTestHarness.EnsureReceiptConfigurationAsync(db);

        var seed = await Phase2PostgresTestHarness.SeedQuantityProductAsync(db, defaultSalePrice: 350m);
        (await db.Products.SingleAsync(x => x.Id == seed.ProductId)).TrackingMode = mode;
        if (mode == TrackingMode.Length)
        {
            (await db.Units.SingleAsync(x => x.Id == seed.UnitId)).DisplayDecimalPlaces = 4;
        }
        await db.SaveChangesAsync();

        var purchaseA = await services.GetRequiredService<CreatePurchaseHandler>().HandleAsync(new CreatePurchaseCommand(
            seed.SupplierId,
            "MWA55-seed-A-" + Guid.NewGuid(),
            DateOnly.FromDateTime(DateTime.UtcNow),
            null,
            0m,
            PurchaseSettlementMode.External,
            seed.ActorId,
            Guid.NewGuid(),
            [new CreatePurchaseLineInput(seed.ProductId, seed.ProductUnitId, 1m, 100m, 350m, [])],
            InitialPaymentAmount: 0m), default);
        Assert.True(purchaseA.IsSuccess, purchaseA.Error?.Message);

        db.ChangeTracker.Clear();
        var itemA = await db.PurchaseItems.AsNoTracking().SingleAsync(x => x.PurchaseId == purchaseA.Value!.PurchaseId);
        var lotA = await db.InventoryLots.AsNoTracking().SingleAsync(x => x.PurchaseItemId == itemA.Id);

        var damage = await services.GetRequiredService<TransferInventoryConditionHandler>().HandleAsync(
            new(seed.ProductId, InventoryBucket.Sellable, InventoryBucket.Damaged, 1m, seed.ActorId, "MWA55 damaged staging"), default);
        Assert.True(damage.IsSuccess, damage.Error?.Message);

        return new CanonicalMwa55Fixture(seed.ProductId, seed.ProductUnitId, seed.ActorId, seed.SupplierId, itemA.Id, lotA.Id);
    }
}
