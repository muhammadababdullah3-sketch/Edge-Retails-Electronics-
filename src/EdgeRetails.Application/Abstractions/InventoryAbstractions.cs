using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Abstractions;

public interface IInventoryCostAllocator
{
    Task TransferBucketAsync(
        Guid productId,
        InventoryBucket from,
        InventoryBucket to,
        decimal baseQuantity,
        CancellationToken cancellationToken);

    Task<decimal> RemoveCarryingValueAsync(
        Guid productId,
        decimal baseQuantity,
        decimal? exactCost,
        CancellationToken cancellationToken);

    Task AddCarryingValueAndLotAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        CancellationToken cancellationToken);

    Task AddCarryingValueAndLotAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken);

    Task<Guid> AddCarryingValueAndLotWithIdAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken);

    Task<Guid> AddCarryingValueAndLotWithIdAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        InventoryBucket initialBucket,
        CancellationToken cancellationToken);

    Task<Guid> AddZeroCarryingLotAsync(
        Guid productId,
        decimal baseQuantity,
        decimal originalUnitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        InventoryBucket initialBucket,
        CancellationToken cancellationToken);

    Task<decimal?> GetCurrentUnitCostAsync(
        Guid productId,
        CancellationToken cancellationToken);

    Task ConsumeBucketAsync(
        Guid productId,
        InventoryBucket bucket,
        decimal baseQuantity,
        Guid movementId,
        decimal unitCostSnapshot,
        CancellationToken cancellationToken);
}
