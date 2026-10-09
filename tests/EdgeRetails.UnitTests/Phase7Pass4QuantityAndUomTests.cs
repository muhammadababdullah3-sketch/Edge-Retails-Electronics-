using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Application.Features.Purchasing;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using EdgeRetails.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EdgeRetails.UnitTests;

// NEW_COVERAGE: exact counts, frozen receipt authority, and historical factor boundary.
public sealed class Phase7Pass4QuantityAndUomTests
{
    [Theory]
    [InlineData("Purchase")] [InlineData("PurchaseReturn")] [InlineData("Sale")]
    [InlineData("SaleReturn")] [InlineData("Thaka")] [InlineData("Adjustment")]
    public async Task G02_HistoryQueryCoversEveryLiveProductUnitBearingPostedEntity(string kind)
    {
        await using var db = new EdgeRetailsDbContext(new DbContextOptionsBuilder<EdgeRetailsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var used = Guid.NewGuid();
        switch (kind)
        {
            case "Purchase": db.PurchaseItems.Add(new PurchaseItem { ProductUnitId = used }); break;
            case "PurchaseReturn": db.PurchaseReturnItems.Add(new PurchaseReturnItem { ProductUnitId = used }); break;
            case "Sale": db.SaleItems.Add(new SaleItem { ProductUnitId = used }); break;
            case "SaleReturn": db.SaleReturnItems.Add(new SaleReturnItem { ProductUnitId = used }); break;
            case "Thaka": db.ThakaMaterialIssueItems.Add(new ThakaMaterialIssueItem { ProductUnitId = used }); break;
            case "Adjustment": db.StockAdjustmentItems.Add(new StockAdjustmentItem { ProductUnitId = used }); break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
        await db.SaveChangesAsync();
        var safety = new ProductManagementReadService(db);
        Assert.True(await safety.HasUnitUsageAsync(used, default));
        Assert.False(await safety.HasUnitUsageAsync(Guid.NewGuid(), default));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(1, 3)]
    [InlineData(2, 1)] [InlineData(2, 2)] [InlineData(2, 3)]
    public async Task G05_ContainerCountEqualsPhysicalIdentitiesAndFrozenBase(int count, int factor)
    {
        var fixture = new ReceiptFixture(TrackingMode.Container, factor);
        var result = await fixture.Handler.HandleAsync(fixture.Command(count));
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(count, result.Value!.CommittedUnits.Count);
        Assert.Equal((decimal)(count * factor), result.Value.BaseQuantity);
        Assert.Equal((decimal)(count * factor), fixture.Doubles.Inventory.Balances[fixture.Product.Id].SellableQty);
        Assert.Equal(count, fixture.Doubles.Inventory.Units.Count);
    }

    [Theory]
    [InlineData("0.5", "catalog.container_quantity_whole")]
    [InlineData("1.5", "catalog.container_quantity_whole")]
    [InlineData("100001", "purchasing.physical_unit_count_out_of_range")]
    [InlineData("2147483648", "purchasing.physical_unit_count_out_of_range")]
    public async Task G05_DirectIntakeRejectsInvalidCountBeforeAnyProvisionalBusinessEffect(string quantity, string code)
    {
        var fixture = new ReceiptFixture(TrackingMode.Container, 2m, 3_000_000_000m);
        var result = await fixture.Handler.HandleAsync(fixture.Command(decimal.Parse(quantity, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
        Assert.Empty(fixture.Doubles.Inventory.Movements);
        Assert.Empty(fixture.Doubles.Inventory.Lots);
        Assert.Empty(fixture.Doubles.Inventory.Units);
        Assert.Empty(fixture.Doubles.Inventory.Balances);
    }

    [Fact]
    public async Task G05_DirectIntakeRejectsDecimalContainerFactor()
    {
        var fixture = new ReceiptFixture(TrackingMode.Container, 1.5m);
        var result = await fixture.Handler.HandleAsync(fixture.Command(1m));
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.container_conversion_whole", result.Error!.Code);
        Assert.Empty(fixture.Doubles.Inventory.Movements);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(901)]
    public async Task G03_InconsistentOrderedBaseSnapshotRejectsBeforeStock(decimal invalidBase)
    {
        var fixture = new ReceiptFixture(TrackingMode.Length, 90m);
        fixture.Item.BaseQuantity = invalidBase;
        var result = await fixture.Handler.HandleAsync(fixture.Command(4m));
        Assert.False(result.IsSuccess);
        Assert.Equal("purchasing.purchase_snapshot_invalid", result.Error!.Code);
        Assert.Empty(fixture.Doubles.Inventory.Movements);
    }

    [Fact]
    public async Task G03_FrozenTenRollOrderCompletesWithFourThenSixDespiteChangedMaster()
    {
        var fixture = new ReceiptFixture(TrackingMode.Length, 90m);
        fixture.ProductUnit.FactorToBaseUnit = 100m;
        Assert.True((await fixture.Handler.HandleAsync(fixture.Command(4m))).IsSuccess);
        Assert.True((await fixture.Handler.HandleAsync(fixture.Command(6m))).IsSuccess);
        Assert.Equal(900m, fixture.Doubles.Inventory.Balances[fixture.Product.Id].SellableQty);
        Assert.Equal(900m, fixture.Doubles.Inventory.Lots.Sum(x => x.ReceivedQuantity));
        Assert.Equal(90m, fixture.Item.FactorToBaseSnapshot);
        var excess = await fixture.Handler.HandleAsync(fixture.Command(1m));
        Assert.False(excess.IsSuccess);
        Assert.Equal("purchasing.intake_exceeds_ordered", excess.Error!.Code);
    }

    [Fact]
    public async Task G03_NineRollsRemainPartialAndWrongUnitRejects()
    {
        var fixture = new ReceiptFixture(TrackingMode.Length, 90m);
        var otherUnit = fixture.Doubles.Catalog.ProductUnits.Values.Single(x =>
            x.ProductId == fixture.Product.Id && x.Id != fixture.ProductUnit.Id);
        var wrong = await fixture.Handler.HandleAsync(fixture.Command(1m) with { ProductUnitId = otherUnit.Id });
        Assert.Equal("purchasing.intake_unit_mismatch", wrong.Error!.Code);
        Assert.True((await fixture.Handler.HandleAsync(fixture.Command(9m))).IsSuccess);
        Assert.Equal(810m, fixture.Doubles.Inventory.Lots.Sum(x => x.ReceivedQuantity));
        Assert.True((await fixture.Handler.HandleAsync(fixture.Command(1m))).IsSuccess);
    }

    [Fact]
    public async Task G04_LandedCostAndTinyForgedOverrideUseServerAuthority()
    {
        var fixture = new ReceiptFixture(TrackingMode.IndividualPiece, 1m);
        fixture.Item.EnteredUnitCost = 1000m;
        fixture.Item.EffectiveBaseUnitCost = 1100m;
        fixture.Item.EffectiveLineCost = 11000m;
        var forged = await fixture.Handler.HandleAsync(fixture.Command(10m) with { EnteredUnitCost = 1000.00001m });
        Assert.False(forged.IsSuccess);
        Assert.Equal("purchasing.receipt_cost_override_not_allowed", forged.Error!.Code);
        Assert.Empty(fixture.Doubles.Inventory.Movements);
        Assert.True((await fixture.Handler.HandleAsync(fixture.Command(10m))).IsSuccess);
        Assert.Equal(11000m, fixture.Doubles.Inventory.CostStates[fixture.Product.Id].TotalInventoryCost);
        Assert.Equal(11000m, fixture.Doubles.Inventory.Units.Sum(x => x.AcquisitionCost));
        Assert.Equal(1100m, Assert.Single(fixture.Doubles.Inventory.Movements).UnitCostSnapshot);
        Assert.Equal(1100m, Assert.Single(fixture.Doubles.Inventory.Lots).OriginalUnitCost);
    }

    [Fact]
    public async Task G04_PartialReceiptsConserveLineDivisionResidualAcrossPoolAndPhysicalCosts()
    {
        var fixture = new ReceiptFixture(TrackingMode.IndividualPiece, 1m, 3m);
        fixture.Item.EffectiveBaseUnitCost = 1.3333m;
        fixture.Item.EffectiveLineCost = 4m;
        Assert.True((await fixture.Handler.HandleAsync(fixture.Command(1m))).IsSuccess);
        Assert.True((await fixture.Handler.HandleAsync(fixture.Command(2m))).IsSuccess);
        Assert.Equal(4m, fixture.Doubles.Inventory.CostStates[fixture.Product.Id].TotalInventoryCost);
        Assert.Equal(4m, fixture.Doubles.Inventory.Units.Sum(x => x.AcquisitionCost));
        Assert.All(fixture.Doubles.Inventory.Lots, x => Assert.Equal(1.3333m, x.OriginalUnitCost));
        Assert.Equal(new[] { 1.333333m, 1.333334m }, fixture.Doubles.Inventory.Lots.Select(x => x.EffectiveUnitCost).ToArray());
        Assert.Equal(4.000001m, fixture.Doubles.Inventory.Lots.Sum(x => x.ReceivedQuantity * x.EffectiveUnitCost));
    }

    [Theory]
    [InlineData(TrackingMode.Quantity)] [InlineData(TrackingMode.IndividualPiece)]
    public async Task G04_LegacyPartialReceiptUsesPersistedCostAndReplaysWithoutReallocation(TrackingMode mode)
    {
        var f = new ReceiptFixture(mode, 1m, 3m);
        f.Item.EffectiveBaseUnitCost = 1.3333m;
        f.Item.EffectiveLineCost = 4m;
        Assert.True((await f.Handler.HandleAsync(f.Command(1m))).IsSuccess);
        var lot = Assert.Single(f.Doubles.Inventory.Lots);
        lot.EffectiveUnitCost = 1.3333m;
        f.Doubles.Inventory.CostStates[f.Product.Id].TotalInventoryCost = 1.3333m;
        foreach (var unit in f.Doubles.Inventory.Units)
        {
            unit.AcquisitionCost = 1.3333m;
        }
        // Bucket exhaustion must not erase original receipt allocation authority.
        Assert.Single(f.Doubles.Inventory.LotBucketBalances).Quantity = 0m;
        var final = f.Command(2m);
        Assert.True((await f.Handler.HandleAsync(final)).IsSuccess);
        Assert.True((await f.Handler.HandleAsync(final)).Value!.WasExisting);
        Assert.Equal(4m, f.Doubles.Inventory.CostStates[f.Product.Id].TotalInventoryCost);
        Assert.Equal(2, f.Doubles.Inventory.Lots.Count);
        Assert.Equal(1.33335m, f.Doubles.Inventory.Lots[1].EffectiveUnitCost);
        Assert.Equal(4m, f.Doubles.Inventory.Lots.Sum(x => x.ReceivedQuantity * x.EffectiveUnitCost));
        if (mode == TrackingMode.IndividualPiece)
        {
            Assert.Equal(4m, f.Doubles.Inventory.Units.Sum(x => x.AcquisitionCost));
        }
    }

    [Fact]
    public async Task G04_SmallCostLargeQuantityCannotCreateNegativeFinalReceiptCost()
    {
        var f = new ReceiptFixture(TrackingMode.Quantity, 1m, 100_000m);
        f.Item.EffectiveBaseUnitCost = 0.0001m;
        f.Item.EffectiveLineCost = 5m;
        Assert.True((await f.Handler.HandleAsync(f.Command(99_999m))).IsSuccess);
        Assert.True((await f.Handler.HandleAsync(f.Command(1m))).IsSuccess);
        Assert.Equal(5m, f.Doubles.Inventory.CostStates[f.Product.Id].TotalInventoryCost);
        Assert.All(f.Doubles.Inventory.Lots, x => Assert.True(x.EffectiveUnitCost >= 0m));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task G02_UsedFactorLocksAndUnusedFactorMayChange(bool used)
    {
        var f = new ReceiptFixture(TrackingMode.Quantity, 2m);
        var handler = new ConfigureProductUnitsHandler(f.Doubles.Catalog, f.Doubles.Transactions,
            f.Doubles.UnitOfWork, safety: new UnitHistory(used), resourceLock: f.Doubles.ResourceLock);
        var result = await handler.HandleAsync(new ConfigureProductUnitsCommand(f.Product.Id,
            [new(f.Product.BaseUnitId, 1m, true, true, true, false, true),
             new(f.ProductUnit.UnitId, 3m, true, true, true, true, false)], ExpectedVersion: f.Product.Version), default);
        Assert.Equal(!used, result.IsSuccess);
        Assert.Equal(used ? 2m : 3m, f.ProductUnit.FactorToBaseUnit);
        if (used)
        {
            Assert.Equal("catalog.product_unit_factor_locked", result.Error!.Code);
        }
    }

    [Fact]
    public async Task G02_StaleProductVersionAndNonUnitBaseFactorReject()
    {
        var f = new ReceiptFixture(TrackingMode.Quantity, 2m);
        var handler = new ConfigureProductUnitsHandler(f.Doubles.Catalog, f.Doubles.Transactions, f.Doubles.UnitOfWork);
        var stale = await handler.HandleAsync(new ConfigureProductUnitsCommand(f.Product.Id,
            [new(f.Product.BaseUnitId, 1m, true, true, true, true, true)], ExpectedVersion: 99), default);
        Assert.Equal("catalog.concurrency_conflict", stale.Error!.Code);
        var badBase = await handler.HandleAsync(new ConfigureProductUnitsCommand(f.Product.Id,
            [new(f.Product.BaseUnitId, 2m, true, true, true, true, true)]), default);
        Assert.Equal("catalog.base_unit_required", badBase.Error!.Code);
    }

    private sealed class UnitHistory(bool used) : IProductCatalogSafetyReadService
    {
        public Task<bool> HasStockOrHistoryAsync(Guid id, CancellationToken ct) => Task.FromResult(used);
        public Task<bool> HasUnitUsageAsync(Guid id, CancellationToken ct) => Task.FromResult(used);
    }

    private sealed class ReceiptFixture
    {
        public Phase2TestDoubles Doubles { get; } = new();
        public Product Product { get; }
        public ProductUnit ProductUnit { get; }
        public PurchaseItem Item { get; }
        public ReceiveProductIntakeHandler Handler { get; }
        private readonly Purchase _purchase;
        public ReceiptFixture(TrackingMode mode, decimal factor, decimal ordered = 10m)
        {
            var supplier = new Supplier { Name = "Pass4 Supplier", DealerCode = "SU1001" };
            Doubles.Parties.AddSupplier(supplier);
            var baseUnit = new Unit { Name = "Base", Symbol = "b" };
            var packUnit = new Unit { Name = "Pack", Symbol = "p" };
            Doubles.Catalog.AddUnit(baseUnit); Doubles.Catalog.AddUnit(packUnit);
            Product = new Product { Name = "Pass4 Product", Sku = "P4-PROD", BaseUnitId = baseUnit.Id, TrackingMode = mode };
            ProductUnit = new ProductUnit { ProductId = Product.Id, UnitId = packUnit.Id, FactorToBaseUnit = factor, CanPurchase = true };
            Doubles.Catalog.AddProduct(Product); Doubles.Catalog.AddProductUnit(ProductUnit);
            Doubles.Catalog.AddProductUnit(new ProductUnit { ProductId = Product.Id, UnitId = baseUnit.Id, FactorToBaseUnit = 1m, CanPurchase = true, CanSell = true });
            _purchase = new Purchase { SupplierId = supplier.Id, PurchaseNumber = "P4-ORDER" };
            Doubles.Purchasing.AddPurchase(_purchase);
            Item = new PurchaseItem { PurchaseId = _purchase.Id, ProductId = Product.Id, ProductUnitId = ProductUnit.Id,
                EnteredQuantity = ordered, FactorToBaseSnapshot = factor, BaseQuantity = ordered * factor,
                EnteredUnitCost = 1000m, EffectiveBaseUnitCost = 1000m, EffectiveLineCost = ordered * factor * 1000m };
            Doubles.Purchasing.AddPurchaseItem(Item);
            Handler = new ReceiveProductIntakeHandler(Doubles.Purchasing, Doubles.Parties, Doubles.Catalog,
                Doubles.Inventory, Doubles.CostAllocator, Doubles.Traceability, Doubles.OperationLock,
                Doubles.ResourceLock, Doubles.Audit, Doubles.Clock, Doubles.Transactions, Doubles.Authorization,
                Doubles.UnitOfWork, physicalUnitCreationAuthority: Doubles.PhysicalUnits);
        }
        public ReceiveProductIntakeCommand Command(decimal count) => new(_purchase.Id, Product.Id,
            ProductUnit.Id, count, null, [], Guid.NewGuid(), Guid.NewGuid());
    }
}
