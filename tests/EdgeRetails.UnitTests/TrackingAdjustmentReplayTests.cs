using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;

namespace EdgeRetails.UnitTests;

public sealed class TrackingAdjustmentReplayTests
{
    [Fact]
    public async Task LegacyCommittedAdjustmentWithoutPayloadOutcome_FailsClosedWithoutAllocating()
    {
        var doubles = new Phase2TestDoubles();
        var operation = Guid.NewGuid();
        doubles.Inventory.AddMovement(new InventoryMovement { CorrelationId = operation, ProductId = Guid.NewGuid() });
        var handler = new CreateStockAdjustmentHandler(doubles.Catalog, doubles.Inventory,
            doubles.CostAllocator, doubles.Parties, doubles.Traceability, doubles.ResourceLock,
            doubles.Audit, doubles.Clock, doubles.Transactions, doubles.Authorization,
            doubles.UnitOfWork, doubles.OutcomeLedger, doubles.PhysicalUnits, doubles.OperationLock);
        var result = await handler.HandleAsync(new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
            [new StockAdjustmentItemCommand(Guid.NewGuid(), null, StockAdjustmentDirection.Increase, InventoryBucket.Sellable, 1m, null)],
            Guid.NewGuid(), operation), CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("idempotency.legacy_adjustment_requires_reconciliation", result.Error?.Code);
        Assert.Empty(doubles.Inventory.Units);
        Assert.Empty(doubles.Inventory.StockAdjustments);
        Assert.Single(doubles.Inventory.Movements);
        Assert.Null(await doubles.OutcomeLedger.GetOutcomeAsync(operation, CancellationToken.None));
    }

    [Fact]
    public async Task ContainerAdjustment_UsesPhysicalCountAndNegativeAdjustmentPreservesIdentity()
    {
        var doubles = new Phase2TestDoubles();
        var product = new Product { Name = "Container", Sku = "CONT", TrackingMode = TrackingMode.Container, IsActive = true };
        var supplier = new Supplier { Name = "Supplier", DealerCode = "SU1", IsActive = true };
        var productUnit = new ProductUnit { ProductId = product.Id, FactorToBaseUnit = 2m, IsActive = true };
        doubles.Catalog.AddProduct(product);
        doubles.Catalog.AddProductUnit(productUnit);
        doubles.Parties.AddSupplier(supplier);
        var handler = new CreateStockAdjustmentHandler(doubles.Catalog, doubles.Inventory,
            doubles.CostAllocator, doubles.Parties, doubles.Traceability, doubles.ResourceLock,
            doubles.Audit, doubles.Clock, doubles.Transactions, doubles.Authorization,
            doubles.UnitOfWork, doubles.OutcomeLedger, doubles.PhysicalUnits, doubles.OperationLock);
        var item = new StockAdjustmentItemCommand(product.Id, productUnit.Id, StockAdjustmentDirection.Increase,
            InventoryBucket.Sellable, 2m, 100m, supplier.Id);
        var command = new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
            [item], Guid.NewGuid(), Guid.NewGuid());
        var first = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var physical = Assert.Single(doubles.Inventory.Units);
        Assert.Equal(200m, physical.AcquisitionCost);
        var identity = (physical.Id, physical.TrackingCode, physical.ItemSequence);
        productUnit.IsActive = false;
        var replay = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(first.Value, replay.Value);
        Assert.Single(doubles.Inventory.Units);
        productUnit.IsActive = true;
        var negative = await handler.HandleAsync(command with
        {
            CorrelationId = Guid.NewGuid(),
            Reason = StockAdjustmentReason.Lost,
            Items = [item with { Direction = StockAdjustmentDirection.Decrease, InventoryUnitIds = [physical.Id] }]
        }, CancellationToken.None);
        Assert.True(negative.IsSuccess, negative.Error?.Message);
        Assert.Equal(InventoryUnitStatus.Missing, physical.Status);
        Assert.Equal(identity, (physical.Id, physical.TrackingCode, physical.ItemSequence));
        Assert.Equal(0m, doubles.Inventory.Balances[product.Id].SellableQty);
        Assert.Equal(0m, doubles.Inventory.CostStates[product.Id].CostedQty);
        Assert.Equal(0m, doubles.Inventory.CostStates[product.Id].TotalInventoryCost);
        Assert.Equal(0m, Assert.Single(doubles.Inventory.LotBucketBalances).Quantity);
        Assert.Single(doubles.Inventory.Units);
    }

    [Theory]
    [InlineData(TrackingMode.Serialized)]
    [InlineData(TrackingMode.IndividualPiece)]
    [InlineData(TrackingMode.Container)]
    public async Task IdentitylessPhysicalAdjustment_ReplayPreservesUnitMovementAndSequence(TrackingMode mode)
    {
        var doubles = new Phase2TestDoubles();
        var product = new Product { Name = "Tracked", Sku = "TRACKED", TrackingMode = mode, IsActive = true };
        var supplier = new Supplier { Name = "Supplier", DealerCode = "SU1", IsActive = true };
        doubles.Catalog.AddProduct(product);
        doubles.Parties.AddSupplier(supplier);
        Guid? productUnitId = null;
        if (mode == TrackingMode.Container)
        {
            var productUnit = new ProductUnit { ProductId = product.Id, FactorToBaseUnit = 1m, IsActive = true };
            doubles.Catalog.AddProductUnit(productUnit);
            productUnitId = productUnit.Id;
        }
        var handler = new CreateStockAdjustmentHandler(doubles.Catalog, doubles.Inventory,
            doubles.CostAllocator, doubles.Parties, doubles.Traceability, doubles.ResourceLock,
            doubles.Audit, doubles.Clock, doubles.Transactions, doubles.Authorization,
            doubles.UnitOfWork, doubles.OutcomeLedger, doubles.PhysicalUnits, doubles.OperationLock);
        var command = new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
            [new StockAdjustmentItemCommand(product.Id, productUnitId, StockAdjustmentDirection.Increase,
                InventoryBucket.Sellable, 1m, 100m, supplier.Id)], Guid.NewGuid(), Guid.NewGuid());

        var first = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        var unit = Assert.Single(doubles.Inventory.Units);
        var identity = (unit.Id, unit.TrackingCode, unit.ItemSequence);
        var authority = await doubles.Traceability.GetSupplierProductForUpdateAsync(supplier.Id, product.Id, CancellationToken.None);
        Assert.Equal(2, authority!.NextItemSequence);

        var replay = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(first.Value, replay.Value);
        var retained = Assert.Single(doubles.Inventory.Units);
        Assert.Equal(identity, (retained.Id, retained.TrackingCode, retained.ItemSequence));
        Assert.Single(doubles.Inventory.Movements);
        Assert.Single(doubles.Inventory.StockAdjustments);
        Assert.Equal(1m, doubles.Inventory.Balances[product.Id].SellableQty);
        Assert.Equal(2, authority.NextItemSequence);
        Assert.Contains(command.CorrelationId, doubles.OperationLock.LockedOperations.Keys);

        var changed = command with { Items = [command.Items[0] with { BaseQuantity = 2m }] };
        var mismatch = await handler.HandleAsync(changed, CancellationToken.None);
        Assert.False(mismatch.IsSuccess);
        Assert.Equal("idempotency.payload_mismatch", mismatch.Error?.Code);
        Assert.Single(doubles.Inventory.Units);
        Assert.Single(doubles.Inventory.Movements);
        Assert.Equal(2, authority.NextItemSequence);
    }

    [Fact]
    public async Task ManufacturerEquivalentAdjustmentReplay_ReturnsOriginalPhysicalIdentity()
    {
        var doubles = new Phase2TestDoubles();
        var product = new Product { Name = "Serialized", Sku = "SER", TrackingMode = TrackingMode.Serialized, SerialTrackingEnabled = true, IsActive = true };
        var supplier = new Supplier { Name = "Supplier", DealerCode = "SU1", IsActive = true };
        doubles.Catalog.AddProduct(product);
        doubles.Parties.AddSupplier(supplier);
        var handler = new CreateStockAdjustmentHandler(doubles.Catalog, doubles.Inventory,
            doubles.CostAllocator, doubles.Parties, doubles.Traceability, doubles.ResourceLock,
            doubles.Audit, doubles.Clock, doubles.Transactions, doubles.Authorization,
            doubles.UnitOfWork, doubles.OutcomeLedger, doubles.PhysicalUnits, doubles.OperationLock);
        var item = new StockAdjustmentItemCommand(product.Id, null, StockAdjustmentDirection.Increase,
            InventoryBucket.Sellable, 1m, 100m, supplier.Id, [new SerializedAdjustmentUnitCommand("ab-12")]);
        var command = new CreateStockAdjustmentCommand(StockAdjustmentMode.Delta, StockAdjustmentReason.Other,
            [item], Guid.NewGuid(), Guid.NewGuid());
        var first = await handler.HandleAsync(command, CancellationToken.None);
        var replay = await handler.HandleAsync(command with { Items = [item with { SerializedUnits = [new SerializedAdjustmentUnitCommand(" ａｂ-12 ")] }] }, CancellationToken.None);
        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.True(replay.IsSuccess, replay.Error?.Message);
        Assert.Equal(first.Value, replay.Value);
        Assert.Single(doubles.Inventory.Units);
        Assert.Single(doubles.Inventory.Movements);
    }
}
