using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase11PackStocktakeAndRemnantBehavioralTests
{
    private readonly Phase2TestDoubles _fakes = new();

    private CreateStockAdjustmentHandler CreateStockAdjustmentHandler() =>
        new(
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Parties,
            _fakes.Traceability,
            _fakes.ResourceLock,
            _fakes.Audit,
            _fakes.Clock,
            _fakes.Transactions,
            _fakes.Authorization,
            _fakes.UnitOfWork,
            _fakes.OutcomeLedger,
            _fakes.PhysicalUnits,
            _fakes.OperationLock);

    private InventoryConditionService CreateConditionService() =>
        new(
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Clock);

    private RecordSerializedStocktakeHandler CreateRecordStocktakeHandler() =>
        new(
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.Clock,
            _fakes.OperationLock,
            _fakes.OutcomeLedger);

    private PostStocktakeHandler CreatePostStocktakeHandler() =>
        new(
            _fakes.Catalog,
            _fakes.Inventory,
            _fakes.CostAllocator,
            _fakes.Transactions,
            _fakes.UnitOfWork,
            _fakes.Clock,
            _fakes.OperationLock,
            _fakes.OutcomeLedger);

    [Fact]
    public async Task P11_IMP_071_SetPhysicalCount_TargetZero_Legal_Reduces_To_Zero_And_SameCount_Is_NoOp()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Industrial Fitting",
            Sku = "FIT-001",
            TrackingMode = TrackingMode.Quantity,
            ReferencePurchaseCost = 50m,
            IsActive = true
        };
        _fakes.Catalog.AddProduct(product);

        var balance = new StockBalance { ProductId = productId, SellableQty = 10m, Version = 1 };
        _fakes.Inventory.Balances[productId] = balance;

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ReceivedQuantity = 10m,
            OriginalUnitCost = 50m,
            EffectiveUnitCost = 50m,
            CreatedAt = _fakes.Clock.UtcNow
        };
        _fakes.Inventory.Lots.Add(lot);
        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 10m
        });
        _fakes.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 10m,
            TotalInventoryCost = 500m,
            MovingAverageCost = 50m,
            Version = 1
        };

        var handler = CreateStockAdjustmentHandler();

        // 1. Same count test: target = 10, current = 10 -> NoOp
        var sameCountCmd = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    10m,
                    null)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var noOpResult = await handler.HandleAsync(sameCountCmd, CancellationToken.None);
        Assert.True(noOpResult.IsSuccess, noOpResult.Error?.Message);
        Assert.Equal(10m, balance.SellableQty);
        Assert.Empty(_fakes.Inventory.Movements);

        // 2. Target zero test: target = 0, current = 10 -> applied delta = -10
        var zeroCmd = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    0m,
                    null)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var zeroResult = await handler.HandleAsync(zeroCmd, CancellationToken.None);
        Assert.True(zeroResult.IsSuccess, zeroResult.Error?.Message);
        Assert.Equal(0m, balance.SellableQty);
        Assert.Equal(0m, _fakes.Inventory.CostStates[productId].CostedQty);
        Assert.Equal(0m, _fakes.Inventory.CostStates[productId].TotalInventoryCost);
    }

    [Fact]
    public void P11_IMP_072_PackSubLedger_Reconciles_Actual_Measured_Remaining_Contents_Without_Guessing_Capacity()
    {
        var authority = _fakes.PackAllocations;

        // Coil originally 50m, currently recorded remaining = 43m
        var coil = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            TrackingCode = "SUP1-CBL-000001",
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 43m,
            PackState = PackState.Open,
            IsLooseStock = false,
            AcquisitionCost = 500m, // $10/m
            InventoryLotId = Guid.NewGuid(),
            CreatedAt = _fakes.Clock.UtcNow
        };

        var movementId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var now = _fakes.Clock.UtcNow;

        // Stocktake actual measurement: physical coil measured at 38m (5m deficit due to cut loss/wastage)
        var result = authority.ReconcileMeasurement(
            coil,
            measuredRemainingQuantity: 38m,
            minimumSellableIncrement: 1m,
            movementId: movementId,
            actorId: actorId,
            occurredAt: now,
            operationType: PackAllocationOperationTypes.StocktakeAdjustment);

        // Invariant 1: Unit remaining is set to actual measured contents (38m), NOT factory capacity (50m)
        Assert.Equal(38m, coil.RemainingBaseQuantity);
        Assert.Equal(50m, coil.InitialBaseQuantity);
        Assert.Equal(PackState.Open, coil.PackState);
        Assert.Equal(5m, result.DeficitQuantity);
        Assert.False(result.IsShortRemnant);
        Assert.Null(result.WarningMessage);

        // Invariant 2: Immutable PackAllocation audit generated
        Assert.NotNull(result.Allocation);
        var alloc = result.Allocation!;
        Assert.Equal(coil.Id, alloc.PackUnitId);
        Assert.Equal(movementId, alloc.MovementId);
        Assert.Equal(coil.InventoryLotId, alloc.LotId);
        Assert.Equal(PackAllocationOperationTypes.StocktakeAdjustment, alloc.OperationType);
        Assert.Equal(5m, alloc.AllocatedBaseQuantity);
        Assert.Equal(10m, alloc.UnitCostSnapshot);
        Assert.Equal(50m, alloc.TotalCostSnapshot);
        Assert.Equal(38m, alloc.RemainingQuantityAfter);
        Assert.Equal(PackState.Open, alloc.PackStateAfter);
    }

    [Fact]
    public void P11_IMP_073_Positive_Capacity_Overwrite_Without_Provenance_Is_Rejected()
    {
        var authority = _fakes.PackAllocations;

        var coil = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            TrackingCode = "SUP1-CBL-000001",
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 40m,
            PackState = PackState.Open,
            AcquisitionCost = 500m,
            InventoryLotId = Guid.NewGuid()
        };

        // Attempting to measure MORE than current recorded capacity (e.g. 45m > 40m)
        // without explicit positive adjustment provenance fails closed
        var ex = Assert.Throws<BusinessRuleException>(() =>
            authority.ReconcileMeasurement(coil, measuredRemainingQuantity: 45m));

        Assert.Equal("inventory.pack_positive_capacity_overwrite_not_allowed", ex.Code);
    }

    [Fact]
    public async Task P11_IMP_074_Condition_Transfer_Preserves_Exact_Lot_And_Cost_Neutrality()
    {
        var productId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        _fakes.Catalog.AddProduct(new Product
        {
            Id = productId,
            Name = "Precision Tool",
            Sku = "TOOL-01",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        });

        _fakes.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 1m,
            DamagedQty = 0m,
            Version = 1
        };

        _fakes.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 1m,
            TotalInventoryCost = 1500m,
            MovingAverageCost = 1500m,
            Version = 1
        };

        _fakes.Inventory.Lots.Add(new InventoryLot
        {
            Id = lotId,
            ProductId = productId,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 1500m,
            EffectiveUnitCost = 1500m,
            CreatedAt = _fakes.Clock.UtcNow
        });

        _fakes.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lotId,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 1m
        });

        var unit = new InventoryUnit
        {
            Id = unitId,
            ProductId = productId,
            InventoryLotId = lotId,
            TrackingCode = "SUP1-TOOL-000001",
            SerialNumber = "SN-TOOL-001",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1500m,
            OriginType = InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId = Guid.NewGuid()
        };
        _fakes.Inventory.Units.Add(unit);

        var conditionService = CreateConditionService();
        var transferCmd = new TransferInventoryConditionCommand(
            productId,
            From: InventoryBucket.Sellable,
            To: InventoryBucket.Damaged,
            BaseQuantity: 1m,
            ActorId: actorId,
            Reason: "Handling drop damage",
            InventoryUnitIds: [unitId]);

        var result = await conditionService.TransferAsync(transferCmd, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        // Verification 1: Balance updated neutrally
        var balance = _fakes.Inventory.Balances[productId];
        Assert.Equal(0m, balance.SellableQty);
        Assert.Equal(1m, balance.DamagedQty);

        // Verification 2: Moving average cost remains completely unaffected (valuation neutral)
        var costState = _fakes.Inventory.CostStates[productId];
        Assert.Equal(1500m, costState.TotalInventoryCost);
        Assert.Equal(1500m, costState.MovingAverageCost);

        // Verification 3: Unit status updated to Damaged
        Assert.Equal(InventoryUnitStatus.Damaged, unit.Status);

        // Verification 4: Exact lot balance transferred from Sellable to Damaged
        var lotSellable = _fakes.Inventory.LotBucketBalances.FirstOrDefault(b => b.LotId == lotId && b.StockBucket == InventoryBucket.Sellable);
        var lotDamaged = _fakes.Inventory.LotBucketBalances.FirstOrDefault(b => b.LotId == lotId && b.StockBucket == InventoryBucket.Damaged);
        Assert.Equal(0m, lotSellable?.Quantity ?? 0m);
        Assert.Equal(1m, lotDamaged?.Quantity ?? 0m);
    }

    [Fact]
    public void P11_IMP_075_Short_Remnant_Raises_Warning_Preserves_Usable_Measure_Without_Auto_Scrap()
    {
        var authority = _fakes.PackAllocations;

        // Coil measured down to 0.75m (below 1m minimum sellable increment)
        var shortCoil = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            TrackingCode = "SUP1-CBL-000002",
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 5m,
            PackState = PackState.Open,
            AcquisitionCost = 500m,
            InventoryLotId = Guid.NewGuid()
        };

        var result = authority.ReconcileMeasurement(
            shortCoil,
            measuredRemainingQuantity: 0.75m,
            minimumSellableIncrement: 1.0m,
            movementId: Guid.NewGuid(),
            actorId: Guid.NewGuid(),
            occurredAt: _fakes.Clock.UtcNow);

        // Invariant A: Usable short piece is NOT auto-scrapped!
        Assert.Equal(0.75m, shortCoil.RemainingBaseQuantity);
        Assert.Equal(PackState.Open, shortCoil.PackState);
        Assert.Equal(InventoryUnitStatus.InStock, shortCoil.Status);

        // Invariant B: Short remnant evaluation flags review warning
        Assert.True(result.IsShortRemnant);
        Assert.NotNull(result.WarningMessage);
        Assert.Contains("below the minimum sellable increment", result.WarningMessage);
    }

    [Fact]
    public void P11_IMP_076_Authorized_Scrap_Remnant_Derecognizes_Unit_And_Records_Scrap_Allocation()
    {
        var authority = _fakes.PackAllocations;

        var remnantUnit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = Guid.NewGuid(),
            TrackingCode = "SUP1-CBL-000003",
            Status = InventoryUnitStatus.InStock,
            InitialBaseQuantity = 50m,
            RemainingBaseQuantity = 0.5m,
            PackState = PackState.Open,
            AcquisitionCost = 500m, // $10/m
            InventoryLotId = Guid.NewGuid()
        };

        var movementId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var now = _fakes.Clock.UtcNow;

        var scrapAllocation = authority.ScrapRemnant(
            remnantUnit,
            movementId,
            actorId,
            now,
            reason: "Unusable off-cut damaged fragment");

        // Invariant 1: Unit remaining set to 0 and consumed
        Assert.Equal(0m, remnantUnit.RemainingBaseQuantity);
        Assert.Equal(PackState.Consumed, remnantUnit.PackState);

        // Invariant 2: Immutable PackAllocation with ScrapRemnant operation type
        Assert.Equal(PackAllocationOperationTypes.ScrapRemnant, scrapAllocation.OperationType);
        Assert.Equal(0.5m, scrapAllocation.AllocatedBaseQuantity);
        Assert.Equal(10m, scrapAllocation.UnitCostSnapshot);
        Assert.Equal(5m, scrapAllocation.TotalCostSnapshot);
        Assert.Equal(0m, scrapAllocation.RemainingQuantityAfter);
        Assert.Equal(PackState.Consumed, scrapAllocation.PackStateAfter);
    }

    [Fact]
    public async Task P11_IMP_078_Active_Stocktake_Blocks_Concurrent_Adjustments_And_Condition_Transfers()
    {
        var productId = Guid.NewGuid();
        _fakes.Catalog.AddProduct(new Product
        {
            Id = productId,
            Name = "Audited Cable",
            Sku = "AUD-01",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        });

        _fakes.Inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 100m };
        _fakes.Inventory.BlockedStocktakeProducts.Add(productId); // Active stocktake locks product

        var adjustmentHandler = CreateStockAdjustmentHandler();
        var adjCmd = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    5m,
                    null)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var adjResult = await adjustmentHandler.HandleAsync(adjCmd, CancellationToken.None);
        Assert.False(adjResult.IsSuccess);
        Assert.Equal("inventory.stocktake_in_progress", adjResult.Error?.Code);

        var conditionService = CreateConditionService();
        var condCmd = new TransferInventoryConditionCommand(
            productId,
            From: InventoryBucket.Sellable,
            To: InventoryBucket.Damaged,
            BaseQuantity: 5m,
            ActorId: Guid.NewGuid(),
            Reason: "Audit damage");

        var condResult = await conditionService.TransferAsync(condCmd, CancellationToken.None);
        Assert.False(condResult.IsSuccess);
        Assert.Equal("inventory.stocktake_in_progress", condResult.Error?.Code);
    }
}
