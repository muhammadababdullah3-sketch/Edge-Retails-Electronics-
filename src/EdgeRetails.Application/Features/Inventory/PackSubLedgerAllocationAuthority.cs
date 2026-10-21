using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record PackAllocationRequest(
    Guid ProductId,
    decimal RequestedBaseQuantity,
    Guid MovementId,
    string OperationType,
    Guid ActorId,
    DateTimeOffset OccurredAt,
    Guid? SaleItemId = null,
    Guid? SaleReturnItemId = null,
    Guid? ThakaIssueItemId = null,
    Guid? PreferredPackUnitId = null,
    bool AllowSplitCuts = false,
    bool IsCableOrLengthProduct = false,
    decimal MinimumCutIncrement = 1m);

public sealed record PackAllocationResult(
    IReadOnlyList<PackAllocation> Allocations,
    decimal TotalAllocatedBaseQuantity,
    decimal TotalCostDerecognized,
    bool WasSplitCut,
    int PacksAffectedCount);

public sealed record PackMeasurementReconciliationResult(
    InventoryUnit Unit,
    decimal PreviousRemainingQuantity,
    decimal MeasuredRemainingQuantity,
    decimal DeficitQuantity,
    PackAllocation? Allocation,
    bool IsShortRemnant,
    string? WarningMessage);

public sealed record ShortRemnantEvaluation(
    bool IsShortRemnant,
    bool IsUsable,
    decimal RemainingQuantity,
    decimal MinimumSellableIncrement,
    string? WarningMessage);

public interface IPackSubLedgerAllocationAuthority
{
    PackAllocationResult Allocate(
        IReadOnlyList<InventoryUnit> availableUnits,
        PackAllocationRequest request);

    PhysicalUnitCreationEntry CreateLooseReturnEntry(
        decimal returnedQuantity,
        decimal unitCost,
        Guid restoredLotId,
        bool isDamaged = false,
        Guid? sourcePurchaseItemId = null);

    PackMeasurementReconciliationResult ReconcileMeasurement(
        InventoryUnit unit,
        decimal measuredRemainingQuantity,
        decimal minimumSellableIncrement = 1m,
        Guid movementId = default,
        Guid actorId = default,
        DateTimeOffset occurredAt = default,
        string operationType = PackAllocationOperationTypes.StocktakeAdjustment);

    ShortRemnantEvaluation EvaluateRemnant(
        decimal remainingQuantity,
        decimal minimumSellableIncrement = 1m);

    PackAllocation ScrapRemnant(
        InventoryUnit unit,
        Guid movementId,
        Guid actorId,
        DateTimeOffset occurredAt,
        string? reason = null);
}

public sealed class PackSubLedgerAllocationAuthority : IPackSubLedgerAllocationAuthority
{
    public PackAllocationResult Allocate(
        IReadOnlyList<InventoryUnit> availableUnits,
        PackAllocationRequest request)
    {
        ArgumentNullException.ThrowIfNull(availableUnits);
        ArgumentNullException.ThrowIfNull(request);

        if (request.RequestedBaseQuantity <= 0)
        {
            throw new BusinessRuleException(
                "inventory.pack_alloc_quantity_positive",
                "Requested allocation base quantity must be strictly positive.");
        }

        var remainingToAllocate = QuantityMath.RoundQuantity(request.RequestedBaseQuantity);

        // Filter eligible units: must be InStock, belong to product, and have positive remaining capacity
        var eligibleUnits = availableUnits
            .Where(u => u.ProductId == request.ProductId &&
                        u.Status == InventoryUnitStatus.InStock &&
                        u.PackState is not null &&
                        u.PackState != PackState.Consumed &&
                        (u.RemainingBaseQuantity ?? 0m) > 0m)
            .ToList();

        if (eligibleUnits.Count == 0)
        {
            throw new BusinessRuleException(
                "inventory.pack_no_eligible_units",
                "No eligible pack or container units available with positive remaining quantity.");
        }

        var allocations = new List<PackAllocation>();
        decimal totalCost = 0m;
        bool wasSplit = false;

        // Path A: Preferred pack unit override (P11-IMP-052)
        if (request.PreferredPackUnitId.HasValue)
        {
            var preferred = eligibleUnits.FirstOrDefault(u => u.Id == request.PreferredPackUnitId.Value)
                ?? throw new BusinessRuleException(
                    "inventory.preferred_pack_unavailable",
                    $"Preferred pack unit {request.PreferredPackUnitId.Value} is not available or not sellable.");

            var availableInPreferred = preferred.RemainingBaseQuantity ?? 0m;
            if (request.IsCableOrLengthProduct && availableInPreferred < remainingToAllocate)
            {
                throw new BusinessRuleException(
                    "inventory.preferred_cable_insufficient_length",
                    $"Preferred cable pack has {availableInPreferred}m available, which is insufficient for continuous length request of {remainingToAllocate}m.");
            }

            var take = Math.Min(remainingToAllocate, availableInPreferred);
            var alloc = CreateAllocationForUnit(preferred, take, request);
            allocations.Add(alloc);
            totalCost += alloc.TotalCostSnapshot;
            remainingToAllocate = QuantityMath.RoundQuantity(remainingToAllocate - take);

            if (remainingToAllocate > 0)
            {
                throw new BusinessRuleException(
                    "inventory.preferred_pack_insufficient_quantity",
                    $"Preferred pack cannot fulfill complete requested quantity of {request.RequestedBaseQuantity}. Remaining unallocated: {remainingToAllocate}.");
            }

            return new PackAllocationResult(
                allocations,
                request.RequestedBaseQuantity,
                totalCost,
                WasSplitCut: false,
                PacksAffectedCount: 1);
        }

        // Path B: Continuous cable / length product selection (P11-IMP-056, P11-IMP-057)
        if (request.IsCableOrLengthProduct)
        {
            // First look for single continuous segment with RemainingBaseQuantity >= remainingToAllocate
            var continuousCandidate = eligibleUnits
                .Where(u => (u.RemainingBaseQuantity ?? 0m) >= remainingToAllocate)
                // Prefer smallest sufficient segment to minimize remaining fragment waste
                .OrderBy(u => u.RemainingBaseQuantity ?? 0m)
                .ThenBy(u => u.PackState == PackState.Open ? 0 : 1) // prefer open coil if sufficient
                .FirstOrDefault();

            if (continuousCandidate is not null)
            {
                var alloc = CreateAllocationForUnit(continuousCandidate, remainingToAllocate, request);
                allocations.Add(alloc);
                totalCost += alloc.TotalCostSnapshot;

                return new PackAllocationResult(
                    allocations,
                    request.RequestedBaseQuantity,
                    totalCost,
                    WasSplitCut: false,
                    PacksAffectedCount: 1);
            }

            // No single continuous segment has sufficient length
            if (!request.AllowSplitCuts)
            {
                throw new BusinessRuleException(
                    "sales.cable_split_required",
                    $"No single continuous cable segment has sufficient length for requested {remainingToAllocate}m. Split cuts require explicit caller acceptance.");
            }

            wasSplit = true;
        }

        // Path C: Automatic Loose-First selection (P11-IMP-051, P11-IMP-054)
        // Order: Loose stock / Open packs first, then Sealed packs
        // Within Open packs: oldest/smallest first to consolidate remnants
        var sortedUnits = eligibleUnits
            .OrderBy(u => u.IsLooseStock ? 0 : (u.PackState == PackState.Open ? 1 : 2))
            .ThenBy(u => u.RemainingBaseQuantity ?? 0m)
            .ThenBy(u => u.CreatedAt)
            .ToList();

        foreach (var unit in sortedUnits)
        {
            if (remainingToAllocate <= 0)
                break;

            var availableInUnit = unit.RemainingBaseQuantity ?? 0m;
            var take = Math.Min(remainingToAllocate, availableInUnit);

            if (take <= 0)
                continue;

            var alloc = CreateAllocationForUnit(unit, take, request);
            allocations.Add(alloc);
            totalCost += alloc.TotalCostSnapshot;
            remainingToAllocate = QuantityMath.RoundQuantity(remainingToAllocate - take);
        }

        if (remainingToAllocate > 0)
        {
            throw new BusinessRuleException(
                "inventory.pack_insufficient_total_quantity",
                $"Insufficient pack stock to fulfill {request.RequestedBaseQuantity}. Unallocated deficit: {remainingToAllocate}.");
        }

        if (allocations.Count > 1)
        {
            wasSplit = true;
        }

        return new PackAllocationResult(
            allocations,
            request.RequestedBaseQuantity,
            totalCost,
            WasSplitCut: wasSplit,
            PacksAffectedCount: allocations.Count);
    }

    private static decimal RoundCost(decimal value) =>
        decimal.Round(value, 6, MidpointRounding.AwayFromZero);

    private static PackAllocation CreateAllocationForUnit(
        InventoryUnit unit,
        decimal quantityToTake,
        PackAllocationRequest request)
    {
        unit.ValidatePackInvariants();

        var initialQty = unit.InitialBaseQuantity ?? 1m;
        var unitCostSnapshot = RoundCost(unit.AcquisitionCost / initialQty);
        var totalCostSnapshot = RoundCost(quantityToTake * unitCostSnapshot);

        // Apply consumption to domain unit (mutates unit.RemainingBaseQuantity and unit.PackState)
        unit.ConsumeFromPack(quantityToTake);

        var allocation = new PackAllocation
        {
            Id = Guid.CreateVersion7(),
            PackUnitId = unit.Id,
            MovementId = request.MovementId,
            LotId = unit.InventoryLotId ?? Guid.Empty,
            OperationType = request.OperationType,
            SaleItemId = request.SaleItemId,
            SaleReturnItemId = request.SaleReturnItemId,
            ThakaIssueItemId = request.ThakaIssueItemId,
            AllocatedBaseQuantity = quantityToTake,
            UnitCostSnapshot = unitCostSnapshot,
            TotalCostSnapshot = totalCostSnapshot,
            RemainingQuantityAfter = unit.RemainingBaseQuantity ?? 0m,
            PackStateAfter = unit.PackState ?? PackState.Consumed,
            ActorId = request.ActorId,
            OccurredAt = request.OccurredAt,
            CreatedAt = request.OccurredAt
        };

        if (allocation.MovementId != Guid.Empty)
        {
            allocation.ValidateInvariants();
        }
        return allocation;
    }

    public PhysicalUnitCreationEntry CreateLooseReturnEntry(
        decimal returnedQuantity,
        decimal unitCost,
        Guid restoredLotId,
        bool isDamaged = false,
        Guid? sourcePurchaseItemId = null)
    {
        var roundedQuantity = QuantityMath.RoundQuantity(returnedQuantity);
        if (roundedQuantity <= 0)
        {
            throw new BusinessRuleException(
                "inventory.loose_return_quantity_positive",
                "Returned loose quantity must be strictly positive.");
        }

        var acquisitionCost = RoundCost(roundedQuantity * unitCost);

        return new PhysicalUnitCreationEntry(
            SerialNumber: null,
            Imei1: null,
            Imei2: null,
            Status: isDamaged ? InventoryUnitStatus.Damaged : InventoryUnitStatus.InStock,
            AcquisitionCost: acquisitionCost,
            InventoryLotId: restoredLotId,
            OriginType: InventoryUnitOriginType.Purchase,
            SourcePurchaseItemId: sourcePurchaseItemId ?? Guid.CreateVersion7(),
            InitialBaseQuantity: roundedQuantity,
            RemainingBaseQuantity: roundedQuantity,
            PackState: PackState.Open,
            IsLooseStock: true);
    }

    public PackMeasurementReconciliationResult ReconcileMeasurement(
        InventoryUnit unit,
        decimal measuredRemainingQuantity,
        decimal minimumSellableIncrement = 1m,
        Guid movementId = default,
        Guid actorId = default,
        DateTimeOffset occurredAt = default,
        string operationType = PackAllocationOperationTypes.StocktakeAdjustment)
    {
        ArgumentNullException.ThrowIfNull(unit);

        if (unit.PackState is null)
        {
            throw new BusinessRuleException(
                "inventory.unit_not_a_pack",
                "Unit is not a physical pack or container.");
        }

        var roundedMeasured = QuantityMath.RoundQuantity(measuredRemainingQuantity);
        if (roundedMeasured < 0)
        {
            throw new BusinessRuleException(
                "inventory.pack_remaining_quantity_nonnegative",
                "Measured remaining quantity cannot be negative.");
        }

        var initialCapacity = unit.InitialBaseQuantity ?? 0m;
        if (initialCapacity > 0 && roundedMeasured > initialCapacity)
        {
            throw new BusinessRuleException(
                "inventory.pack_overcapacity",
                $"Measured remaining quantity {roundedMeasured} cannot exceed initial pack capacity {initialCapacity}.");
        }

        var previousRemaining = unit.RemainingBaseQuantity ?? 0m;
        if (roundedMeasured > previousRemaining)
        {
            throw new BusinessRuleException(
                "inventory.pack_positive_capacity_overwrite_not_allowed",
                "Measured remaining quantity cannot exceed current recorded pack capacity; positive physical stock corrections require explicit positive adjustment provenance.");
        }

        var evaluation = EvaluateRemnant(roundedMeasured, minimumSellableIncrement);
        var delta = QuantityMath.RoundQuantity(previousRemaining - roundedMeasured);

        if (delta == 0m)
        {
            return new PackMeasurementReconciliationResult(
                unit,
                previousRemaining,
                roundedMeasured,
                DeficitQuantity: 0m,
                Allocation: null,
                evaluation.IsShortRemnant,
                evaluation.WarningMessage);
        }

        if (roundedMeasured == 0m)
        {
            unit.RemainingBaseQuantity = 0m;
            unit.PackState = PackState.Consumed;
        }
        else
        {
            unit.RemainingBaseQuantity = roundedMeasured;
            unit.PackState = PackState.Open;
        }

        unit.ValidatePackInvariants();
        unit.Version++;

        var initialQty = unit.InitialBaseQuantity ?? 1m;
        var unitCost = RoundCost(unit.AcquisitionCost / initialQty);
        var totalCost = RoundCost(delta * unitCost);

        var allocation = new PackAllocation
        {
            Id = Guid.CreateVersion7(),
            PackUnitId = unit.Id,
            MovementId = movementId,
            LotId = unit.InventoryLotId ?? Guid.Empty,
            OperationType = operationType,
            AllocatedBaseQuantity = delta,
            UnitCostSnapshot = unitCost,
            TotalCostSnapshot = totalCost,
            RemainingQuantityAfter = roundedMeasured,
            PackStateAfter = unit.PackState ?? PackState.Consumed,
            ActorId = actorId,
            OccurredAt = occurredAt,
            CreatedAt = occurredAt
        };

        if (allocation.MovementId != Guid.Empty && allocation.LotId != Guid.Empty)
        {
            allocation.ValidateInvariants();
        }

        return new PackMeasurementReconciliationResult(
            unit,
            previousRemaining,
            roundedMeasured,
            DeficitQuantity: delta,
            Allocation: allocation,
            evaluation.IsShortRemnant,
            evaluation.WarningMessage);
    }

    public ShortRemnantEvaluation EvaluateRemnant(
        decimal remainingQuantity,
        decimal minimumSellableIncrement = 1m)
    {
        var rounded = QuantityMath.RoundQuantity(remainingQuantity);
        var roundedThreshold = QuantityMath.RoundQuantity(minimumSellableIncrement);
        if (roundedThreshold <= 0)
        {
            roundedThreshold = 1m;
        }

        if (rounded <= 0)
        {
            return new ShortRemnantEvaluation(
                IsShortRemnant: false,
                IsUsable: false,
                RemainingQuantity: 0m,
                MinimumSellableIncrement: roundedThreshold,
                WarningMessage: "Pack is fully consumed.");
        }

        if (rounded < roundedThreshold)
        {
            return new ShortRemnantEvaluation(
                IsShortRemnant: true,
                IsUsable: true,
                RemainingQuantity: rounded,
                MinimumSellableIncrement: roundedThreshold,
                WarningMessage: $"Remnant quantity {rounded} is below the minimum sellable increment of {roundedThreshold}. Usable piece preserved for review.");
        }

        return new ShortRemnantEvaluation(
            IsShortRemnant: false,
            IsUsable: true,
            RemainingQuantity: rounded,
            MinimumSellableIncrement: roundedThreshold,
            WarningMessage: null);
    }

    public PackAllocation ScrapRemnant(
        InventoryUnit unit,
        Guid movementId,
        Guid actorId,
        DateTimeOffset occurredAt,
        string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        unit.ValidatePackInvariants();

        var quantityToScrap = unit.RemainingBaseQuantity ?? 0m;
        if (quantityToScrap <= 0)
        {
            throw new BusinessRuleException(
                "inventory.pack_already_consumed",
                "Pack has no remaining quantity to scrap.");
        }

        unit.RemainingBaseQuantity = 0m;
        unit.PackState = PackState.Consumed;
        unit.ValidatePackInvariants();
        unit.Version++;

        var initialQty = unit.InitialBaseQuantity ?? 1m;
        var unitCost = RoundCost(unit.AcquisitionCost / initialQty);
        var totalCost = RoundCost(quantityToScrap * unitCost);

        var allocation = new PackAllocation
        {
            Id = Guid.CreateVersion7(),
            PackUnitId = unit.Id,
            MovementId = movementId,
            LotId = unit.InventoryLotId ?? Guid.Empty,
            OperationType = PackAllocationOperationTypes.ScrapRemnant,
            AllocatedBaseQuantity = quantityToScrap,
            UnitCostSnapshot = unitCost,
            TotalCostSnapshot = totalCost,
            RemainingQuantityAfter = 0m,
            PackStateAfter = PackState.Consumed,
            ActorId = actorId,
            OccurredAt = occurredAt,
            CreatedAt = occurredAt
        };

        if (allocation.MovementId != Guid.Empty && allocation.LotId != Guid.Empty)
        {
            allocation.ValidateInvariants();
        }

        return allocation;
    }
}
