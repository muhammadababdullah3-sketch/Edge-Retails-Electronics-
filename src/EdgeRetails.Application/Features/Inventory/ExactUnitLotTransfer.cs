using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

internal static class ExactUnitLotTransfer
{
    public static async Task<Result> TransferAsync(
        IInventoryRepository inventory, IReadOnlyList<InventoryUnit> units,
        InventoryBucket from, InventoryBucket to, CancellationToken cancellationToken)
    {
        foreach (var group in units.GroupBy(x => x.InventoryLotId).OrderBy(x => x.Key))
        {
            if (group.Key is not Guid lotId)
                return Result.Failure("inventory.exact_unit_lot_missing", "Selected physical unit has no lot provenance.");
            decimal quantity = 0m;
            foreach (var unit in group)
                quantity += await inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, cancellationToken);
            var source = await inventory.GetLotBucketBalanceForUpdateAsync(lotId, from, cancellationToken);
            if (source is null || source.Quantity < quantity)
                return Result.Failure("inventory.exact_unit_lot_insufficient", "Selected physical unit's source lot quantity is unavailable.");
            var target = await inventory.GetLotBucketBalanceForUpdateAsync(lotId, to, cancellationToken);
            if (target is null)
            {
                target = new InventoryLotBucketBalance { LotId = lotId, StockBucket = to };
                inventory.AddLotBucketBalance(target);
            }
            source.Quantity -= quantity;
            target.Quantity += quantity;
        }
        return Result.Success();
    }
}
