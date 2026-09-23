using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Parties;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record SerializedAdjustmentUnitCommand(
    string? SerialNumber,
    string? Imei1 = null,
    string? Imei2 = null);

public sealed record StockAdjustmentItemCommand(
    Guid ProductId,
    Guid? ProductUnitId,
    StockAdjustmentDirection Direction,
    InventoryBucket TargetBucket,
    decimal BaseQuantity,
    decimal? UnitCostSnapshot,
    Guid? SupplierId = null,
    IReadOnlyList<SerializedAdjustmentUnitCommand>? SerializedUnits = null,
    IReadOnlyList<Guid>? InventoryUnitIds = null,
    string? ReasonDetails = null);

public sealed record CreateStockAdjustmentCommand(
    StockAdjustmentMode Mode,
    StockAdjustmentReason Reason,
    IReadOnlyList<StockAdjustmentItemCommand> Items,
    Guid ActorId,
    Guid CorrelationId,
    string? Note = null);

public sealed class CreateStockAdjustmentHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costAllocator;
    private readonly IPartyRepository _parties;
    private readonly ITraceabilityRepository _traceability;
    private readonly IResourceLock _resourceLock;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public CreateStockAdjustmentHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costAllocator,
        IPartyRepository parties,
        ITraceabilityRepository traceability,
        IResourceLock resourceLock,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _catalog = catalog;
        _inventory = inventory;
        _costAllocator = costAllocator;
        _parties = parties;
        _traceability = traceability;
        _resourceLock = resourceLock;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public async Task<Result<Guid>> HandleAsync(
        CreateStockAdjustmentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.Items is null || command.Items.Count == 0)
        {
            return Result<Guid>.Failure(
                "inventory.adjustment_items_required",
                "At least one adjustment item is required.");
        }

        var auth = await _authorization.AuthorizeAsync(
            command.ActorId,
            PermissionKeys.InventoryManage,
            cancellationToken);
        if (!auth.IsSuccess)
        {
            return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
        }

        // 1. Initial validation and product retrieval
        var productIds = command.Items.Select(x => x.ProductId).Distinct().OrderBy(x => x).ToArray();
        var products = new Dictionary<Guid, Product>();
        foreach (var pid in productIds)
        {
            var p = await _catalog.GetProductAsync(pid, cancellationToken);
            if (p is null)
            {
                return Result<Guid>.Failure(
                    "catalog.product_not_found",
                    $"Product {pid} was not found.");
            }

            products[pid] = p;
        }

        // Validate items and tracking rules
        var seenSerials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenImeis = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in command.Items)
        {
            if (item.BaseQuantity <= 0)
            {
                return Result<Guid>.Failure(
                    "inventory.quantity_positive",
                    "Adjustment quantity must be greater than zero.");
            }

            if (item.UnitCostSnapshot.HasValue && item.UnitCostSnapshot.Value < 0)
            {
                return Result<Guid>.Failure(
                    "inventory.cost_negative",
                    "Unit cost cannot be negative.");
            }

            var product = products[item.ProductId];
            if (product.TrackingMode == TrackingMode.Serialized)
            {
                if (!QuantityMath.IsWhole(item.BaseQuantity))
                {
                    return Result<Guid>.Failure(
                        "catalog.serialized_whole_quantity",
                        "Serialized adjustment quantity must be a whole number.");
                }

                var count = (int)item.BaseQuantity;

                if (item.Direction == StockAdjustmentDirection.Increase)
                {
                    if (string.IsNullOrWhiteSpace(product.Sku))
                    {
                        return Result<Guid>.Failure(
                            "catalog.sku_required",
                            $"Serialized product '{product.Name}' requires a valid SKU.");
                    }

                    if (item.SupplierId is null || item.SupplierId == Guid.Empty)
                    {
                        return Result<Guid>.Failure(
                            "inventory.serialized_supplier_provenance_required",
                            "Positive serialized adjustment requires explicit Supplier provenance.");
                    }

                    if (item.SerializedUnits is null || item.SerializedUnits.Count != count)
                    {
                        return Result<Guid>.Failure(
                            "inventory.serialized_units_count_mismatch",
                            $"Expected {count} serialized unit definitions but received {item.SerializedUnits?.Count ?? 0}.");
                    }

                    foreach (var u in item.SerializedUnits)
                    {
                        if (product.SerialTrackingEnabled)
                        {
                            if (string.IsNullOrWhiteSpace(u.SerialNumber))
                            {
                                return Result<Guid>.Failure(
                                    "identity.serial_required",
                                    $"Serial number is required for product '{product.Name}'.");
                            }

                            var normSerial = IdentityNormalizationRules.NormalizeSerialNumber(u.SerialNumber);
                            if (!seenSerials.Add(normSerial))
                            {
                                return Result<Guid>.Failure(
                                    "identity.duplicate_serial_in_command",
                                    $"Duplicate serial number '{normSerial}' in adjustment items.");
                            }
                        }

                        if (product.ImeiTrackingEnabled)
                        {
                            if (string.IsNullOrWhiteSpace(u.Imei1))
                            {
                                return Result<Guid>.Failure(
                                    "identity.imei_required",
                                    $"IMEI1 is required for product '{product.Name}'.");
                            }

                            var normImei1 = IdentityNormalizationRules.NormalizeImei(u.Imei1);
                            if (!seenImeis.Add(normImei1))
                            {
                                return Result<Guid>.Failure(
                                    "identity.duplicate_imei_in_command",
                                    $"Duplicate IMEI1 '{normImei1}' in adjustment items.");
                            }

                            if (!string.IsNullOrWhiteSpace(u.Imei2))
                            {
                                var normImei2 = IdentityNormalizationRules.NormalizeImei(u.Imei2);
                                if (!seenImeis.Add(normImei2))
                                {
                                    return Result<Guid>.Failure(
                                        "identity.duplicate_imei_in_command",
                                        $"Duplicate IMEI2 '{normImei2}' in adjustment items.");
                                }
                            }
                        }
                    }
                }
                else
                {
                    // Negative serialized
                    if (item.InventoryUnitIds is null || item.InventoryUnitIds.Count != count)
                    {
                        return Result<Guid>.Failure(
                            "inventory.serialized_exact_units_required",
                            $"Negative serialized adjustment of {count} units requires exactly {count} selected InventoryUnit IDs.");
                    }

                    if (item.InventoryUnitIds.Distinct().Count() != count)
                    {
                        return Result<Guid>.Failure(
                            "inventory.duplicate_unit_in_command",
                            "Duplicate InventoryUnit ID found in adjustment items.");
                    }
                }
            }
        }

        // 2. Execute Transaction and Canonical Resource Locking (Section 185)
        return await _transactions.ExecuteAsync(async ct =>
        {
            // Sort Product IDs
            foreach (var pid in productIds)
            {
                await _resourceLock.AcquireAsync("product", pid, ct);
            }

            // Sort SupplierProduct keys for positive serialized items
            var supplierProductKeys = command.Items
                .Where(x => x.Direction == StockAdjustmentDirection.Increase && x.SupplierId.HasValue && products[x.ProductId].TrackingMode == TrackingMode.Serialized)
                .Select(x => $"{x.SupplierId!.Value:D}:{x.ProductId:D}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            foreach (var spKey in supplierProductKeys)
            {
                await _resourceLock.AcquireAsync("supplier-product", spKey, ct);
            }

            // Sort Identity lock keys
            var identityKeys = seenSerials.Select(s => $"SERIAL:{s}")
                .Concat(seenImeis.Select(i => $"IMEI:{i}"))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray();

            foreach (var idKey in identityKeys)
            {
                await _resourceLock.AcquireAsync("inventory-identity", idKey, ct);
            }

            var now = _clock.UtcNow;

            // Check if identity already exists in database history
            foreach (var item in command.Items.Where(x => x.Direction == StockAdjustmentDirection.Increase && products[x.ProductId].TrackingMode == TrackingMode.Serialized))
            {
                var p = products[item.ProductId];
                foreach (var u in item.SerializedUnits!)
                {
                    var normSerial = p.SerialTrackingEnabled && !string.IsNullOrWhiteSpace(u.SerialNumber)
                        ? IdentityNormalizationRules.NormalizeSerialNumber(u.SerialNumber)
                        : null;
                    var normImei1 = p.ImeiTrackingEnabled && !string.IsNullOrWhiteSpace(u.Imei1)
                        ? IdentityNormalizationRules.NormalizeImei(u.Imei1)
                        : null;
                    var normImei2 = p.ImeiTrackingEnabled && !string.IsNullOrWhiteSpace(u.Imei2)
                        ? IdentityNormalizationRules.NormalizeImei(u.Imei2)
                        : null;

                    if (await _inventory.InventoryIdentityExistsAsync(normSerial, normImei1, normImei2, ct))
                    {
                        return Result<Guid>.Failure(
                            "identity.already_exists",
                            "A specified Serial or IMEI already exists in inventory history.");
                    }
                }
            }

            var adjustmentId = Guid.CreateVersion7();
            var adjustmentNumber = $"ADJ-{now:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..12].ToUpperInvariant()}";

            var adjustment = new StockAdjustment
            {
                Id = adjustmentId,
                AdjustmentNumber = adjustmentNumber,
                Mode = command.Mode,
                Reason = command.Reason,
                Status = StockAdjustmentStatus.Posted,
                ActorId = command.ActorId,
                OccurredAt = now,
                CorrelationId = command.CorrelationId,
                Note = command.Note,
                CreatedAt = now,
                Version = 1
            };
            _inventory.AddStockAdjustment(adjustment);

            // Pre-load StockBalance and ProductCostState for all products in sorted order
            var balances = new Dictionary<Guid, StockBalance>();
            var costStates = new Dictionary<Guid, ProductCostState>();

            foreach (var pid in productIds)
            {
                if (await _inventory.IsProductBlockedByCountingStocktakeAsync(pid, ct))
                {
                    return Result<Guid>.Failure(
                        "inventory.stocktake_in_progress",
                        $"Product '{products[pid].Name}' is locked by an active stocktake.");
                }

                var bal = await _inventory.GetStockBalanceForUpdateAsync(pid, ct);
                if (bal is null)
                {
                    bal = new StockBalance { ProductId = pid, Version = 1 };
                    _inventory.AddStockBalance(bal);
                }
                balances[pid] = bal;

                var cs = await _inventory.GetCostStateForUpdateAsync(pid, ct);
                if (cs is null)
                {
                    cs = new ProductCostState { ProductId = pid, Version = 1 };
                    _inventory.AddCostState(cs);
                }
                costStates[pid] = cs;
            }

            // Process items
            foreach (var itemCommand in command.Items)
            {
                var product = products[itemCommand.ProductId];
                var balance = balances[itemCommand.ProductId];
                var costState = costStates[itemCommand.ProductId];

                // Determine unit cost
                decimal unitCost;
                if (itemCommand.UnitCostSnapshot.HasValue)
                {
                    unitCost = itemCommand.UnitCostSnapshot.Value;
                }
                else if (itemCommand.Direction == StockAdjustmentDirection.Increase && product.ReferencePurchaseCost.HasValue)
                {
                    unitCost = product.ReferencePurchaseCost.Value;
                }
                else
                {
                    unitCost = itemCommand.Direction == StockAdjustmentDirection.Decrease
                        ? costState.MovingAverageCost
                        : 0m;
                }

                var adjustmentItemId = Guid.CreateVersion7();
                var adjustmentItem = new StockAdjustmentItem
                {
                    Id = adjustmentItemId,
                    StockAdjustmentId = adjustment.Id,
                    ProductId = itemCommand.ProductId,
                    ProductUnitId = itemCommand.ProductUnitId,
                    Direction = itemCommand.Direction,
                    TargetBucket = itemCommand.TargetBucket,
                    BaseQuantity = itemCommand.BaseQuantity,
                    UnitCostSnapshot = unitCost,
                    TotalCostSnapshot = decimal.Round(unitCost * itemCommand.BaseQuantity, 6, MidpointRounding.AwayFromZero),
                    SupplierId = itemCommand.SupplierId,
                    ReasonDetails = itemCommand.ReasonDetails
                };
                _inventory.AddStockAdjustmentItem(adjustmentItem);

                var movement = new InventoryMovement
                {
                    ProductId = itemCommand.ProductId,
                    MovementType = command.Reason == StockAdjustmentReason.OpeningStock
                        ? InventoryMovementType.OpeningStock
                        : InventoryMovementType.StockAdjustment,
                    ReferenceType = "STOCK_ADJUSTMENT",
                    ReferenceId = adjustmentItem.Id,
                    UnitCostSnapshot = unitCost,
                    ActorId = command.ActorId,
                    OccurredAt = now,
                    CorrelationId = command.CorrelationId,
                    Reason = command.Reason.ToString(),
                    Note = itemCommand.ReasonDetails
                };
                _inventory.AddMovement(movement);

                if (itemCommand.Direction == StockAdjustmentDirection.Increase)
                {
                    // Positive adjustment
                    Guid lotId;
                    if (itemCommand.TargetBucket == InventoryBucket.Scrap && unitCost == 0m)
                    {
                        lotId = await _costAllocator.AddZeroCarryingLotAsync(
                            product.Id,
                            itemCommand.BaseQuantity,
                            unitCost,
                            movement.Id,
                            null,
                            itemCommand.TargetBucket,
                            ct);
                    }
                    else
                    {
                        lotId = await _costAllocator.AddCarryingValueAndLotWithIdAsync(
                            product.Id,
                            itemCommand.BaseQuantity,
                            unitCost,
                            movement.Id,
                            null,
                            itemCommand.TargetBucket,
                            ct);
                    }

                    if (product.TrackingMode == TrackingMode.Serialized)
                    {
                        var count = (int)itemCommand.BaseQuantity;
                        var supplier = await _parties.GetSupplierAsync(itemCommand.SupplierId!.Value, ct);
                        if (supplier is null)
                        {
                            return Result<Guid>.Failure("parties.supplier_not_found", "Supplier was not found.");
                        }

                        if (string.IsNullOrWhiteSpace(supplier.DealerCode))
                        {
                            return Result<Guid>.Failure(
                                "parties.dealer_code_missing",
                                $"Supplier '{supplier.Name}' does not have an allocated DealerCode.");
                        }

                        var supplierProduct = await _traceability.GetSupplierProductForUpdateAsync(
                            itemCommand.SupplierId.Value,
                            itemCommand.ProductId,
                            ct);

                        if (supplierProduct is null)
                        {
                            supplierProduct = new SupplierProduct
                            {
                                SupplierId = itemCommand.SupplierId.Value,
                                ProductId = itemCommand.ProductId,
                                NextItemSequence = 1,
                                IsActive = true,
                                CreatedAt = now,
                                UpdatedAt = now,
                                Version = 1
                            };
                            _traceability.AddSupplierProduct(supplierProduct);
                        }

                        adjustmentItem.SupplierProductId = supplierProduct.Id;

                        var startSequence = supplierProduct.NextItemSequence;
                        supplierProduct.NextItemSequence = checked(startSequence + count);
                        supplierProduct.UpdatedAt = now;
                        supplierProduct.Version++;

                        var targetStatus = itemCommand.TargetBucket switch
                        {
                            InventoryBucket.Sellable => InventoryUnitStatus.InStock,
                            InventoryBucket.Damaged => InventoryUnitStatus.Damaged,
                            InventoryBucket.Defective => InventoryUnitStatus.Defective,
                            InventoryBucket.WithSupplier => InventoryUnitStatus.WithSupplier,
                            _ => InventoryUnitStatus.Scrapped
                        };

                        for (int i = 0; i < count; i++)
                        {
                            var currentSeq = startSequence + i;
                            var trackingCode = TraceabilityCodeRules.BuildTrackingCode(
                                supplier.DealerCode!,
                                product.Sku!,
                                currentSeq);

                            var unitCmd = itemCommand.SerializedUnits![i];

                            var unit = new InventoryUnit
                            {
                                ProductId = product.Id,
                                SupplierProductId = supplierProduct.Id,
                                OriginType = InventoryUnitOriginType.StockAdjustment,
                                SourceStockAdjustmentItemId = adjustmentItem.Id,
                                InventoryLotId = lotId,
                                ItemSequence = currentSeq,
                                TrackingCode = trackingCode,
                                SupplierCodeSnapshot = supplier.DealerCode,
                                ProductSkuSnapshot = product.Sku,
                                SerialNumber = product.SerialTrackingEnabled && !string.IsNullOrWhiteSpace(unitCmd.SerialNumber)
                                    ? IdentityNormalizationRules.NormalizeSerialNumber(unitCmd.SerialNumber)
                                    : null,
                                Imei1 = product.ImeiTrackingEnabled && !string.IsNullOrWhiteSpace(unitCmd.Imei1)
                                    ? IdentityNormalizationRules.NormalizeImei(unitCmd.Imei1)
                                    : null,
                                Imei2 = product.ImeiTrackingEnabled && !string.IsNullOrWhiteSpace(unitCmd.Imei2)
                                    ? IdentityNormalizationRules.NormalizeImei(unitCmd.Imei2)
                                    : null,
                                AcquisitionCost = unitCost,
                                Status = targetStatus,
                                CreatedAt = now,
                                Version = 1
                            };

                            unit.ValidateOriginInvariants();
                            _inventory.AddInventoryUnit(unit);

                            _inventory.AddMovementUnit(new InventoryMovementUnit
                            {
                                MovementId = movement.Id,
                                InventoryUnitId = unit.Id,
                                FromStatus = null,
                                ToStatus = targetStatus
                            });
                        }
                    }

                    var beforeQty = balance.Get(itemCommand.TargetBucket);
                    balance.ApplyDelta(itemCommand.TargetBucket, itemCommand.BaseQuantity);
                    var afterQty = balance.Get(itemCommand.TargetBucket);

                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = itemCommand.TargetBucket,
                        QuantityDelta = itemCommand.BaseQuantity,
                        QuantityBefore = beforeQty,
                        QuantityAfter = afterQty
                    });
                }
                else
                {
                    // Negative adjustment (Decrease)
                    var available = balance.Get(itemCommand.TargetBucket);
                    if (available < itemCommand.BaseQuantity)
                    {
                        return Result<Guid>.Failure(
                            "inventory.insufficient_stock",
                            $"Insufficient stock in bucket '{itemCommand.TargetBucket}'. Available: {available}, Requested: {itemCommand.BaseQuantity}.");
                    }

                    if (product.TrackingMode == TrackingMode.Serialized)
                    {
                        var count = (int)itemCommand.BaseQuantity;
                        var units = await _inventory.GetInventoryUnitsForUpdateAsync(
                            product.Id,
                            itemCommand.InventoryUnitIds!,
                            ct);

                        if (units.Count != count)
                        {
                            return Result<Guid>.Failure(
                                "inventory.serialized_units_not_found",
                                "One or more selected serialized inventory units were not found.");
                        }

                        foreach (var u in units)
                        {
                            var accountingRule = InventoryUnitAccountingPolicy.GetRule(u.Status);
                            if (!accountingRule.ContributesToStockBalance || accountingRule.AuthoritativeBucket is null)
                            {
                                return Result<Guid>.Failure(
                                    "inventory.unit_not_in_stock",
                                    $"Unit '{u.TrackingCode}' is not in an active stock state (Status: {u.Status}).");
                            }

                            if (accountingRule.AuthoritativeBucket.Value != itemCommand.TargetBucket)
                            {
                                return Result<Guid>.Failure(
                                    "inventory.unit_bucket_mismatch",
                                    $"Unit '{u.TrackingCode}' is in '{accountingRule.AuthoritativeBucket.Value}' bucket, but adjustment specified source bucket '{itemCommand.TargetBucket}'.");
                            }

                            // Consume unit's lot bucket balance
                            if (u.InventoryLotId.HasValue)
                            {
                                var lotBucket = await _inventory.GetLotBucketBalanceForUpdateAsync(
                                    u.InventoryLotId.Value,
                                    itemCommand.TargetBucket,
                                    ct);

                                if (lotBucket is not null && lotBucket.Quantity >= 1m)
                                {
                                    lotBucket.Quantity = QuantityMath.RoundQuantity(lotBucket.Quantity - 1m);
                                }

                                _inventory.AddLotConsumption(new InventoryLotConsumption
                                {
                                    LotId = u.InventoryLotId.Value,
                                    MovementId = movement.Id,
                                    Quantity = 1m,
                                    UnitCostSnapshot = u.AcquisitionCost,
                                    TotalCostSnapshot = u.AcquisitionCost,
                                    OccurredAt = now
                                });
                            }

                            // Remove carrying cost for this exact unit
                            await _costAllocator.RemoveCarryingValueAsync(
                                product.Id,
                                1m,
                                u.AcquisitionCost > 0 ? u.AcquisitionCost : null,
                                ct);

                            var fromStatus = u.Status;
                            u.Status = InventoryUnitStatus.Scrapped;
                            u.Version++;

                            _inventory.AddMovementUnit(new InventoryMovementUnit
                            {
                                MovementId = movement.Id,
                                InventoryUnitId = u.Id,
                                FromStatus = fromStatus,
                                ToStatus = InventoryUnitStatus.Scrapped
                            });
                        }
                    }
                    else
                    {
                        // Non-serialized negative adjustment
                        await _costAllocator.ConsumeBucketAsync(
                            product.Id,
                            itemCommand.TargetBucket,
                            itemCommand.BaseQuantity,
                            movement.Id,
                            costState.MovingAverageCost,
                            ct);

                        await _costAllocator.RemoveCarryingValueAsync(
                            product.Id,
                            itemCommand.BaseQuantity,
                            null,
                            ct);
                    }

                    var beforeQty = balance.Get(itemCommand.TargetBucket);
                    balance.ApplyDelta(itemCommand.TargetBucket, -itemCommand.BaseQuantity);
                    var afterQty = balance.Get(itemCommand.TargetBucket);

                    _inventory.AddMovementEffect(new InventoryMovementEffect
                    {
                        MovementId = movement.Id,
                        StockBucket = itemCommand.TargetBucket,
                        QuantityDelta = -itemCommand.BaseQuantity,
                        QuantityBefore = beforeQty,
                        QuantityAfter = afterQty
                    });
                }
            }

            _audit.Record(
                "STOCK_ADJUSTMENT_POSTED",
                "STOCK_ADJUSTMENT",
                adjustment.Id,
                command.ActorId,
                command.CorrelationId,
                adjustment.AdjustmentNumber);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(adjustment.Id);
        }, cancellationToken);
    }
}
