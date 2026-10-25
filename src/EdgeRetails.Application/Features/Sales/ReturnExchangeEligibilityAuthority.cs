using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Inventory;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Application.Features.Sales;

public enum ReturnLineKind
{
    Standard = 1,
    Serialized = 2,
    ContainerFullPack = 3,
    ContainerLoose = 4
}

public sealed record PreparedReturnLine(
    SaleItem Item,
    SaleReturnLineInput Input,
    Product Product,
    StockBalance Stock,
    decimal BaseQuantity,
    decimal RefundAmount,
    decimal OriginalCostAmount,
    ReturnLineKind Kind,
    IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)> SerializedUnits,
    IReadOnlyList<OriginalLotReturnAllocation> OriginAllocations,
    InventoryUnit? FullPackUnit = null);

public interface IReturnExchangeEligibilityAuthority
{
    Task<PreparedReturnLine> PrepareReturnLineAsync(
        SaleReturnLineInput input,
        Guid saleId,
        CancellationToken ct);

    Task RestoreReturnLineAsync(
        PreparedReturnLine prepared,
        Guid returnItemId,
        InventoryMovement movement,
        Guid actorId,
        CancellationToken ct);
}

public sealed class ReturnExchangeEligibilityAuthority : IReturnExchangeEligibilityAuthority
{
    private readonly ISalesRepository _sales;
    private readonly IInventoryRepository _inventory;
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryCostAllocator _costs;
    private readonly IClock _clock;
    private readonly IWarrantyRepository? _warranty;
    private readonly IPhysicalUnitCreationAuthority? _physicalUnits;
    private readonly IPackSubLedgerAllocationAuthority _packAllocations;
    private readonly ITraceabilityRepository? _traceability;
    private readonly IPartyRepository? _parties;

    public ReturnExchangeEligibilityAuthority(
        ISalesRepository sales,
        IInventoryRepository inventory,
        ICatalogRepository catalog,
        IInventoryCostAllocator costs,
        IClock clock,
        IWarrantyRepository? warranty = null,
        IPhysicalUnitCreationAuthority? physicalUnits = null,
        IPackSubLedgerAllocationAuthority? packAllocations = null,
        ITraceabilityRepository? traceability = null,
        IPartyRepository? parties = null)
    {
        _sales = sales;
        _inventory = inventory;
        _catalog = catalog;
        _costs = costs;
        _clock = clock;
        _warranty = warranty;
        _physicalUnits = physicalUnits;
        _packAllocations = packAllocations ?? new PackSubLedgerAllocationAuthority();
        _traceability = traceability;
        _parties = parties;
    }

    public async Task<PreparedReturnLine> PrepareReturnLineAsync(
        SaleReturnLineInput input,
        Guid saleId,
        CancellationToken ct)
    {
        var item = await _sales.GetSaleItemForUpdateAsync(input.SaleItemId, ct);
        if (item is null || item.SaleId != saleId)
        {
            throw new BusinessRuleException(
                "sales.return_item_invalid",
                "Sale return item does not belong to the sale.");
        }

        var baseQuantity = QuantityMath.RoundQuantity(input.BaseQuantity);
        if (baseQuantity <= 0)
        {
            throw new BusinessRuleException(
                "sales.return_quantity_positive",
                "Return base quantity must be greater than zero.");
        }

        var priorQty = await _sales.GetReturnedBaseQuantityAsync(item.Id, ct);
        var cumulativeQty = QuantityMath.RoundQuantity(priorQty + baseQuantity);
        if (cumulativeQty > item.BaseQuantity)
        {
            throw new BusinessRuleException(
                "sales.return_exceeds_original",
                "Return quantity exceeds the remaining sold quantity.");
        }

        if (_warranty is not null)
        {
            var activeWarrantyQty = await _warranty.GetActiveClaimedQuantityAsync(item.Id, ct);
            var terminallyRemovedQty = await _warranty.GetTerminallyRemovedQuantityAsync(item.Id, ct);
            var warrantyBlockedQty = QuantityMath.RoundQuantity(activeWarrantyQty + terminallyRemovedQty);
            if (cumulativeQty > QuantityMath.RoundQuantity(item.BaseQuantity - warrantyBlockedQty))
            {
                throw new BusinessRuleException(
                    "sales.return_unit_active_warranty",
                    "Return quantity exceeds remaining quantity available outside warranty claims.");
            }
        }

        if (await _inventory.IsProductBlockedByCountingStocktakeAsync(item.ProductId, ct))
        {
            throw new BusinessRuleException(
                "inventory.stocktake_blocks_product",
                "Sale return is blocked by an active stocktake.");
        }

        var stock = await _inventory.GetStockBalanceForUpdateAsync(item.ProductId, ct);
        if (stock is null)
        {
            throw new BusinessRuleException(
                "sales.stock_missing",
                "Stock balance was not found for the returned product.");
        }

        var product = await _catalog.GetProductForUpdateAsync(item.ProductId, ct)
            ?? throw new BusinessRuleException("catalog.product_not_found", "Product was not found.");

        var priorRefund = await _sales.GetRefundedAmountAsync(item.Id, ct);
        var refundAmount = CalculateResidualAmount(
            item.NetLineTotal,
            item.BaseQuantity,
            priorQty,
            baseQuantity,
            priorRefund,
            2);

        var priorCost = await _sales.GetReturnedOriginalCostAmountAsync(item.Id, ct);
        var originalCostAmount = CalculateResidualAmount(
            item.TotalCostSnapshot,
            item.BaseQuantity,
            priorQty,
            baseQuantity,
            priorCost,
            6);

        var saleItemUnits = await _sales.GetSaleItemUnitsAsync(item.Id, ct);

        // Path A: Serialized / IndividualPiece tracking
        if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece)
        {
            var serializedUnits = await PrepareSerializedUnitsAsync(
                item,
                input,
                baseQuantity,
                saleItemUnits,
                ct);

            originalCostAmount = Cost(serializedUnits.Sum(x => x.Snapshot.UnitCostSnapshot));

            return new PreparedReturnLine(
                Item: item,
                Input: input,
                Product: product,
                Stock: stock,
                BaseQuantity: baseQuantity,
                RefundAmount: refundAmount,
                OriginalCostAmount: originalCostAmount,
                Kind: ReturnLineKind.Serialized,
                SerializedUnits: serializedUnits,
                OriginAllocations: Array.Empty<OriginalLotReturnAllocation>());
        }

        // Path B: Container / Pack subledger tracking
        if (product.TrackingMode == TrackingMode.Container)
        {
            // Case B1: Full sealed pack return
            if (input.InventoryUnitIds.Count == 1)
            {
                var unitId = input.InventoryUnitIds[0];
                var soldUnitSnapshot = saleItemUnits.FirstOrDefault(x => x.InventoryUnitId == unitId);
                if (soldUnitSnapshot is not null)
                {
                    var priorReturned = await _sales.GetReturnedInventoryUnitIdsAsync(item.Id, ct);
                    if (priorReturned.Contains(unitId))
                    {
                        throw new BusinessRuleException(
                            "sales.return_serial_already_returned",
                            "One or more serialized units were already returned.");
                    }

                    var units = await _inventory.GetInventoryUnitsForUpdateAsync(
                        item.ProductId,
                        new[] { unitId },
                        ct);

                    if (units.Count == 1)
                    {
                        var candidate = units[0];
                        if (_warranty is not null)
                        {
                            if (await _warranty.HasActiveClaimForUnitAsync(candidate.Id, ct))
                            {
                                throw new BusinessRuleException(
                                    "sales.return_unit_active_warranty",
                                    "One or more serialized units have an active warranty claim and cannot be returned.");
                            }
                            if (await _warranty.IsUnitTerminallyResolvedAsync(candidate.Id, ct))
                            {
                                throw new BusinessRuleException(
                                    "sales.return_unit_warranty_resolved",
                                    "One or more serialized units have already received terminal warranty resolution and cannot be returned.");
                            }
                        }

                        if (candidate.Status == InventoryUnitStatus.Sold &&
                            candidate.InitialBaseQuantity == baseQuantity)
                        {
                            // Intact whole pack return
                            return new PreparedReturnLine(
                                Item: item,
                                Input: input,
                                Product: product,
                                Stock: stock,
                                BaseQuantity: baseQuantity,
                                RefundAmount: refundAmount,
                                OriginalCostAmount: originalCostAmount,
                                Kind: ReturnLineKind.ContainerFullPack,
                                SerializedUnits: Array.Empty<(InventoryUnit, SaleItemUnit)>(),
                                OriginAllocations: Array.Empty<OriginalLotReturnAllocation>(),
                                FullPackUnit: candidate);
                        }
                    }
                }
            }

            // Case B2: Loose return (partial cut / segment return)
            if (input.InventoryUnitIds.Count > 1)
            {
                throw new BusinessRuleException(
                    "sales.return_serial_count_mismatch",
                    "Container partial returns do not take multiple unit ids.");
            }

            ValidateContainerReturnMetadata(input, product);

            if (input.InventoryUnitIds.Count == 0 &&
                !input.ReturnSourceContainerId.HasValue &&
                saleItemUnits.Count == 0)
            {
                throw new BusinessRuleException(
                    "sales.return_source_container_required",
                    "Container loose return requires a return source container id when no inventory unit id is supplied.");
            }

            if (input.InventoryUnitIds.Count == 0 && input.ReturnSourceContainerId is { } sourceContainerId)
            {
                await ValidateReturnSourceContainerOnSaleAsync(item, sourceContainerId, saleItemUnits, ct);
            }
            else if (input.ReturnSourceContainerId is { } declaredContainerId &&
                     input.InventoryUnitIds.Count == 1 &&
                     input.InventoryUnitIds[0] != declaredContainerId)
            {
                throw new BusinessRuleException(
                    "sales.return_source_container_mismatch",
                    "Return source container id must match the returned inventory unit when both are supplied.");
            }

            if (input.InventoryUnitIds.Count == 1 && _warranty is not null)
            {
                var unitId = input.InventoryUnitIds[0];
                if (await _warranty.HasActiveClaimForUnitAsync(unitId, ct))
                {
                    throw new BusinessRuleException(
                        "sales.return_unit_active_warranty",
                        "One or more serialized units have an active warranty claim and cannot be returned.");
                }
                if (await _warranty.IsUnitTerminallyResolvedAsync(unitId, ct))
                {
                    throw new BusinessRuleException(
                        "sales.return_unit_warranty_resolved",
                        "One or more serialized units have already received terminal warranty resolution and cannot be returned.");
                }
            }

            var containerOrigins = SoldSourceAllocationAuthority.Select(
                await _sales.GetSoldSourceCapacityForUpdateAsync(item.Id, ct),
                baseQuantity);
            if (containerOrigins.Any(x => !x.PurchaseItemId.HasValue || !x.SupplierId.HasValue))
            {
                throw new BusinessRuleException(
                    "sales.return_source_provenance_required",
                    "Container return requires purchase-item and supplier provenance for every sold-source allocation.");
            }

            var containerSuppliers = containerOrigins
                .Where(x => x.SupplierId.HasValue)
                .Select(x => x.SupplierId!.Value)
                .Distinct()
                .ToArray();
            if (containerSuppliers.Length > 1)
            {
                throw new BusinessRuleException(
                    "sales.return_source_supplier_ambiguous",
                    "Return source supplier is ambiguous across sold-source allocations.");
            }

            return new PreparedReturnLine(
                Item: item,
                Input: input,
                Product: product,
                Stock: stock,
                BaseQuantity: baseQuantity,
                RefundAmount: refundAmount,
                OriginalCostAmount: originalCostAmount,
                Kind: ReturnLineKind.ContainerLoose,
                SerializedUnits: Array.Empty<(InventoryUnit, SaleItemUnit)>(),
                OriginAllocations: containerOrigins);
        }

        // Path C: Standard Quantity / Length tracking
        if (input.InventoryUnitIds.Count > 0)
        {
            throw new BusinessRuleException(
                "sales.return_serials_not_allowed",
                "Non-serialized sale items cannot return inventory-unit ids.");
        }

        var originAllocations = SoldSourceAllocationAuthority.Select(
            await _sales.GetSoldSourceCapacityForUpdateAsync(item.Id, ct),
            baseQuantity);

        return new PreparedReturnLine(
            Item: item,
            Input: input,
            Product: product,
            Stock: stock,
            BaseQuantity: baseQuantity,
            RefundAmount: refundAmount,
            OriginalCostAmount: originalCostAmount,
            Kind: ReturnLineKind.Standard,
            SerializedUnits: Array.Empty<(InventoryUnit, SaleItemUnit)>(),
            OriginAllocations: originAllocations);
    }

    public async Task RestoreReturnLineAsync(
        PreparedReturnLine prepared,
        Guid returnItemId,
        InventoryMovement movement,
        Guid actorId,
        CancellationToken ct)
    {
        var destination = SaleMath.ToInventoryBucket(prepared.Input.Disposition);

        switch (prepared.Kind)
        {
            case ReturnLineKind.Serialized:
                await RestoreSerializedUnitsAsync(
                    prepared.SerializedUnits,
                    prepared.Item.BaseQuantity / prepared.SerializedUnits.Count,
                    prepared.Input.Disposition,
                    movement,
                    ct);

                foreach (var pair in prepared.SerializedUnits)
                {
                    _sales.AddReturnItemUnit(new SaleReturnItemUnit
                    {
                        SaleReturnItemId = returnItemId,
                        InventoryUnitId = pair.Unit.Id
                    });
                }
                break;

            case ReturnLineKind.ContainerFullPack:
            {
                var unit = prepared.FullPackUnit!;
                var unitCost = Cost(prepared.OriginalCostAmount / prepared.BaseQuantity);
                Guid lotId;

                if (prepared.Input.Disposition == SaleReturnDisposition.Scrap)
                {
                    lotId = await _costs.AddZeroCarryingLotAsync(
                        prepared.Item.ProductId,
                        prepared.BaseQuantity,
                        unitCost,
                        movement.Id,
                        unit.SourcePurchaseItemId,
                        InventoryBucket.Scrap,
                        ct);
                }
                else
                {
                    lotId = await _costs.AddCarryingValueAndLotWithIdAsync(
                        prepared.Item.ProductId,
                        prepared.BaseQuantity,
                        unitCost,
                        movement.Id,
                        unit.SourcePurchaseItemId,
                        destination,
                        ct);

                    var costState = await _inventory.GetCostStateForUpdateAsync(prepared.Item.ProductId, ct)
                        ?? throw new BusinessRuleException("inventory.cost_state_missing", "Inventory cost state is required.");
                    var represented = Cost(prepared.BaseQuantity * unitCost);
                    costState.TotalInventoryCost = Cost(costState.TotalInventoryCost + prepared.OriginalCostAmount - represented);
                    costState.MovingAverageCost = Cost(costState.TotalInventoryCost / costState.CostedQty);
                }

                var fromStatus = unit.Status;
                unit.Status = prepared.Input.Disposition switch
                {
                    SaleReturnDisposition.RestockSellable => InventoryUnitStatus.InStock,
                    SaleReturnDisposition.Damaged => InventoryUnitStatus.Damaged,
                    SaleReturnDisposition.Defective => InventoryUnitStatus.Defective,
                    SaleReturnDisposition.Scrap => InventoryUnitStatus.Scrapped,
                    _ => throw new ArgumentOutOfRangeException(nameof(prepared.Input.Disposition))
                };
                unit.RemainingBaseQuantity = unit.InitialBaseQuantity;
                unit.PackState = PackState.Sealed;
                unit.InventoryLotId = lotId;
                unit.Version++;

                var alloc = new PackAllocation
                {
                    Id = Guid.CreateVersion7(),
                    PackUnitId = unit.Id,
                    MovementId = movement.Id,
                    LotId = lotId,
                    OperationType = PackAllocationOperationTypes.ReturnRestock,
                    SaleReturnItemId = returnItemId,
                    AllocatedBaseQuantity = prepared.BaseQuantity,
                    UnitCostSnapshot = unitCost,
                    TotalCostSnapshot = prepared.OriginalCostAmount,
                    RemainingQuantityAfter = unit.RemainingBaseQuantity ?? prepared.BaseQuantity,
                    PackStateAfter = PackState.Sealed,
                    ActorId = actorId,
                    OccurredAt = _clock.UtcNow,
                    CreatedAt = _clock.UtcNow
                };
                alloc.ValidateInvariants();
                _inventory.AddPackAllocation(alloc);

                _inventory.AddMovementUnit(new InventoryMovementUnit
                {
                    MovementId = movement.Id,
                    InventoryUnitId = unit.Id,
                    FromStatus = fromStatus,
                    ToStatus = unit.Status
                });

                _sales.AddReturnItemUnit(new SaleReturnItemUnit
                {
                    SaleReturnItemId = returnItemId,
                    InventoryUnitId = unit.Id
                });
                break;
            }

            case ReturnLineKind.ContainerLoose:
            {
                await RestoreLooseContainerReturnAsync(prepared, returnItemId, movement, actorId, ct);
                break;
            }

            case ReturnLineKind.Standard:
                await SoldSourceAllocationAuthority.RestoreReturnAsync(
                    _sales,
                    _inventory,
                    _costs,
                    prepared.Item,
                    returnItemId,
                    prepared.OriginAllocations,
                    prepared.Input.Disposition,
                    prepared.OriginalCostAmount,
                    movement,
                    movement.OccurredAt,
                    ct);
                break;

            default:
                throw new ArgumentOutOfRangeException();
        }
    }

    private static void ValidateContainerReturnMetadata(SaleReturnLineInput input, Product product)
    {
        if (product.TrackingMode != TrackingMode.Container)
        {
            return;
        }

        if (input.Disposition is SaleReturnDisposition.Damaged or SaleReturnDisposition.Defective &&
            string.IsNullOrWhiteSpace(input.ConditionInspectionResult))
        {
            throw new BusinessRuleException(
                "sales.return_condition_inspection_required",
                "Damaged or defective container returns require a condition inspection result.");
        }
    }

    private async Task ValidateReturnSourceContainerOnSaleAsync(
        SaleItem item,
        Guid containerUnitId,
        IReadOnlyList<SaleItemUnit> saleItemUnits,
        CancellationToken ct)
    {
        if (saleItemUnits.Any(x => x.InventoryUnitId == containerUnitId))
        {
            return;
        }

        var units = await _inventory.GetInventoryUnitsForUpdateAsync(item.ProductId, new[] { containerUnitId }, ct);
        if (units.Count != 1)
        {
            throw new BusinessRuleException(
                "sales.return_source_container_invalid",
                "Return source container was not found for this product.");
        }

        var soldSources = await _sales.GetSoldSourceCapacityForUpdateAsync(item.Id, ct);
        if (soldSources.Count == 0)
        {
            throw new BusinessRuleException(
                "sales.return_source_provenance_required",
                "Container return requires purchase-item and supplier provenance for every sold-source allocation.");
        }

        var distinctSuppliers = soldSources
            .Where(x => x.SupplierId.HasValue)
            .Select(x => x.SupplierId!.Value)
            .Distinct()
            .ToArray();
        if (distinctSuppliers.Length > 1)
        {
            throw new BusinessRuleException(
                "sales.return_source_supplier_ambiguous",
                "Return source supplier is ambiguous across sold-source allocations.");
        }
    }

    private async Task RestoreLooseContainerReturnAsync(
        PreparedReturnLine prepared,
        Guid returnItemId,
        InventoryMovement movement,
        Guid actorId,
        CancellationToken ct)
    {
        if (_physicalUnits is null)
        {
            throw new BusinessRuleException(
                "inventory.physical_unit_authority_unavailable",
                "Physical-unit creation authority is unavailable.");
        }
        if (prepared.OriginAllocations.Count == 0 ||
            prepared.OriginAllocations.Any(x => !x.PurchaseItemId.HasValue || !x.SupplierId.HasValue))
        {
            throw new BusinessRuleException(
                "sales.return_source_provenance_required",
                "Container return requires purchase-item and supplier provenance for every sold-source allocation.");
        }

        var restored = new List<(OriginalLotReturnAllocation Source, Guid LotId, decimal Cost, PhysicalUnitCreationEntry Entry)>();
        decimal remainingCost = prepared.OriginalCostAmount;
        for (var index = 0; index < prepared.OriginAllocations.Count; index++)
        {
            var source = prepared.OriginAllocations[index];
            var sourceCost = index == prepared.OriginAllocations.Count - 1
                ? remainingCost
                : Cost(prepared.OriginalCostAmount * source.Quantity / prepared.BaseQuantity);
            remainingCost = Cost(remainingCost - sourceCost);
            var sourceUnitCost = Cost(sourceCost / source.Quantity);
            var lotId = prepared.Input.Disposition == SaleReturnDisposition.Scrap
                ? await _costs.AddZeroCarryingLotAsync(
                    prepared.Item.ProductId, source.Quantity, sourceUnitCost, movement.Id,
                    source.PurchaseItemId, InventoryBucket.Scrap, ct)
                : await _costs.AddCarryingValueAndLotByAmountAsync(
                    prepared.Item.ProductId, source.Quantity, sourceCost, movement.Id,
                    source.PurchaseItemId, SaleMath.ToInventoryBucket(prepared.Input.Disposition), ct);

            var entry = _packAllocations.CreateLooseReturnEntry(
                returnedQuantity: source.Quantity,
                unitCost: sourceUnitCost,
                restoredLotId: lotId,
                isDamaged: prepared.Input.Disposition == SaleReturnDisposition.Damaged,
                sourcePurchaseItemId: source.PurchaseItemId,
                restockAsRemnant: prepared.Input.IsRestockAsRemnant);
            entry = entry with { AcquisitionCost = sourceCost };
            if (prepared.Input.Disposition == SaleReturnDisposition.Defective)
                entry = entry with { Status = InventoryUnitStatus.Defective };
            else if (prepared.Input.Disposition == SaleReturnDisposition.Scrap)
                entry = entry with { Status = InventoryUnitStatus.Scrapped };

            if (prepared.Input.IsRestockAsRemnant &&
                prepared.Input.Disposition == SaleReturnDisposition.RestockSellable)
            {
                var remnant = _packAllocations.EvaluateRemnant(source.Quantity);
                if (!remnant.IsUsable)
                {
                    throw new BusinessRuleException(
                        "sales.return_remnant_not_sellable",
                        remnant.WarningMessage ?? "Returned remnant quantity is not sellable.");
                }
            }

            restored.Add((source, lotId, sourceCost, entry));
        }

        // Physical-unit creation takes one supplier per batch. Keep each source slice's
        // purchase provenance intact and acquire supplier-product locks in stable order.
        foreach (var supplierGroup in restored.GroupBy(x => x.Source.SupplierId!.Value).OrderBy(x => x.Key))
        {
            var batch = supplierGroup.ToArray();
            var creation = await _physicalUnits.CreateAsync(
                supplierGroup.Key, prepared.Item.ProductId, batch.Select(x => x.Entry).ToArray(), ct);
            if (!creation.IsSuccess || creation.Value is null || creation.Value.Count != batch.Length)
            {
                throw new BusinessRuleException(
                    creation.Error?.Code ?? "inventory.loose_return_failed",
                    creation.Error?.Message ?? "Failed to create all source-linked loose return units.");
            }

            for (var index = 0; index < batch.Length; index++)
            {
                var row = batch[index];
                var looseUnit = creation.Value[index];
                var packAllocation = new PackAllocation
                {
                    Id = Guid.CreateVersion7(),
                    PackUnitId = looseUnit.Id,
                    MovementId = movement.Id,
                    LotId = row.LotId,
                    OperationType = PackAllocationOperationTypes.ReturnRestock,
                    SaleReturnItemId = returnItemId,
                    AllocatedBaseQuantity = row.Source.Quantity,
                    UnitCostSnapshot = Cost(row.Cost / row.Source.Quantity),
                    TotalCostSnapshot = row.Cost,
                    RemainingQuantityAfter = looseUnit.RemainingBaseQuantity ?? row.Source.Quantity,
                    PackStateAfter = looseUnit.PackState ?? PackState.Open,
                    ActorId = actorId,
                    OccurredAt = movement.OccurredAt,
                    CreatedAt = _clock.UtcNow
                };
                packAllocation.ValidateInvariants();
                _inventory.AddPackAllocation(packAllocation);
                _inventory.AddMovementUnit(new InventoryMovementUnit
                {
                    MovementId = movement.Id,
                    InventoryUnitId = looseUnit.Id,
                    FromStatus = null,
                    ToStatus = looseUnit.Status
                });
                _sales.AddReturnItemUnit(new SaleReturnItemUnit
                {
                    SaleReturnItemId = returnItemId,
                    InventoryUnitId = looseUnit.Id
                });
                _sales.AddReturnSourceAllocation(new SaleReturnSourceAllocation
                {
                    SaleReturnItemId = returnItemId,
                    SaleConsumptionId = row.Source.SaleConsumptionId,
                    ReturnMovementId = movement.Id,
                    RestoredInventoryLotId = row.LotId,
                    BaseQuantity = row.Source.Quantity,
                    ClientOperationId = movement.CorrelationId,
                    ActorId = actorId,
                    OccurredAt = movement.OccurredAt
                });
            }
        }

        if (prepared.Input.Disposition != SaleReturnDisposition.Scrap)
        {
            var representedCost = Cost(restored.Sum(x => x.Entry.AcquisitionCost));
            if (representedCost != prepared.OriginalCostAmount)
            {
                throw new BusinessRuleException("inventory.return_cost_provenance_mismatch",
                    "Restored physical units must represent the exact source carrying amount.");
            }
        }
    }

    private async Task<IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)>> PrepareSerializedUnitsAsync(
        SaleItem item,
        SaleReturnLineInput input,
        decimal baseQuantity,
        IReadOnlyList<SaleItemUnit> soldUnits,
        CancellationToken ct)
    {
        if (soldUnits.Count == 0)
        {
            throw new BusinessRuleException(
                "sales.return_serial_count_mismatch",
                "Serialized return requires one distinct sold unit per base unit.");
        }

        var basePerUnit = item.BaseQuantity / soldUnits.Count;
        var physicalQuantity = baseQuantity / basePerUnit;
        if (!QuantityMath.IsWhole(physicalQuantity) ||
            physicalQuantity <= 0 ||
            physicalQuantity > int.MaxValue ||
            input.InventoryUnitIds.Count != decimal.ToInt32(physicalQuantity) ||
            input.InventoryUnitIds.Distinct().Count() != input.InventoryUnitIds.Count)
        {
            throw new BusinessRuleException(
                "sales.return_serial_count_mismatch",
                "Serialized return requires one distinct sold unit per base unit.");
        }

        var soldById = soldUnits.ToDictionary(x => x.InventoryUnitId);
        if (input.InventoryUnitIds.Any(x => !soldById.ContainsKey(x)))
        {
            throw new BusinessRuleException(
                "sales.return_serial_not_original",
                "One or more serialized units were not sold on this sale item.");
        }

        var priorReturned = await _sales.GetReturnedInventoryUnitIdsAsync(item.Id, ct);
        if (input.InventoryUnitIds.Any(priorReturned.Contains))
        {
            throw new BusinessRuleException(
                "sales.return_serial_already_returned",
                "One or more serialized units were already returned.");
        }

        var units = await _inventory.GetInventoryUnitsForUpdateAsync(
            item.ProductId,
            input.InventoryUnitIds,
            ct);

        if (units.Count != input.InventoryUnitIds.Count ||
            units.Any(x => x.Status != InventoryUnitStatus.Sold))
        {
            throw new BusinessRuleException(
                "sales.return_serial_not_eligible",
                "One or more serialized units are not currently eligible for return.");
        }

        if (_warranty is not null)
        {
            foreach (var unit in units)
            {
                if (await _warranty.HasActiveClaimForUnitAsync(unit.Id, ct))
                {
                    throw new BusinessRuleException(
                        "sales.return_unit_active_warranty",
                        "One or more serialized units have an active warranty claim and cannot be returned.");
                }

                if (await _warranty.IsUnitTerminallyResolvedAsync(unit.Id, ct))
                {
                    throw new BusinessRuleException(
                        "sales.return_unit_warranty_resolved",
                        "One or more serialized units have already received terminal warranty resolution and cannot be returned.");
                }
            }
        }

        return units
            .OrderBy(x => x.Id)
            .Select(x => (x, soldById[x.Id]))
            .ToArray();
    }

    private async Task RestoreSerializedUnitsAsync(
        IReadOnlyList<(InventoryUnit Unit, SaleItemUnit Snapshot)> units,
        decimal basePerUnit,
        SaleReturnDisposition disposition,
        InventoryMovement movement,
        CancellationToken ct)
    {
        foreach (var pair in units)
        {
            var destination = SaleMath.ToInventoryBucket(disposition);
            Guid lotId;

            if (disposition == SaleReturnDisposition.Scrap)
            {
                lotId = await _costs.AddZeroCarryingLotAsync(
                    pair.Unit.ProductId,
                    basePerUnit,
                    pair.Snapshot.UnitCostSnapshot / basePerUnit,
                    movement.Id,
                    pair.Unit.SourcePurchaseItemId,
                    InventoryBucket.Scrap,
                    ct);
            }
            else
            {
                lotId = await _costs.AddCarryingValueAndLotWithIdAsync(
                    pair.Unit.ProductId,
                    basePerUnit,
                    pair.Snapshot.UnitCostSnapshot / basePerUnit,
                    movement.Id,
                    pair.Unit.SourcePurchaseItemId,
                    destination,
                    ct);

                var costState = await _inventory.GetCostStateForUpdateAsync(pair.Unit.ProductId, ct)
                    ?? throw new BusinessRuleException("inventory.cost_state_missing", "Inventory cost state is required.");
                var represented = Cost(basePerUnit * Cost(pair.Snapshot.UnitCostSnapshot / basePerUnit));
                costState.TotalInventoryCost = Cost(costState.TotalInventoryCost + pair.Snapshot.UnitCostSnapshot - represented);
                costState.MovingAverageCost = Cost(costState.TotalInventoryCost / costState.CostedQty);
            }

            var from = pair.Unit.Status;
            pair.Unit.Status = disposition switch
            {
                SaleReturnDisposition.RestockSellable => InventoryUnitStatus.InStock,
                SaleReturnDisposition.Damaged => InventoryUnitStatus.Damaged,
                SaleReturnDisposition.Defective => InventoryUnitStatus.Defective,
                SaleReturnDisposition.Scrap => InventoryUnitStatus.Scrapped,
                _ => throw new ArgumentOutOfRangeException(nameof(disposition))
            };
            pair.Unit.InventoryLotId = lotId;
            pair.Unit.Version++;

            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = pair.Unit.Id,
                FromStatus = from,
                ToStatus = pair.Unit.Status
            });
        }
    }

    private static decimal CalculateResidualAmount(
        decimal originalTotal,
        decimal originalBaseQuantity,
        decimal priorReturnedBaseQuantity,
        decimal currentBaseQuantity,
        decimal priorAmount,
        int decimals)
    {
        var cumulativeQty = QuantityMath.RoundQuantity(
            priorReturnedBaseQuantity + currentBaseQuantity);

        var targetCumulative = cumulativeQty == originalBaseQuantity
            ? originalTotal
            : decimal.Round(
                originalTotal * cumulativeQty / originalBaseQuantity,
                decimals,
                MidpointRounding.AwayFromZero);

        return decimal.Round(
            targetCumulative - priorAmount,
            decimals,
            MidpointRounding.AwayFromZero);
    }

    private static decimal Cost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}
