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

public sealed class StockAdjustmentHandlerBehavioralTests
{
    private readonly FakeCatalogRepository _catalog = new();
    private readonly FakeInventoryRepository _inventory = new();
    private readonly FakeInventoryCostAllocator _costAllocator;
    private readonly FakePartyRepository _parties = new();
    private readonly FakeTraceabilityRepository _traceability = new();
    private readonly FakeResourceLock _resourceLock = new();
    private readonly FakeBusinessAuditWriter _audit = new();
    private readonly FakeClock _clock = new(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeTransactionRunner _transactions = new();
    private readonly FakePermissionAuthorizer _authorization = new();
    private readonly FakeUnitOfWork _unitOfWork = new();

    public StockAdjustmentHandlerBehavioralTests()
    {
        _costAllocator = new FakeInventoryCostAllocator(_inventory);
    }

    private CreateStockAdjustmentHandler CreateHandler() =>
        new(
            _catalog,
            _inventory,
            _costAllocator,
            _parties,
            _traceability,
            _resourceLock,
            _audit,
            _clock,
            _transactions,
            _authorization,
            _unitOfWork);

    private static string MakeValidImei(string prefix14)
    {
        var p = prefix14.PadRight(14, '0')[..14];
        for (int d = 0; d <= 9; d++)
        {
            var candidate = p + d;
            if (IdentityNormalizationRules.ValidateLuhn(candidate))
            {
                return candidate;
            }
        }
        return p + "0";
    }

    [Fact]
    public async Task Positive_Serialized_Adjustment_Creates_Lots_Balances_And_Units_Correctly()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();

        var product = new Product
        {
            Id = productId,
            Name = "iPhone 15 Pro",
            Sku = "IP15P-128",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
            ReferencePurchaseCost = 250000m,
            IsActive = true
        };
        _catalog.Products[productId] = product;

        var supplier = new Supplier
        {
            Id = supplierId,
            Name = "Apple Direct",
            DealerCode = "AD1",
            IsActive = true
        };
        _parties.Suppliers[supplierId] = supplier;

        var imeiA = MakeValidImei("86012345678901");
        var imeiB = MakeValidImei("86012345678902");

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    2m,
                    245000m,
                    supplierId,
                    [
                        new SerializedAdjustmentUnitCommand("SN-AD-001", imeiA),
                        new SerializedAdjustmentUnitCommand("SN-AD-002", imeiB)
                    ],
                    ReasonDetails: "Discovered in back store")
            ],
            actorId,
            correlationId,
            Note: "Audit finding correction");

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotEqual(Guid.Empty, result.Value);

        // 1. Stock Adjustment entity created and posted
        Assert.Single(_inventory.StockAdjustments);
        var adj = _inventory.StockAdjustments[0];
        Assert.Equal(result.Value, adj.Id);
        Assert.Equal(StockAdjustmentStatus.Posted, adj.Status);
        Assert.Equal(StockAdjustmentMode.Delta, adj.Mode);
        Assert.Equal(StockAdjustmentReason.Other, adj.Reason);
        Assert.Equal(actorId, adj.ActorId);
        Assert.Equal(correlationId, adj.CorrelationId);

        // 2. Stock Adjustment Item created
        Assert.Single(_inventory.StockAdjustmentItems);
        var item = _inventory.StockAdjustmentItems[0];
        Assert.Equal(adj.Id, item.StockAdjustmentId);
        Assert.Equal(productId, item.ProductId);
        Assert.Equal(StockAdjustmentDirection.Increase, item.Direction);
        Assert.Equal(InventoryBucket.Sellable, item.TargetBucket);
        Assert.Equal(2m, item.BaseQuantity);
        Assert.Equal(245000m, item.UnitCostSnapshot);
        Assert.Equal(490000m, item.TotalCostSnapshot);
        Assert.NotNull(item.SupplierProductId);

        // 3. Movement created with stock adjustment reference
        Assert.Single(_inventory.Movements);
        var mov = _inventory.Movements[0];
        Assert.Equal(InventoryMovementType.StockAdjustment, mov.MovementType);
        Assert.Equal("STOCK_ADJUSTMENT", mov.ReferenceType);
        Assert.Equal(item.Id, mov.ReferenceId);
        Assert.Equal(245000m, mov.UnitCostSnapshot);

        // 4. Lot created & cost allocator updated
        Assert.Single(_inventory.Lots);
        var lot = _inventory.Lots[0];
        Assert.Equal(productId, lot.ProductId);
        Assert.Equal(2m, lot.ReceivedQuantity);
        Assert.Equal(245000m, lot.OriginalUnitCost);

        Assert.Single(_inventory.LotBucketBalances);
        var lotBalance = _inventory.LotBucketBalances[0];
        Assert.Equal(lot.Id, lotBalance.LotId);
        Assert.Equal(InventoryBucket.Sellable, lotBalance.StockBucket);
        Assert.Equal(2m, lotBalance.Quantity);

        var costState = _inventory.CostStates[productId];
        Assert.Equal(2m, costState.CostedQty);
        Assert.Equal(490000m, costState.TotalInventoryCost);
        Assert.Equal(245000m, costState.MovingAverageCost);

        // 5. StockBalance updated
        var balance = _inventory.Balances[productId];
        Assert.Equal(2m, balance.SellableQty);

        // 6. Serialized Units created with strict traceability invariants
        Assert.Equal(2, _inventory.Units.Count);
        var unit1 = _inventory.Units[0];
        var unit2 = _inventory.Units[1];

        Assert.Equal(productId, unit1.ProductId);
        Assert.Equal(InventoryUnitOriginType.StockAdjustment, unit1.OriginType);
        Assert.Equal(item.Id, unit1.SourceStockAdjustmentItemId);
        Assert.Null(unit1.SourcePurchaseItemId);
        Assert.Equal(lot.Id, unit1.InventoryLotId);
        Assert.Equal(1, unit1.ItemSequence);
        Assert.Equal("AD1-IP15P-128-000001", unit1.TrackingCode);
        Assert.Equal("SN-AD-001", unit1.SerialNumber);
        Assert.Equal(imeiA, unit1.Imei1);
        Assert.Equal(InventoryUnitStatus.InStock, unit1.Status);
        Assert.Equal(245000m, unit1.AcquisitionCost);

        Assert.Equal(productId, unit2.ProductId);
        Assert.Equal(InventoryUnitOriginType.StockAdjustment, unit2.OriginType);
        Assert.Equal(item.Id, unit2.SourceStockAdjustmentItemId);
        Assert.Null(unit2.SourcePurchaseItemId);
        Assert.Equal(lot.Id, unit2.InventoryLotId);
        Assert.Equal(2, unit2.ItemSequence);
        Assert.Equal("AD1-IP15P-128-000002", unit2.TrackingCode);
        Assert.Equal("SN-AD-002", unit2.SerialNumber);
        Assert.Equal(imeiB, unit2.Imei1);
        Assert.Equal(InventoryUnitStatus.InStock, unit2.Status);

        // 7. SupplierProduct sequence advanced
        var sp = _traceability.SupplierProducts.Values.Single();
        Assert.Equal(3, sp.NextItemSequence);

        // 8. Audit and UoW committed
        Assert.Single(_audit.Records);
        Assert.Equal("STOCK_ADJUSTMENT_POSTED", _audit.Records[0].Action);
        Assert.Equal(1, _unitOfWork.SavedCount);
    }

    [Fact]
    public async Task Opening_Stock_Adjustment_Sets_OpeningStock_Movement_Type()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Non-Serialized Cable",
            Sku = "CBL-001",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.OpeningStock,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    100m,
                    150m)
            ],
            actorId,
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(_inventory.Movements);
        var mov = _inventory.Movements[0];
        Assert.Equal(InventoryMovementType.OpeningStock, mov.MovementType);
        Assert.Equal("STOCK_ADJUSTMENT", mov.ReferenceType);

        var balance = _inventory.Balances[productId];
        Assert.Equal(100m, balance.SellableQty);

        var costState = _inventory.CostStates[productId];
        Assert.Equal(100m, costState.CostedQty);
        Assert.Equal(15000m, costState.TotalInventoryCost);
        Assert.Equal(150m, costState.MovingAverageCost);
    }

    [Fact]
    public async Task Negative_Serialized_Adjustment_Reduces_Authoritative_Bucket_And_Lot_And_Transitions_To_Scrapped()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var lotId = Guid.NewGuid();
        var unitId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Laptop X",
            Sku = "LAP-X",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };

        var balance = new StockBalance { ProductId = productId, SellableQty = 1m, Version = 1 };
        _inventory.Balances[productId] = balance;

        var costState = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 1m,
            TotalInventoryCost = 120000m,
            MovingAverageCost = 120000m,
            Version = 1
        };
        _inventory.CostStates[productId] = costState;

        var lot = new InventoryLot
        {
            Id = lotId,
            ProductId = productId,
            ReceivedQuantity = 1m,
            OriginalUnitCost = 120000m,
            EffectiveUnitCost = 120000m,
            CreatedAt = _clock.UtcNow
        };
        _inventory.Lots.Add(lot);

        var lotBucket = new InventoryLotBucketBalance
        {
            LotId = lotId,
            StockBucket = InventoryBucket.Sellable,
            Quantity = 1m
        };
        _inventory.LotBucketBalances.Add(lotBucket);

        var unit = new InventoryUnit
        {
            Id = unitId,
            ProductId = productId,
            InventoryLotId = lotId,
            ItemSequence = 1,
            TrackingCode = "DL1-LAPX-000001",
            SerialNumber = "SN-LAP-001",
            Status = InventoryUnitStatus.InStock,
            AcquisitionCost = 120000m,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = Guid.NewGuid(),
            CreatedAt = _clock.UtcNow,
            Version = 1
        };
        _inventory.Units.Add(unit);

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Damaged,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    1m,
                    null,
                    InventoryUnitIds: [unitId],
                    ReasonDetails: "Water damage during transit")
            ],
            actorId,
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Error?.Message);

        // StockBalance reduced
        Assert.Equal(0m, balance.SellableQty);

        // Unit transitioned to Scrapped
        Assert.Equal(InventoryUnitStatus.Scrapped, unit.Status);

        // Lot bucket reduced to 0
        Assert.Equal(0m, lotBucket.Quantity);

        // Lot consumption recorded
        Assert.Single(_inventory.LotConsumptions);
        var consumption = _inventory.LotConsumptions[0];
        Assert.Equal(lotId, consumption.LotId);
        Assert.Equal(1m, consumption.Quantity);
        Assert.Equal(120000m, consumption.TotalCostSnapshot);

        // Cost state reduced
        Assert.Equal(0m, costState.CostedQty);
        Assert.Equal(0m, costState.TotalInventoryCost);
    }

    [Fact]
    public async Task Negative_Serialized_Adjustment_Rejects_Bucket_Mismatch()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Monitor 4K",
            Sku = "MON-4K",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };

        _inventory.Balances[productId] = new StockBalance { ProductId = productId, DamagedQty = 1m, SellableQty = 0m };
        _inventory.CostStates[productId] = new ProductCostState { ProductId = productId, CostedQty = 1m, TotalInventoryCost = 50000m };

        // Unit is in Damaged status (AuthoritativeBucket == Damaged)
        _inventory.Units.Add(new InventoryUnit
        {
            Id = unitId,
            ProductId = productId,
            Status = InventoryUnitStatus.Damaged,
            TrackingCode = "DL1-MON4K-000001",
            SerialNumber = "SN-MON-001",
            AcquisitionCost = 50000m,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = Guid.NewGuid()
        });

        // Command incorrectly specifies Sellable as source bucket
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Damaged,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable, // Mismatch! Unit is in Damaged bucket
                    1m,
                    null,
                    InventoryUnitIds: [unitId])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.insufficient_stock", result.Error?.Code);
    }

    [Fact]
    public async Task Negative_Serialized_Adjustment_Rejects_Unit_Bucket_Mismatch_When_Stock_Present()
    {
        // Arrange: Sellable has 1 item, but the unit provided is in Damaged status
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Monitor 4K",
            Sku = "MON-4K",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };

        _inventory.Balances[productId] = new StockBalance { ProductId = productId, DamagedQty = 1m, SellableQty = 1m };
        _inventory.CostStates[productId] = new ProductCostState { ProductId = productId, CostedQty = 2m, TotalInventoryCost = 100000m };

        // Unit is in Damaged status
        _inventory.Units.Add(new InventoryUnit
        {
            Id = unitId,
            ProductId = productId,
            Status = InventoryUnitStatus.Damaged,
            TrackingCode = "DL1-MON4K-000001",
            SerialNumber = "SN-MON-001",
            AcquisitionCost = 50000m,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = Guid.NewGuid()
        });

        // Command specifies Sellable bucket
        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Damaged,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    1m,
                    null,
                    InventoryUnitIds: [unitId])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.unit_bucket_mismatch", result.Error?.Code);
    }

    [Fact]
    public async Task Negative_Serialized_Adjustment_Rejects_Unit_Not_In_Stock()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var unitId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Monitor 4K",
            Sku = "MON-4K",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };

        _inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 1m };
        _inventory.CostStates[productId] = new ProductCostState { ProductId = productId, CostedQty = 1m, TotalInventoryCost = 50000m };

        // Unit is already Sold (does not contribute to stock balance)
        _inventory.Units.Add(new InventoryUnit
        {
            Id = unitId,
            ProductId = productId,
            Status = InventoryUnitStatus.Sold,
            TrackingCode = "DL1-MON4K-000001",
            SerialNumber = "SN-MON-001",
            AcquisitionCost = 50000m,
            OriginType = InventoryUnitOriginType.StockAdjustment,
            SourceStockAdjustmentItemId = Guid.NewGuid()
        });

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Damaged,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    1m,
                    null,
                    InventoryUnitIds: [unitId])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.unit_not_in_stock", result.Error?.Code);
    }

    [Fact]
    public async Task Positive_Serialized_Adjustment_Requires_Product_Sku()
    {
        // Arrange
        var productId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "No SKU Phone",
            Sku = "", // Invalid SKU for serialized product
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    50000m,
                    supplierId,
                    [new SerializedAdjustmentUnitCommand("SN-001")])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("catalog.sku_required", result.Error?.Code);
    }

    [Fact]
    public async Task Positive_Serialized_Adjustment_Requires_Supplier_Provenance()
    {
        // Arrange
        var productId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Phone With SKU",
            Sku = "SKU-PH-1",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    50000m,
                    SupplierId: null, // Missing Supplier
                    SerializedUnits: [new SerializedAdjustmentUnitCommand("SN-001")])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.serialized_supplier_provenance_required", result.Error?.Code);
    }

    [Fact]
    public async Task Positive_Serialized_Adjustment_Enforces_Serial_And_Imei_Tracking_Rules()
    {
        var productId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();

        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Dual Tracked Phone",
            Sku = "DT-001",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            ImeiTrackingEnabled = true,
            IsActive = true
        };
        _parties.Suppliers[supplierId] = new Supplier { Id = supplierId, Name = "Sup1", DealerCode = "SP1" };

        var handler = CreateHandler();

        // 1. Missing serial when serial tracking enabled
        var imei1 = MakeValidImei("86012345678910");
        var cmdMissingSerial = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    1000m,
                    supplierId,
                    [new SerializedAdjustmentUnitCommand("", imei1)])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var resMissingSerial = await handler.HandleAsync(cmdMissingSerial, CancellationToken.None);
        Assert.False(resMissingSerial.IsSuccess);
        Assert.Equal("identity.serial_required", resMissingSerial.Error?.Code);

        // 2. Missing IMEI when IMEI tracking enabled
        var cmdMissingImei = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    1000m,
                    supplierId,
                    [new SerializedAdjustmentUnitCommand("SN-123", "")])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var resMissingImei = await handler.HandleAsync(cmdMissingImei, CancellationToken.None);
        Assert.False(resMissingImei.IsSuccess);
        Assert.Equal("identity.imei_required", resMissingImei.Error?.Code);

        // 3. Duplicate Serial within command
        var cmdDupSerial = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    2m,
                    1000m,
                    supplierId,
                    [
                        new SerializedAdjustmentUnitCommand("SN-DUP", MakeValidImei("86012345678911")),
                        new SerializedAdjustmentUnitCommand("SN-DUP", MakeValidImei("86012345678912"))
                    ])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var resDupSerial = await handler.HandleAsync(cmdDupSerial, CancellationToken.None);
        Assert.False(resDupSerial.IsSuccess);
        Assert.Equal("identity.duplicate_serial_in_command", resDupSerial.Error?.Code);

        // 4. Duplicate IMEI within command
        var cmdDupImei = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    2m,
                    1000m,
                    supplierId,
                    [
                        new SerializedAdjustmentUnitCommand("SN-001", imei1),
                        new SerializedAdjustmentUnitCommand("SN-002", imei1)
                    ])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var resDupImei = await handler.HandleAsync(cmdDupImei, CancellationToken.None);
        Assert.False(resDupImei.IsSuccess);
        Assert.Equal("identity.duplicate_imei_in_command", resDupImei.Error?.Code);

        // 5. Existing serial in DB history
        _inventory.ExistingSerials.Add("SN-EXISTING");
        var cmdExistingSerial = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    1000m,
                    supplierId,
                    [new SerializedAdjustmentUnitCommand("SN-EXISTING", MakeValidImei("86012345678913"))])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var resExistingSerial = await handler.HandleAsync(cmdExistingSerial, CancellationToken.None);
        Assert.False(resExistingSerial.IsSuccess);
        Assert.Equal("identity.already_exists", resExistingSerial.Error?.Code);
    }

    [Fact]
    public async Task Section185_Resource_Lock_Order_Is_Deterministically_Preserved()
    {
        // Arrange
        var prodA = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var prodB = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var supA = Guid.Parse("00000000-0000-0000-0000-000000000004");
        var supB = Guid.Parse("00000000-0000-0000-0000-000000000003");

        _catalog.Products[prodA] = new Product
        {
            Id = prodA,
            Name = "Prod A",
            Sku = "SKU-A",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _catalog.Products[prodB] = new Product
        {
            Id = prodB,
            Name = "Prod B",
            Sku = "SKU-B",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };

        _parties.Suppliers[supA] = new Supplier { Id = supA, Name = "Sup A", DealerCode = "SA1" };
        _parties.Suppliers[supB] = new Supplier { Id = supB, Name = "Sup B", DealerCode = "SB1" };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                // Pass items in unsorted order
                new StockAdjustmentItemCommand(
                    prodA,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    100m,
                    supA,
                    [new SerializedAdjustmentUnitCommand("SN-Z")]),
                new StockAdjustmentItemCommand(
                    prodB,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    200m,
                    supB,
                    [new SerializedAdjustmentUnitCommand("SN-A")])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Error?.Message);

        var locks = _resourceLock.AcquiredLocks;

        // 1. All "product" locks come first, in ascending Guid order
        var productLocks = locks.Where(l => l.ResourceType == "product").ToList();
        Assert.Equal(2, productLocks.Count);
        Assert.Equal(prodB.ToString("D"), productLocks[0].Key);
        Assert.Equal(prodA.ToString("D"), productLocks[1].Key);

        // 2. All "supplier-product" locks come next, in ordinal sorted order
        var spLocks = locks.Where(l => l.ResourceType == "supplier-product").ToList();
        Assert.Equal(2, spLocks.Count);
        Assert.Equal($"{supB:D}:{prodB:D}", spLocks[0].Key);
        Assert.Equal($"{supA:D}:{prodA:D}", spLocks[1].Key);

        // 3. All "inventory-identity" locks come last, in ordinal sorted order
        var idLocks = locks.Where(l => l.ResourceType == "inventory-identity").ToList();
        Assert.Equal(2, idLocks.Count);
        Assert.Equal("SERIAL:SN-A", idLocks[0].Key);
        Assert.Equal("SERIAL:SN-Z", idLocks[1].Key);

        // Overall lock list order check
        Assert.Equal(6, locks.Count);
        Assert.Equal("product", locks[0].ResourceType);
        Assert.Equal("product", locks[1].ResourceType);
        Assert.Equal("supplier-product", locks[2].ResourceType);
        Assert.Equal("supplier-product", locks[3].ResourceType);
        Assert.Equal("inventory-identity", locks[4].ResourceType);
        Assert.Equal("inventory-identity", locks[5].ResourceType);
    }

    [Fact]
    public async Task Negative_NonSerialized_Adjustment_Consumes_MovingAverageCost_And_Updates_Balance()
    {
        // Arrange
        var productId = Guid.NewGuid();
        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Bulk Sugar",
            Sku = "SUG-50KG",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        };

        _inventory.Balances[productId] = new StockBalance { ProductId = productId, SellableQty = 10m };
        _inventory.CostStates[productId] = new ProductCostState
        {
            ProductId = productId,
            CostedQty = 10m,
            TotalInventoryCost = 5000m,
            MovingAverageCost = 500m
        };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    3m,
                    null,
                    ReasonDetails: "Bag leakage in warehouse")
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();

        // Act
        var result = await handler.HandleAsync(command, CancellationToken.None);

        // Assert
        Assert.True(result.IsSuccess, result.Error?.Message);

        var balance = _inventory.Balances[productId];
        Assert.Equal(7m, balance.SellableQty);

        var costState = _inventory.CostStates[productId];
        Assert.Equal(7m, costState.CostedQty);
        Assert.Equal(3500m, costState.TotalInventoryCost);
        Assert.Equal(500m, costState.MovingAverageCost);
    }

    [Fact]
    public async Task Adjustment_Rejects_When_Product_Locked_By_Counting_Stocktake()
    {
        var productId = Guid.NewGuid();
        _catalog.Products[productId] = new Product { Id = productId, Name = "Locked Product", Sku = "LCK-01" };
        _inventory.BlockedStocktakeProducts.Add(productId);

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    100m)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.stocktake_in_progress", result.Error?.Code);
    }

    [Fact]
    public async Task Adjustment_Rejects_Unauthorized_Actor()
    {
        _authorization.IsAuthorized = false;

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    Guid.NewGuid(),
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    100m)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("auth.forbidden", result.Error?.Code);
    }

    [Fact]
    public async Task Adjustment_Rejects_Zero_Or_Negative_Quantity()
    {
        var productId = Guid.NewGuid();
        _catalog.Products[productId] = new Product { Id = productId, Name = "Product", Sku = "PR-1" };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    0m, // Invalid quantity
                    100m)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.quantity_positive", result.Error?.Code);
    }

    [Fact]
    public async Task Adjustment_Rejects_Negative_Cost()
    {
        var productId = Guid.NewGuid();
        _catalog.Products[productId] = new Product { Id = productId, Name = "Product", Sku = "PR-1" };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    -50m) // Negative cost
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("inventory.cost_negative", result.Error?.Code);
    }

    [Fact]
    public async Task Positive_Length_Adjustment_Creates_Lot_And_Balance()
    {
        var productId = Guid.NewGuid();
        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Copper Wire Roll",
            Sku = "WIRE-CU-100M",
            TrackingMode = TrackingMode.Length,
            IsActive = true
        };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    150.5m,
                    12.5m)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(150.5m, _inventory.Balances[productId].SellableQty);
        Assert.Equal(150.5m, _inventory.CostStates[productId].CostedQty);
        Assert.Equal(12.5m, _inventory.CostStates[productId].MovingAverageCost);
    }

    [Fact]
    public async Task Adjustment_Does_Not_Create_Purchase_Or_Payable_Entries()
    {
        var productId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Tracked Phone",
            Sku = "TP-1",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _parties.Suppliers[supplierId] = new Supplier { Id = supplierId, Name = "Supplier 1", DealerCode = "S1" };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    1m,
                    1000m,
                    supplierId,
                    [new SerializedAdjustmentUnitCommand("SN-TP-1")])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();
        var result = await handler.HandleAsync(command, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        // Verify that units created have 0 purchase provenance
        var unit = _inventory.Units.Single();
        Assert.Null(unit.SourcePurchaseItemId);
        Assert.Equal(InventoryUnitOriginType.StockAdjustment, unit.OriginType);
        Assert.NotNull(unit.SourceStockAdjustmentItemId);

        // Verify lot created has 0 purchase item id
        var lot = _inventory.Lots.Single();
        Assert.Null(lot.PurchaseItemId);
    }

    [Fact]
    public async Task Adjustment_Rolls_Back_On_Mid_Command_Failure()
    {
        _transactions.ShouldFail = true;

        var productId = Guid.NewGuid();
        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Product",
            Sku = "PR-1",
            TrackingMode = TrackingMode.Quantity,
            IsActive = true
        };

        var command = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    5m,
                    100m)
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var handler = CreateHandler();
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(command, CancellationToken.None));
    }

    [Fact]
    public async Task Full_Reconciliation_Invariant_Verification()
    {
        var productId = Guid.NewGuid();
        var supplierId = Guid.NewGuid();
        _catalog.Products[productId] = new Product
        {
            Id = productId,
            Name = "Tracked Tablet",
            Sku = "TAB-1",
            TrackingMode = TrackingMode.Serialized,
            SerialTrackingEnabled = true,
            IsActive = true
        };
        _parties.Suppliers[supplierId] = new Supplier { Id = supplierId, Name = "Supplier 1", DealerCode = "S1" };

        var handler = CreateHandler();

        // 1. Intake 3 units
        var intakeCommand = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Other,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Increase,
                    InventoryBucket.Sellable,
                    3m,
                    30000m,
                    supplierId,
                    [
                        new SerializedAdjustmentUnitCommand("SN-TAB-1"),
                        new SerializedAdjustmentUnitCommand("SN-TAB-2"),
                        new SerializedAdjustmentUnitCommand("SN-TAB-3")
                    ])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var intakeResult = await handler.HandleAsync(intakeCommand, CancellationToken.None);
        Assert.True(intakeResult.IsSuccess, intakeResult.Error?.Message);

        // 2. Outtake 1 unit
        var unitToScrap = _inventory.Units.First(u => u.SerialNumber == "SN-TAB-2");
        var outtakeCommand = new CreateStockAdjustmentCommand(
            StockAdjustmentMode.Delta,
            StockAdjustmentReason.Damaged,
            [
                new StockAdjustmentItemCommand(
                    productId,
                    null,
                    StockAdjustmentDirection.Decrease,
                    InventoryBucket.Sellable,
                    1m,
                    null,
                    InventoryUnitIds: [unitToScrap.Id])
            ],
            Guid.NewGuid(),
            Guid.NewGuid());

        var outtakeResult = await handler.HandleAsync(outtakeCommand, CancellationToken.None);
        Assert.True(outtakeResult.IsSuccess, outtakeResult.Error?.Message);

        // Reconcile:
        // StockBalance Sellable == Active InStock shop-owned units
        var activeUnits = _inventory.Units.Where(u => u.ProductId == productId && u.Status == InventoryUnitStatus.InStock).ToList();
        var balance = _inventory.Balances[productId];
        var costState = _inventory.CostStates[productId];
        var activeLotBalance = _inventory.LotBucketBalances.Where(lb => lb.StockBucket == InventoryBucket.Sellable).Sum(lb => lb.Quantity);

        Assert.Equal(2m, balance.SellableQty);
        Assert.Equal(2, activeUnits.Count);
        Assert.Equal(2m, activeLotBalance);
        Assert.Equal(2m, costState.CostedQty);
        Assert.Equal(60000m, costState.TotalInventoryCost);
        Assert.Equal(30000m, costState.MovingAverageCost);
    }
}

#region Test Fakes and Test Doubles

internal sealed class FakeCatalogRepository : ICatalogRepository
{
    public Dictionary<Guid, Product> Products { get; } = new();
    public Dictionary<Guid, ProductUnit> ProductUnits { get; } = new();

    public Task<Product?> GetProductAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(Products.TryGetValue(productId, out var p) ? p : null);

    public Task<Product?> GetProductForUpdateAsync(Guid productId, CancellationToken cancellationToken) =>
        GetProductAsync(productId, cancellationToken);

    public Task<Product?> GetProductBySkuAsync(string normalizedSku, CancellationToken cancellationToken) =>
        Task.FromResult(Products.Values.FirstOrDefault(p =>
            string.Equals(p.Sku, normalizedSku, StringComparison.Ordinal)));

    public Task<IReadOnlyList<Product>> GetProductsAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Product>>(Products.Values
            .Where(p => includeInactive || p.IsActive)
            .ToList());

    public Task<Category?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult<Category?>(new Category { Id = categoryId, Name = "Test Category", IsActive = true });

    public Task<Category?> GetCategoryForUpdateAsync(Guid categoryId, CancellationToken cancellationToken) =>
        GetCategoryAsync(categoryId, cancellationToken);

    public Task<IReadOnlyList<Category>> GetCategoriesAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Category>>([]);

    public Task<bool> IsCategoryInUseByActiveProductAsync(Guid categoryId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<Unit?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken) =>
        Task.FromResult<Unit?>(new Unit { Id = unitId, Name = "Piece", Symbol = "pc", IsActive = true });

    public Task<Unit?> GetUnitForUpdateAsync(Guid unitId, CancellationToken cancellationToken) =>
        GetUnitAsync(unitId, cancellationToken);

    public Task<IReadOnlyList<Unit>> GetUnitsAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Unit>>([]);

    public Task<bool> IsUnitInUseByActiveCatalogAsync(Guid unitId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<ProductUnit?> GetProductUnitAsync(Guid productUnitId, CancellationToken cancellationToken) =>
        Task.FromResult(ProductUnits.TryGetValue(productUnitId, out var pu) ? pu : null);

    public Task<ProductUnit?> GetProductUnitAsync(Guid productId, Guid unitId, CancellationToken cancellationToken) =>
        Task.FromResult(ProductUnits.Values.FirstOrDefault(pu => pu.ProductId == productId && pu.UnitId == unitId));

    public Task<IReadOnlyList<ProductUnit>> GetProductUnitsAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ProductUnit>>(ProductUnits.Values.Where(pu => pu.ProductId == productId).ToList());

    public Task<ProductUnitBarcode?> GetBarcodeAsync(string barcode, CancellationToken cancellationToken) =>
        Task.FromResult<ProductUnitBarcode?>(null);

    public Task<IReadOnlyList<Product>> GetActiveProductsAsync(StocktakeScope scope, Guid? categoryId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Product>>(Products.Values.ToList());

    public void AddCategory(Category category) { }
    public void AddUnit(Unit unit) { }
    public void AddProduct(Product product) => Products[product.Id] = product;
    public void AddProductUnit(ProductUnit productUnit) => ProductUnits[productUnit.Id] = productUnit;
    public void AddBarcode(ProductUnitBarcode barcode) { }
}

internal sealed class FakeInventoryRepository : IInventoryRepository
{
    public Dictionary<Guid, StockBalance> Balances { get; } = new();
    public Dictionary<Guid, ProductCostState> CostStates { get; } = new();
    public List<InventoryUnit> Units { get; } = new();
    public List<InventoryMovement> Movements { get; } = new();
    public List<InventoryMovementEffect> MovementEffects { get; } = new();
    public List<InventoryMovementUnit> MovementUnits { get; } = new();
    public List<InventoryLot> Lots { get; } = new();
    public List<InventoryLotBucketBalance> LotBucketBalances { get; } = new();
    public List<InventoryLotConsumption> LotConsumptions { get; } = new();
    public List<StockAdjustment> StockAdjustments { get; } = new();
    public List<StockAdjustmentItem> StockAdjustmentItems { get; } = new();
    public HashSet<string> ExistingSerials { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ExistingImeis { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<Guid> BlockedStocktakeProducts { get; } = new();

    public Task<StockBalance?> GetStockBalanceForUpdateAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(Balances.TryGetValue(productId, out var b) ? b : null);

    public Task<ProductCostState?> GetCostStateForUpdateAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(CostStates.TryGetValue(productId, out var c) ? c : null);

    public Task<IReadOnlyList<LotBucketPosition>> GetLotBucketPositionsForUpdateAsync(Guid productId, InventoryBucket bucket, CancellationToken cancellationToken)
    {
        var positions = (from b in LotBucketBalances
                         join l in Lots on b.LotId equals l.Id
                         where l.ProductId == productId && b.StockBucket == bucket && b.Quantity > 0
                         orderby l.CreatedAt, l.Id
                         select new LotBucketPosition(l, b)).ToList();
        return Task.FromResult<IReadOnlyList<LotBucketPosition>>(positions);
    }

    public Task<IReadOnlyList<LotBucketPosition>> GetPurchaseItemLotPositionsForUpdateAsync(Guid purchaseItemId, InventoryBucket bucket, CancellationToken cancellationToken)
    {
        var positions = (from b in LotBucketBalances
                         join l in Lots on b.LotId equals l.Id
                         where l.PurchaseItemId == purchaseItemId && b.StockBucket == bucket && b.Quantity > 0
                         orderby l.CreatedAt, l.Id
                         select new LotBucketPosition(l, b)).ToList();
        return Task.FromResult<IReadOnlyList<LotBucketPosition>>(positions);
    }

    public Task<InventoryLotBucketBalance?> GetLotBucketBalanceForUpdateAsync(Guid lotId, InventoryBucket bucket, CancellationToken cancellationToken) =>
        Task.FromResult(LotBucketBalances.FirstOrDefault(l => l.LotId == lotId && l.StockBucket == bucket));

    public Task<InventoryLot?> GetInventoryLotForUpdateAsync(Guid lotId, CancellationToken cancellationToken) =>
        Task.FromResult(Lots.FirstOrDefault(l => l.Id == lotId));

    public Task<bool> HasPurchaseItemConsumptionAsync(Guid purchaseItemId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<InventoryLotConsumption>> GetMovementLotConsumptionsAsync(Guid movementId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InventoryLotConsumption>>(LotConsumptions.Where(c => c.MovementId == movementId).ToList());

    public Task<IReadOnlyList<InventoryUnit>> GetInventoryUnitsForUpdateAsync(Guid productId, IReadOnlyCollection<Guid> inventoryUnitIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InventoryUnit>>(Units.Where(u => u.ProductId == productId && inventoryUnitIds.Contains(u.Id)).ToList());

    public Task<IReadOnlyList<InventoryUnit>> GetSellableInventoryUnitsAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InventoryUnit>>(Units.Where(u => u.ProductId == productId && u.Status == InventoryUnitStatus.InStock).ToList());

    public Task<bool> InventoryIdentityExistsAsync(string? serialNumber, string? imei1, string? imei2, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(serialNumber) && ExistingSerials.Contains(serialNumber))
        {
            return Task.FromResult(true);
        }

        if (!string.IsNullOrWhiteSpace(imei1) && ExistingImeis.Contains(imei1))
        {
            return Task.FromResult(true);
        }

        if (!string.IsNullOrWhiteSpace(imei2) && ExistingImeis.Contains(imei2))
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task<bool> IsProductBlockedByCountingStocktakeAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(BlockedStocktakeProducts.Contains(productId));

    public void AddStockBalance(StockBalance balance) => Balances[balance.ProductId] = balance;
    public void AddCostState(ProductCostState costState) => CostStates[costState.ProductId] = costState;
    public void AddInventoryUnit(InventoryUnit unit) => Units.Add(unit);
    public void AddMovement(InventoryMovement movement) => Movements.Add(movement);
    public void AddMovementEffect(InventoryMovementEffect effect) => MovementEffects.Add(effect);
    public void AddMovementUnit(InventoryMovementUnit movementUnit) => MovementUnits.Add(movementUnit);
    public void AddLot(InventoryLot lot) => Lots.Add(lot);
    public void AddLotBucketBalance(InventoryLotBucketBalance balance) => LotBucketBalances.Add(balance);
    public void AddLotConsumption(InventoryLotConsumption consumption) => LotConsumptions.Add(consumption);

    public List<Stocktake> Stocktakes { get; } = [];
    public List<StocktakeItem> StocktakeItems { get; } = [];
    public List<StocktakeUnitCheck> StocktakeUnitChecks { get; } = [];

    public Task<Stocktake?> GetOpenStocktakeForUpdateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Stocktakes.FirstOrDefault(s => s.Status == StocktakeStatus.Counting || s.Status == StocktakeStatus.Review));
    public Task<Stocktake?> GetStocktakeForUpdateAsync(Guid stocktakeId, CancellationToken cancellationToken) =>
        Task.FromResult(Stocktakes.FirstOrDefault(s => s.Id == stocktakeId));
    public Task<IReadOnlyList<StocktakeItem>> GetStocktakeItemsAsync(Guid stocktakeId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StocktakeItem>>(StocktakeItems.Where(i => i.StocktakeId == stocktakeId).ToList());
    public Task<StocktakeItem?> GetStocktakeItemForUpdateAsync(Guid stocktakeId, Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(StocktakeItems.FirstOrDefault(i => i.StocktakeId == stocktakeId && i.ProductId == productId));
    public Task<IReadOnlyList<StocktakeUnitCheck>> GetStocktakeUnitChecksAsync(Guid stocktakeItemId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StocktakeUnitCheck>>(StocktakeUnitChecks.Where(u => u.StocktakeItemId == stocktakeItemId).ToList());
    public void AddStocktake(Stocktake stocktake) => Stocktakes.Add(stocktake);
    public void AddStocktakeItem(StocktakeItem item) => StocktakeItems.Add(item);
    public void AddStocktakeUnitCheck(StocktakeUnitCheck unitCheck) => StocktakeUnitChecks.Add(unitCheck);
    public void RemoveStocktakeUnitCheck(StocktakeUnitCheck unitCheck) => StocktakeUnitChecks.Remove(unitCheck);

    public Task<StockAdjustment?> GetStockAdjustmentAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(StockAdjustments.FirstOrDefault(a => a.Id == id));

    public Task<StockAdjustmentItem?> GetStockAdjustmentItemAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(StockAdjustmentItems.FirstOrDefault(i => i.Id == id));

    public void AddStockAdjustment(StockAdjustment adjustment) => StockAdjustments.Add(adjustment);
    public void AddStockAdjustmentItem(StockAdjustmentItem item) => StockAdjustmentItems.Add(item);
}

internal sealed class FakeInventoryCostAllocator : IInventoryCostAllocator
{
    private readonly FakeInventoryRepository _inventory;

    public FakeInventoryCostAllocator(FakeInventoryRepository inventory)
    {
        _inventory = inventory;
    }

    public Task TransferBucketAsync(Guid productId, InventoryBucket from, InventoryBucket to, decimal baseQuantity, CancellationToken cancellationToken)
    {
        var lots = _inventory.Lots
            .Where(l => l.ProductId == productId)
            .OrderBy(l => l.CreatedAt)
            .ToList();

        var remaining = baseQuantity;
        foreach (var lot in lots)
        {
            if (remaining <= 0)
            {
                break;
            }

            var fromBalance = _inventory.LotBucketBalances.FirstOrDefault(b => b.LotId == lot.Id && b.StockBucket == from);
            if (fromBalance is null || fromBalance.Quantity <= 0)
            {
                continue;
            }

            var take = Math.Min(fromBalance.Quantity, remaining);
            fromBalance.Quantity -= take;
            remaining -= take;

            var toBalance = _inventory.LotBucketBalances.FirstOrDefault(b => b.LotId == lot.Id && b.StockBucket == to);
            if (toBalance is null)
            {
                toBalance = new InventoryLotBucketBalance
                {
                    LotId = lot.Id,
                    StockBucket = to,
                    Quantity = 0m
                };
                _inventory.AddLotBucketBalance(toBalance);
            }
            toBalance.Quantity += take;
        }

        return Task.CompletedTask;
    }

    public Task<decimal> RemoveCarryingValueAsync(Guid productId, decimal baseQuantity, decimal? exactCost, CancellationToken cancellationToken)
    {
        if (_inventory.CostStates.TryGetValue(productId, out var state))
        {
            var costToRemove = exactCost.HasValue
                ? exactCost.Value * baseQuantity
                : state.MovingAverageCost * baseQuantity;

            state.CostedQty = Math.Max(0m, state.CostedQty - baseQuantity);
            state.TotalInventoryCost = Math.Max(0m, state.TotalInventoryCost - costToRemove);
            if (state.CostedQty == 0m)
            {
                state.TotalInventoryCost = 0m;
            }
            else
            {
                state.MovingAverageCost = decimal.Round(state.TotalInventoryCost / state.CostedQty, 6, MidpointRounding.AwayFromZero);
            }
            return Task.FromResult(costToRemove);
        }
        return Task.FromResult(0m);
    }

    public Task AddCarryingValueAndLotAsync(Guid productId, decimal baseQuantity, decimal unitCost, Guid sourceMovementId, CancellationToken cancellationToken) =>
        AddCarryingValueAndLotWithIdAsync(productId, baseQuantity, unitCost, sourceMovementId, null, InventoryBucket.Sellable, cancellationToken);

    public Task AddCarryingValueAndLotAsync(Guid productId, decimal baseQuantity, decimal unitCost, Guid sourceMovementId, Guid? sourcePurchaseItemId, CancellationToken cancellationToken) =>
        AddCarryingValueAndLotWithIdAsync(productId, baseQuantity, unitCost, sourceMovementId, sourcePurchaseItemId, InventoryBucket.Sellable, cancellationToken);

    public Task<Guid> AddCarryingValueAndLotWithIdAsync(Guid productId, decimal baseQuantity, decimal unitCost, Guid sourceMovementId, Guid? sourcePurchaseItemId, CancellationToken cancellationToken) =>
        AddCarryingValueAndLotWithIdAsync(productId, baseQuantity, unitCost, sourceMovementId, sourcePurchaseItemId, InventoryBucket.Sellable, cancellationToken);

    public Task<Guid> AddCarryingValueAndLotWithIdAsync(Guid productId, decimal baseQuantity, decimal unitCost, Guid sourceMovementId, Guid? sourcePurchaseItemId, InventoryBucket initialBucket, CancellationToken cancellationToken)
    {
        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = productId,
            PurchaseItemId = sourcePurchaseItemId,
            SourceMovementId = sourceMovementId,
            ReceivedQuantity = baseQuantity,
            OriginalUnitCost = unitCost,
            EffectiveUnitCost = unitCost,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _inventory.AddLot(lot);

        _inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = initialBucket,
            Quantity = baseQuantity
        });

        if (!_inventory.CostStates.TryGetValue(productId, out var state))
        {
            state = new ProductCostState { ProductId = productId, Version = 1 };
            _inventory.AddCostState(state);
        }

        var newTotalQty = state.CostedQty + baseQuantity;
        var newTotalCost = state.TotalInventoryCost + (baseQuantity * unitCost);
        state.CostedQty = newTotalQty;
        state.TotalInventoryCost = newTotalCost;
        state.MovingAverageCost = newTotalQty > 0
            ? decimal.Round(newTotalCost / newTotalQty, 6, MidpointRounding.AwayFromZero)
            : 0m;

        return Task.FromResult(lot.Id);
    }

    public Task<Guid> AddZeroCarryingLotAsync(Guid productId, decimal baseQuantity, decimal originalUnitCost, Guid sourceMovementId, Guid? sourcePurchaseItemId, InventoryBucket initialBucket, CancellationToken cancellationToken)
    {
        var lot = new InventoryLot
        {
            Id = Guid.CreateVersion7(),
            ProductId = productId,
            PurchaseItemId = sourcePurchaseItemId,
            SourceMovementId = sourceMovementId,
            ReceivedQuantity = baseQuantity,
            OriginalUnitCost = originalUnitCost,
            EffectiveUnitCost = 0m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _inventory.AddLot(lot);

        _inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = initialBucket,
            Quantity = baseQuantity
        });

        return Task.FromResult(lot.Id);
    }

    public Task<decimal?> GetCurrentUnitCostAsync(Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult<decimal?>(_inventory.CostStates.TryGetValue(productId, out var state) ? state.MovingAverageCost : null);

    public Task ConsumeBucketAsync(Guid productId, InventoryBucket bucket, decimal baseQuantity, Guid movementId, decimal unitCostSnapshot, CancellationToken cancellationToken)
    {
        var lots = _inventory.Lots
            .Where(l => l.ProductId == productId)
            .OrderBy(l => l.CreatedAt)
            .ToList();

        var remaining = baseQuantity;
        foreach (var lot in lots)
        {
            if (remaining <= 0)
            {
                break;
            }

            var balance = _inventory.LotBucketBalances.FirstOrDefault(b => b.LotId == lot.Id && b.StockBucket == bucket);
            if (balance is null || balance.Quantity <= 0)
            {
                continue;
            }

            var take = Math.Min(balance.Quantity, remaining);
            balance.Quantity -= take;
            remaining -= take;

            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                Id = Guid.CreateVersion7(),
                LotId = lot.Id,
                MovementId = movementId,
                Quantity = take,
                UnitCostSnapshot = unitCostSnapshot,
                TotalCostSnapshot = decimal.Round(take * unitCostSnapshot, 2, MidpointRounding.AwayFromZero),
                OccurredAt = DateTimeOffset.UtcNow
            });
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakePartyRepository : IPartyRepository
{
    public Dictionary<Guid, Supplier> Suppliers { get; } = new();
    public Dictionary<Guid, Customer> Customers { get; } = new();

    public Task<Customer?> GetCustomerAsync(Guid customerId, CancellationToken cancellationToken) =>
        Task.FromResult(Customers.TryGetValue(customerId, out var c) ? c : null);

    public Task<Customer?> GetCustomerForUpdateAsync(Guid customerId, CancellationToken cancellationToken) =>
        GetCustomerAsync(customerId, cancellationToken);

    public Task<Supplier?> GetSupplierAsync(Guid supplierId, CancellationToken cancellationToken) =>
        Task.FromResult(Suppliers.TryGetValue(supplierId, out var s) ? s : null);

    public Task<Supplier?> GetSupplierForUpdateAsync(Guid supplierId, CancellationToken cancellationToken) =>
        GetSupplierAsync(supplierId, cancellationToken);

    public Task<IReadOnlyList<Customer>> GetCustomersAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Customer>>(Customers.Values.ToList());

    public Task<IReadOnlyList<Supplier>> GetSuppliersAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Supplier>>(Suppliers.Values
            .Where(s => includeInactive || s.IsActive)
            .ToList());

    public void AddCustomer(Customer customer) => Customers[customer.Id] = customer;
    public void AddSupplier(Supplier supplier) => Suppliers[supplier.Id] = supplier;
}

internal sealed class FakeTraceabilityRepository : ITraceabilityRepository
{
    public Dictionary<(Guid, Guid), SupplierProduct> SupplierProducts { get; } = new();
    public Dictionary<string, SupplierCodeSequence> Sequences { get; } = new();

    public Task<SupplierProduct?> GetSupplierProductAsync(Guid supplierId, Guid productId, CancellationToken cancellationToken) =>
        Task.FromResult(SupplierProducts.TryGetValue((supplierId, productId), out var sp) ? sp : null);

    public Task<SupplierProduct?> GetSupplierProductForUpdateAsync(Guid supplierId, Guid productId, CancellationToken cancellationToken) =>
        GetSupplierProductAsync(supplierId, productId, cancellationToken);

    public Task<SupplierProduct?> GetSupplierProductByIdForUpdateAsync(Guid supplierProductId, CancellationToken cancellationToken) =>
        Task.FromResult(SupplierProducts.Values.FirstOrDefault(sp => sp.Id == supplierProductId));

    public Task<SupplierCodeSequence?> GetSupplierCodeSequenceForUpdateAsync(string prefix, CancellationToken cancellationToken) =>
        Task.FromResult(Sequences.TryGetValue(prefix, out var s) ? s : null);

    public void AddSupplierProduct(SupplierProduct supplierProduct) =>
        SupplierProducts[(supplierProduct.SupplierId, supplierProduct.ProductId)] = supplierProduct;

    public void AddSupplierCodeSequence(SupplierCodeSequence sequence) =>
        Sequences[sequence.Prefix] = sequence;
}

internal sealed class FakeResourceLock : IResourceLock
{
    public List<(string ResourceType, string Key)> AcquiredLocks { get; } = new();

    public Task AcquireAsync(string resourceType, string key, CancellationToken cancellationToken)
    {
        AcquiredLocks.Add((resourceType, key));
        return Task.CompletedTask;
    }

    public Task AcquireAsync(string resourceType, Guid id, CancellationToken cancellationToken) =>
        AcquireAsync(resourceType, id.ToString("D"), cancellationToken);
}

internal sealed class FakeBusinessAuditWriter : IBusinessAuditWriter
{
    public List<(string Action, string EntityType, Guid? EntityId, Guid ActorId, Guid CorrelationId, string? Summary)> Records { get; } = new();

    public void Record(string action, string entityType, Guid? entityId, Guid actorId, Guid correlationId, string? summary = null) =>
        Records.Add((action, entityType, entityId, actorId, correlationId, summary));
}

internal sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }
    public DateOnly ShopDate => DateOnly.FromDateTime(UtcNow.Date);

    public FakeClock(DateTimeOffset now)
    {
        UtcNow = now;
    }
}

internal sealed class FakeTransactionRunner : ITransactionRunner
{
    public bool ShouldFail { get; set; }

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        if (ShouldFail)
        {
            throw new InvalidOperationException("Simulated transaction execution failure.");
        }
        return await operation(cancellationToken);
    }
}

internal sealed class FakePermissionAuthorizer : IApplicationPermissionAuthorizer
{
    public bool IsAuthorized { get; set; } = true;

    public Task<Result> AuthorizeAsync(Guid actorId, string permissionKey, CancellationToken cancellationToken) =>
        Task.FromResult(IsAuthorized
            ? Result.Success()
            : Result.Failure("auth.forbidden", "Permission denied."));
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int SavedCount { get; private set; }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken)
    {
        SavedCount++;
        return Task.FromResult(1);
    }
}

#endregion
