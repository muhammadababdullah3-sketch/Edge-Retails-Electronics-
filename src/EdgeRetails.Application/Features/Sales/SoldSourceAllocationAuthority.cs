using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Sales;
using EdgeRetails.Domain.Warranty;

namespace EdgeRetails.Application.Features.Sales;

internal sealed record OriginalLotReturnAllocation(
    Guid SaleConsumptionId, Guid LotId, Guid? PurchaseItemId, Guid? SupplierId, decimal Quantity);

internal static class SoldSourceAllocationAuthority
{
    public static IReadOnlyList<OriginalLotReturnAllocation> Select(
        IReadOnlyList<SoldSourceCapacity> positions, decimal quantity, Guid? supplierId = null)
    {
        var remaining = QuantityMath.RoundQuantity(quantity);
        if (remaining <= 0m)
        {
            throw new BusinessRuleException("sales.return_quantity_invalid", "Source quantity must be positive.");
        }
        var result = new List<OriginalLotReturnAllocation>();
        foreach (var source in positions.Where(x => supplierId is null || x.SupplierId == supplierId).OrderBy(x => x.SaleConsumptionId))
        {
            var take = QuantityMath.RoundQuantity(Math.Min(remaining, source.RemainingQuantity));
            if (take <= 0m)
            {
                continue;
            }
            result.Add(new(source.SaleConsumptionId, source.OriginalLotId, source.PurchaseItemId, source.SupplierId, take));
            remaining = QuantityMath.RoundQuantity(remaining - take);
        }
        if (remaining > 0m)
        {
            throw new BusinessRuleException("sales.return_source_capacity_exceeded", "Original sold-source capacity cannot cover this quantity.");
        }
        return result;
    }

    public static async Task RestoreReturnAsync(
        ISalesRepository sales, IInventoryRepository inventory, IInventoryCostAllocator costs,
        SaleItem item, Guid returnItemId, IReadOnlyList<OriginalLotReturnAllocation> allocations,
        SaleReturnDisposition disposition, decimal originalCostAmount, InventoryMovement movement,
        DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        var quantity = QuantityMath.RoundQuantity(allocations.Sum(x => x.Quantity));
        if (quantity <= 0m)
        {
            throw new BusinessRuleException("sales.return_origin_allocation_missing", "Original sold-source allocation is required.");
        }
        // Return valuation remains the frozen original SaleItem residual, never source purchase cost/current MWA.
        var unitCost = Cost(originalCostAmount / quantity);
        decimal representedCost = 0m;
        foreach (var allocation in allocations)
        {
            var restoredLot = disposition == SaleReturnDisposition.Scrap
                ? await costs.AddZeroCarryingLotAsync(item.ProductId, allocation.Quantity, unitCost, movement.Id,
                    allocation.PurchaseItemId, InventoryBucket.Scrap, cancellationToken)
                : await costs.AddCarryingValueAndLotWithIdAsync(item.ProductId, allocation.Quantity, unitCost, movement.Id,
                    allocation.PurchaseItemId, SaleMath.ToInventoryBucket(disposition), cancellationToken);
            representedCost = Cost(representedCost + Cost(allocation.Quantity * unitCost));
            sales.AddReturnSourceAllocation(new SaleReturnSourceAllocation
            {
                SaleReturnItemId = returnItemId, SaleConsumptionId = allocation.SaleConsumptionId,
                ReturnMovementId = movement.Id, RestoredInventoryLotId = restoredLot,
                BaseQuantity = allocation.Quantity, ClientOperationId = movement.CorrelationId,
                ActorId = movement.ActorId, OccurredAt = occurredAt
            });
        }
        if (disposition != SaleReturnDisposition.Scrap && representedCost != originalCostAmount)
        {
            var state = await inventory.GetCostStateForUpdateAsync(item.ProductId, cancellationToken)
                ?? throw new BusinessRuleException("inventory.cost_state_missing", "Return cost state is required.");
            state.TotalInventoryCost = Cost(state.TotalInventoryCost + originalCostAmount - representedCost);
            state.MovingAverageCost = Cost(state.TotalInventoryCost / state.CostedQty);
        }
    }

    private static decimal Cost(decimal value) => decimal.Round(value, 6, MidpointRounding.AwayFromZero);
}
