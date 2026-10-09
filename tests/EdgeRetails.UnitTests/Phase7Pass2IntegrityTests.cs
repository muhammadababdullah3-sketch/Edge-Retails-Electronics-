using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Application.Features.Thaka;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;
using EdgeRetails.Domain.Thaka;
using Xunit;

namespace EdgeRetails.UnitTests;

public sealed class Phase7Pass2IntegrityTests
{
    private readonly Phase2TestDoubles _doubles = new();

    private CreateStockAdjustmentHandler CreateStockAdjustmentHandler() =>
        new(
            _doubles.Catalog,
            _doubles.Inventory,
            _doubles.CostAllocator,
            _doubles.Parties,
            _doubles.Traceability,
            _doubles.ResourceLock,
            _doubles.Audit,
            _doubles.Clock,
            _doubles.Transactions,
            _doubles.Authorization,
            _doubles.UnitOfWork,
            _doubles.OutcomeLedger,
            _doubles.PhysicalUnits,
            _doubles.OperationLock);

    private InventoryConditionService CreateConditionService() =>
        new(
            _doubles.Catalog,
            _doubles.Inventory,
            _doubles.CostAllocator,
            _doubles.Clock);

    private IssueThakaMaterialHandler CreateIssueThakaMaterialHandler() =>
        new(
            _doubles.Thaka,
            _doubles.Parties,
            _doubles.Catalog,
            _doubles.Inventory,
            _doubles.CostAllocator,
            _doubles.OperationLock,
            _doubles.ResourceLock,
            _doubles.Numbers,
            _doubles.Audit,
            _doubles.Clock,
            _doubles.Transactions,
            _doubles.Authorization,
            _doubles.UnitOfWork,
            _doubles.OutcomeLedger);

    private ReverseThakaMaterialHandler CreateReverseThakaMaterialHandler() =>
        new(
            _doubles.Thaka,
            _doubles.Inventory,
            _doubles.CostAllocator,
            _doubles.OperationLock,
            _doubles.ResourceLock,
            _doubles.Numbers,
            _doubles.Audit,
            _doubles.Clock,
            _doubles.Transactions,
            _doubles.Authorization,
            _doubles.UnitOfWork,
            _doubles.OutcomeLedger);

    // =========================================================================
    // 1. P7-N03: InventoryUnitAccountingPolicy alignment for Scrapped and IssuedThaka
    // =========================================================================

    [Fact]
    public void P7_N03_InventoryUnitAccountingPolicy_IssuedThaka_DoesNotContributeToCostState()
    {
        var rule = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.IssuedThaka);
        Assert.False(rule.ContributesToStockBalance);
        Assert.Null(rule.AuthoritativeBucket);
        Assert.False(rule.ContributesToProductCostState);
        Assert.Equal(BusinessOwner.Shop, rule.BusinessOwner);
    }

    [Fact]
    public void P7_N03_InventoryUnitAccountingPolicy_Scrapped_DoesNotContributeToCostState()
    {
        var rule = InventoryUnitAccountingPolicy.GetRule(InventoryUnitStatus.Scrapped);
        Assert.True(rule.ContributesToStockBalance);
        Assert.Equal(InventoryBucket.Scrap, rule.AuthoritativeBucket);
        Assert.False(rule.ContributesToProductCostState);
        Assert.True(rule.IsTerminal);
    }

    // =========================================================================
    // 2. F01: StockAdjustment SetPhysicalCount Semantics
    // =========================================================================

    [Fact]
    public async Task F01_StockAdjustment_SetPhysicalCount_QuantityMode_ReducesStockCorrectly()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Copper Wire Bulk",
            Sku = "WIRE-BULK",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 10m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 10m,
            TotalInventoryCost = 1000m,
            MovingAverageCost = 100m
        };
        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ReceivedQuantity = 10m,
            OriginalUnitCost = 100m,
            EffectiveUnitCost = 100m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 10m
        });

        var handler = CreateStockAdjustmentHandler();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    4m, // Target physical count is 4, so delta = 4 - 10 = -6
                    null)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        var balance = _doubles.Inventory.Balances[productId];
        Assert.Equal(4m, balance.SellableQty);

        var costState = _doubles.Inventory.CostStates[productId];
        Assert.Equal(4m, costState.CostedQty);
        Assert.Equal(400m, costState.TotalInventoryCost);
    }

    [Fact]
    public async Task F01_StockAdjustment_SetPhysicalCount_TargetZero_ReducesStockToZero()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Clearance Item",
            Sku = "CLR-01",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 5m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 5m,
            TotalInventoryCost = 250m,
            MovingAverageCost = 50m
        };
        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ReceivedQuantity = 5m,
            OriginalUnitCost = 50m,
            EffectiveUnitCost = 50m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 5m
        });

        var handler = CreateStockAdjustmentHandler();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    0m, // Target physical count is 0
                    null)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        var balance = _doubles.Inventory.Balances[productId];
        Assert.Equal(0m, balance.SellableQty);

        var costState = _doubles.Inventory.CostStates[productId];
        Assert.Equal(0m, costState.CostedQty);
        Assert.Equal(0m, costState.TotalInventoryCost);
    }

    [Fact]
    public async Task F01_StockAdjustment_SetPhysicalCount_SameCount_IsNoOpWithoutFakeMovements()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Unchanged Item",
            Sku = "SAME-01",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 7m
        };

        var handler = CreateStockAdjustmentHandler();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    7m, // Target physical count = 7 (same as current)
                    null)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(_doubles.Inventory.Movements);
        Assert.Equal(7m, _doubles.Inventory.Balances[productId].SellableQty);
    }

    [Fact]
    public async Task F01_StockAdjustment_SetPhysicalCount_PositiveAdjustment_OnPhysicalUnit_FailsClosed()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Smartphone",
            Sku = "PHONE-01",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 1m
        };

        var handler = CreateStockAdjustmentHandler();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    2m, // Target 2 > Current 1
                    null)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.physical_positive_adjustment_unsupported", result.Error?.Code);
    }

    [Fact]
    public async Task F01_StockAdjustment_SetPhysicalCount_NegativeAdjustment_TransitionsUnitsToMissing()
    {
        var productId = Guid.NewGuid();
        var supplier = new Supplier { Name = "TechSupplier", DealerCode = "TS1", IsActive = true };
        _doubles.Parties.AddSupplier(supplier);

        var product = new Product
        {
            Id = productId,
            Name = "Laptop",
            Sku = "LAP-01",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ReceivedQuantity = 2m,
            OriginalUnitCost = 1000m,
            EffectiveUnitCost = 1000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 2m
        });
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 2m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 2m,
            TotalInventoryCost = 2000m,
            MovingAverageCost = 1000m
        };

        var unit1 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "TS1-LAP-01-000001",
            SerialNumber = "SN-001",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m
        };
        var unit2 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "TS1-LAP-01-000002",
            SerialNumber = "SN-002",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 1000m
        };
        _doubles.Inventory.Units.Add(unit1);
        _doubles.Inventory.Units.Add(unit2);

        var handler = CreateStockAdjustmentHandler();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.Lost,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    1m, // Target 1, so 1 unit removed
                    null,
                    InventoryUnitIds: [unit1.Id])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        Assert.Equal(InventoryUnitStatus.Missing, unit1.Status);
        Assert.Equal(InventoryUnitStatus.InStock, unit2.Status);
        Assert.Equal(1m, _doubles.Inventory.Balances[productId].SellableQty);
        Assert.Equal(1000m, _doubles.Inventory.CostStates[productId].TotalInventoryCost);
    }

    // =========================================================================
    // 3. D-ADJ-1: Container Adjustment ProductUnit / Pack-Factor Authority
    // =========================================================================

    [Fact]
    public async Task D_ADJ_1_ContainerAdjustment_MissingProductUnit_FailsWithProductUnitRequired()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Tiles Carton",
            Sku = "TILE-CART",
            TrackingMode = TrackingMode.Container,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);

        var handler = CreateStockAdjustmentHandler();
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.PhysicalCountCorrection,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null, // Missing ProductUnitId!
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    100m)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.product_unit_required", result.Error?.Code);
    }

    [Fact]
    public async Task D_ADJ_1_ContainerAdjustment_PackFactorAuthority_ScalesDeltaCorrectly()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Cable Drums",
            Sku = "DRUM-50",
            TrackingMode = TrackingMode.Container,
            IsActive = true
        };
        var productUnit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            FactorToBaseUnit = 50m,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Catalog.AddProductUnit(productUnit);

        var recvMovement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            MovementType = InventoryMovementType.PurchaseIn,
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.AddMovement(recvMovement);
        _doubles.Inventory.MovementEffects.Add(new InventoryMovementEffect
        {
            MovementId = recvMovement.Id,
            StockBucket = InventoryBucket.Sellable,
            QuantityDelta = 150m
        });

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SourceMovementId = recvMovement.Id,
            ReceivedQuantity = 150m, // 3 drums = 150 base meters
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 150m
        });
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 150m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 150m,
            TotalInventoryCost = 1500m,
            MovingAverageCost = 10m
        };

        var drum1 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "DRUM-001",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m
        };
        var drum2 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "DRUM-002",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m
        };
        var drum3 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "DRUM-003",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 500m
        };
        _doubles.Inventory.Units.AddRange([drum1, drum2, drum3]);
        _doubles.Inventory.MovementUnits.AddRange([
            new InventoryMovementUnit { MovementId = recvMovement.Id, InventoryUnitId = drum1.Id },
            new InventoryMovementUnit { MovementId = recvMovement.Id, InventoryUnitId = drum2.Id },
            new InventoryMovementUnit { MovementId = recvMovement.Id, InventoryUnitId = drum3.Id }
        ]);

        var handler = CreateStockAdjustmentHandler();
        // Target physical count = 1 drum (= 50 base units). Current = 150 base units.
        // Base delta = 50 - 150 = -100 base units. Must remove 2 drums.
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.SetPhysicalCount,
            StockAdjustmentReason.Lost,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    productUnit.Id,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    1m, // Entered target 1 drum, converted by ProductUnit factor50.
                    null,
                    InventoryUnitIds: [drum1.Id, drum2.Id])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        Assert.Equal(50m, _doubles.Inventory.Balances[productId].SellableQty);
        Assert.Equal(InventoryUnitStatus.Missing, drum1.Status);
        Assert.Equal(InventoryUnitStatus.Missing, drum2.Status);
        Assert.Equal(InventoryUnitStatus.InStock, drum3.Status);
    }

    // =========================================================================
    // 4. F02: Condition-Transfer Scrap Carrying-Value Arithmetic
    // =========================================================================

    [Fact]
    public async Task F02_ConditionTransfer_ToScrap_DirectExactCarryingValueRemoval()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Monitor",
            Sku = "MON-01",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 12000m,
            EffectiveUnitCost = 12000m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Damaged,
            Quantity = 1m
        });
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            DamagedQty = 1m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 1m,
            TotalInventoryCost = 12000m,
            MovingAverageCost = 12000m
        };

        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "MON-001",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 12000m
        };
        _doubles.Inventory.Units.Add(unit);

        var service = CreateConditionService();
        var command = new TransferInventoryConditionCommand(
            productId,
            InventoryBucket.Damaged,
            InventoryBucket.Scrap,
            1m,
            Guid.NewGuid(),
            "Damaged beyond repair",
            InventoryUnitIds: [unit.Id]);

        var result = await service.TransferAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        Assert.Equal(InventoryUnitStatus.Scrapped, unit.Status);
        var costState = _doubles.Inventory.CostStates[productId];
        Assert.Equal(0m, costState.TotalInventoryCost);
        Assert.Equal(0m, costState.CostedQty);

        var scrapMovement = Assert.Single(_doubles.Inventory.Movements);
        Assert.Equal(12000m, scrapMovement.RecognizedLossAmount);
    }

    [Fact]
    public async Task F02_ConditionTransfer_Container_ToScrap_PreservesTotalCarryingValue()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Bolt Pack",
            Sku = "BOLT-PK",
            TrackingMode = TrackingMode.Container,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);

        var recvMovement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            MovementType = InventoryMovementType.PurchaseIn,
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.AddMovement(recvMovement);
        _doubles.Inventory.MovementEffects.Add(new InventoryMovementEffect
        {
            MovementId = recvMovement.Id,
            StockBucket = InventoryBucket.Damaged,
            QuantityDelta = 50m
        });

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SourceMovementId = recvMovement.Id,
            ReceivedQuantity = 50m,
            OriginalUnitCost = 10m,
            EffectiveUnitCost = 10m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Damaged,
            Quantity = 50m
        });
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            DamagedQty = 50m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 50m,
            TotalInventoryCost = 500m,
            MovingAverageCost = 10m
        };

        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "BOLT-BOX-001",
            Status = InventoryUnitStatus.Damaged,
            AcquisitionCost = 500m // Total pack cost is 500
        };
        _doubles.Inventory.Units.Add(unit);
        _doubles.Inventory.MovementUnits.Add(new InventoryMovementUnit
        {
            MovementId = recvMovement.Id,
            InventoryUnitId = unit.Id
        });

        var service = CreateConditionService();
        var command = new TransferInventoryConditionCommand(
            productId,
            InventoryBucket.Damaged,
            InventoryBucket.Scrap,
            50m,
            Guid.NewGuid(),
            "Water damage",
            InventoryUnitIds: [unit.Id]);

        var result = await service.TransferAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        Assert.Equal(0m, _doubles.Inventory.CostStates[productId].TotalInventoryCost);
        var movement = _doubles.Inventory.Movements.Last();
        Assert.Equal(500m, movement.RecognizedLossAmount);
    }

    // =========================================================================
    // 5. F03: Physical Condition Transfer Quantity Integrity
    // =========================================================================

    [Fact]
    public async Task F03_ConditionTransfer_PhysicalTracking_FractionalQuantity_RejectedBeforeRounding()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Serialized Router",
            Sku = "ROUTER-01",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 10m
        };

        var service = CreateConditionService();
        var command = new TransferInventoryConditionCommand(
            productId,
            InventoryBucket.Sellable,
            InventoryBucket.Damaged,
            1.5m, // Fractional quantity on Serialized product
            Guid.NewGuid(),
            "Physical damage");

        var result = await service.TransferAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.serialized_quantity_whole", result.Error?.Code);
    }

    [Fact]
    public async Task F03_ConditionTransfer_PhysicalTracking_UnitCountMismatch_Rejected()
    {
        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Serialized Switch",
            Sku = "SW-01",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 10m
        };

        var unit = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            TrackingCode = "SW-001",
            Status = InventoryUnitStatus.InStock
        };
        _doubles.Inventory.Units.Add(unit);

        var service = CreateConditionService();
        var command = new TransferInventoryConditionCommand(
            productId,
            InventoryBucket.Sellable,
            InventoryBucket.Damaged,
            2m, // Base quantity 2, but only 1 unit provided
            Guid.NewGuid(),
            "Damage test",
            InventoryUnitIds: [unit.Id]);

        var result = await service.TransferAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.serialized_unit_count", result.Error?.Code);
    }

    // =========================================================================
    // 6. F04: Thaka UOM / BaseQuantity Costing Integrity
    // =========================================================================

    [Fact]
    public async Task F04_Thaka_IssueMaterial_ChargeAndCost_CalculatedOnBaseQuantity()
    {
        var customerId = Guid.NewGuid();
        _doubles.Parties.AddCustomer(new Customer { Id = customerId, Name = "Test Customer 1", IsActive = true });
        var project = new ThakaProject
        {
            Id = Guid.NewGuid(),
            ProjectNumber = "PRJ-001",
            CustomerId = customerId,
            ProjectName = "Industrial Wiring",
            Status = ThakaProjectStatus.Active
        };
        _doubles.Thaka.AddProject(project);

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Heavy Cable",
            Sku = "CBL-HVY",
            TrackingMode = TrackingMode.Length,
            DefaultSalePrice = 100m,
            IsActive = true
        };
        var unit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            FactorToBaseUnit = 25m, // 1 roll = 25 meters
            CanUseInThaka = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Catalog.AddProductUnit(unit);

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            ReceivedQuantity = 100m,
            OriginalUnitCost = 40m, // 40 per base meter
            EffectiveUnitCost = 40m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 100m
        });
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 100m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 100m,
            TotalInventoryCost = 4000m,
            MovingAverageCost = 40m
        };

        var handler = CreateIssueThakaMaterialHandler();
        // Issue 2 rolls (= 50 base meters) @ authoritative charge 100 per meter
        var command = new IssueThakaMaterialCommand(
            Guid.NewGuid(),
            project.Id,
            Guid.NewGuid(),
            "Cable delivery",
            [
                new IssueThakaMaterialLineInput(
                    productId,
                    unit.Id,
                    2m, // EnteredQuantity = 2 rolls
                    100m, // Authoritative charge per base meter
                    [])
            ]);

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);

        // Expected Charge: 50 base meters * 100 = 5000 (NOT 2 * 100 = 200)
        Assert.Equal(5000m, result.Value!.TotalCharge);

        // Expected Cost: 50 base meters * 40 = 2000 (NOT 2 * 40 = 80)
        Assert.Equal(2000m, result.Value!.TotalCost);

        Assert.Equal(50m, _doubles.Inventory.Balances[productId].SellableQty);
    }

    // =========================================================================
    // 7. P7-N02: Cross-Line Duplicate InventoryUnitId Rejection
    // =========================================================================

    [Fact]
    public async Task P7_N02_Thaka_IssueMaterial_DuplicateUnitAcrossLines_Rejected()
    {
        var customerId = Guid.NewGuid();
        _doubles.Parties.AddCustomer(new Customer { Id = customerId, Name = "Test Customer 2", IsActive = true });
        var project = new ThakaProject
        {
            Id = Guid.NewGuid(),
            ProjectNumber = "PRJ-002",
            CustomerId = customerId,
            ProjectName = "Substation Assembly",
            Status = ThakaProjectStatus.Active
        };
        _doubles.Thaka.AddProject(project);

        var productA = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Transformer Core A",
            Sku = "TRANS-01A",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = 5000m,
            IsActive = true
        };
        var unitA = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productA.Id,
            FactorToBaseUnit = 1m,
            CanUseInThaka = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(productA);
        _doubles.Catalog.AddProductUnit(unitA);

        var productB = new Product
        {
            Id = Guid.NewGuid(),
            Name = "Transformer Core B",
            Sku = "TRANS-01B",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            DefaultSalePrice = 5000m,
            IsActive = true
        };
        var unitB = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productB.Id,
            FactorToBaseUnit = 1m,
            CanUseInThaka = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(productB);
        _doubles.Catalog.AddProductUnit(unitB);

        var sharedUnitId = Guid.NewGuid();

        var handler = CreateIssueThakaMaterialHandler();
        var command = new IssueThakaMaterialCommand(
            Guid.NewGuid(),
            project.Id,
            Guid.NewGuid(),
            "Duplicate unit test",
            [
                new IssueThakaMaterialLineInput(productA.Id, unitA.Id, 1m, 5000m, [sharedUnitId]),
                new IssueThakaMaterialLineInput(productB.Id, unitB.Id, 1m, 5000m, [sharedUnitId])
            ]);

        var result = await handler.HandleAsync(command, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal("thaka.serial_selection_invalid", result.Error?.Code);
    }

    [Fact]
    public async Task P7_N02_Thaka_IssueAndReversal_Container_SymmetricBalanceAndWip()
    {
        var customerId = Guid.NewGuid();
        _doubles.Parties.AddCustomer(new Customer { Id = customerId, Name = "Test Customer 3", IsActive = true });
        var project = new ThakaProject
        {
            Id = Guid.NewGuid(),
            ProjectNumber = "PRJ-003",
            CustomerId = customerId,
            ProjectName = "Grid Installation",
            Status = ThakaProjectStatus.Active
        };
        _doubles.Thaka.AddProject(project);

        var productId = Guid.NewGuid();
        var product = new Product
        {
            Id = productId,
            Name = "Conduit Pipe Pack",
            Sku = "PIPE-PK",
            TrackingMode = TrackingMode.Container,
            DefaultSalePrice = 200m,
            IsActive = true
        };
        var unit = new ProductUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            FactorToBaseUnit = 10m,
            CanUseInThaka = true,
            IsActive = true
        };
        _doubles.Catalog.AddProduct(product);
        _doubles.Catalog.AddProductUnit(unit);

        var recvMovement = new InventoryMovement
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            MovementType = InventoryMovementType.PurchaseIn,
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.AddMovement(recvMovement);
        _doubles.Inventory.MovementEffects.Add(new InventoryMovementEffect
        {
            MovementId = recvMovement.Id,
            StockBucket = InventoryBucket.Sellable,
            QuantityDelta = 10m
        });

        var lot = new InventoryLot
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            SourceMovementId = recvMovement.Id,
            ReceivedQuantity = 10m,
            OriginalUnitCost = 15m,
            EffectiveUnitCost = 15m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _doubles.Inventory.Lots.Add(lot);
        _doubles.Inventory.LotBucketBalances.Add(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 10m
        });
        _doubles.Inventory.Balances[productId] = new StockBalance
        {
            ProductId = productId,
            SellableQty = 10m
        };
        _doubles.Inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 10m,
            TotalInventoryCost = 150m,
            MovingAverageCost = 15m
        };

        var container1 = new InventoryUnit
        {
            Id = Guid.NewGuid(),
            ProductId = productId,
            InventoryLotId = lot.Id,
            TrackingCode = "BUNDLE-01",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 150m
        };
        _doubles.Inventory.Units.Add(container1);
        _doubles.Inventory.MovementUnits.Add(new InventoryMovementUnit
        {
            MovementId = recvMovement.Id,
            InventoryUnitId = container1.Id
        });

        var issueHandler = CreateIssueThakaMaterialHandler();
        var issueCommand = new IssueThakaMaterialCommand(
            Guid.NewGuid(),
            project.Id,
            Guid.NewGuid(),
            "Issue 1 bundle",
            [
                new IssueThakaMaterialLineInput(productId, unit.Id, 1m, 200m, [container1.Id])
            ]);

        var issueResult = await issueHandler.HandleAsync(issueCommand, CancellationToken.None);
        Assert.True(issueResult.IsSuccess, issueResult.Error?.Message);
        Assert.Equal(InventoryUnitStatus.IssuedThaka, container1.Status);
        Assert.Equal(0m, _doubles.Inventory.Balances[productId].SellableQty);
        Assert.Equal(0m, _doubles.Inventory.CostStates[productId].TotalInventoryCost);

        var actorId = Guid.NewGuid();
        var reversalHandler = CreateReverseThakaMaterialHandler();
        var reversalCommand = new ReverseThakaMaterialCommand(
            Guid.NewGuid(),
            project.Id,
            issueResult.Value!.MaterialIssueId,
            "Return unused bundle",
            actorId);

        var reversalResult = await reversalHandler.HandleAsync(reversalCommand, CancellationToken.None);
        Assert.True(reversalResult.IsSuccess, reversalResult.Error?.Message);

        Assert.Equal(InventoryUnitStatus.InStock, container1.Status);
        Assert.Equal(10m, _doubles.Inventory.Balances[productId].SellableQty);
        Assert.Equal(150m, _doubles.Inventory.CostStates[productId].TotalInventoryCost);
    }
}
