using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class InventoryCostAllocator : IInventoryCostAllocator
{
    private readonly EdgeRetailsDbContext _db;
    private readonly IInventoryRepository _inventory;
    private readonly IClock _clock;

    public InventoryCostAllocator(
        EdgeRetailsDbContext db,
        IInventoryRepository inventory,
        IClock clock)
    {
        _db = db;
        _inventory = inventory;
        _clock = clock;
    }

    public async Task TransferBucketAsync(
        Guid productId,
        InventoryBucket from,
        InventoryBucket to,
        decimal baseQuantity,
        CancellationToken cancellationToken)
    {
        var remaining = RequirePositiveQuantity(baseQuantity);
        var positions = await _inventory.GetLotBucketPositionsForUpdateAsync(
            productId,
            from,
            cancellationToken);

        EnsureAvailable(positions, remaining, from);

        foreach (var position in positions)
        {
            if (remaining == 0)
            {
                break;
            }

            var take = Math.Min(position.Balance.Quantity, remaining);
            take = QuantityMath.RoundQuantity(take);
            position.Balance.Quantity = QuantityMath.RoundQuantity(
                position.Balance.Quantity - take);

            var target = await GetLotBucketForUpdateAsync(
                position.Lot.Id,
                to,
                cancellationToken);

            if (target is null)
            {
                target = new InventoryLotBucketBalance
                {
                    LotId = position.Lot.Id,
                    StockBucket = to,
                    Quantity = take
                };
                _db.InventoryLotBucketBalances.Add(target);
            }
            else
            {
                target.Quantity = QuantityMath.RoundQuantity(target.Quantity + take);
            }

            remaining = QuantityMath.RoundQuantity(remaining - take);
        }
    }

    public async Task<decimal> RemoveCarryingValueAsync(
        Guid productId,
        decimal baseQuantity,
        decimal? exactCost,
        CancellationToken cancellationToken)
    {
        var quantity = RequirePositiveQuantity(baseQuantity);
        var state = await _inventory.GetCostStateForUpdateAsync(productId, cancellationToken)
            ?? throw new BusinessRuleException(
                "inventory.cost_state_missing",
                "Inventory cost state was not found.");

        if (quantity > state.CostedQty)
        {
            throw new BusinessRuleException(
                "inventory.cost_pool_insufficient",
                "Inventory cost pool does not contain enough quantity.");
        }

        if (exactCost is null)
        {
            var removal = state.CalculateRemovalCost(quantity);
            state.RemoveCarryingValue(quantity);
            return removal;
        }

        if (exactCost.Value < 0)
        {
            throw new BusinessRuleException(
                "inventory.cost_negative",
                "Inventory unit cost cannot be negative.");
        }

        var amount = decimal.Round(
            quantity * exactCost.Value,
            6,
            MidpointRounding.AwayFromZero);

        if (amount > state.TotalInventoryCost + 0.000001m)
        {
            throw new BusinessRuleException(
                "inventory.cost_value_insufficient",
                "Inventory carrying value is insufficient for this removal.");
        }

        state.CostedQty = QuantityMath.RoundQuantity(state.CostedQty - quantity);
        state.TotalInventoryCost = decimal.Round(
            Math.Max(0m, state.TotalInventoryCost - amount),
            6,
            MidpointRounding.AwayFromZero);
        state.MovingAverageCost = state.CostedQty == 0
            ? 0
            : decimal.Round(
                state.TotalInventoryCost / state.CostedQty,
                6,
                MidpointRounding.AwayFromZero);
        state.Version++;

        return amount;
    }

    public Task AddCarryingValueAndLotAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        CancellationToken cancellationToken) =>
        AddCarryingValueAndLotAsync(
            productId,
            baseQuantity,
            unitCost,
            sourceMovementId,
            null,
            cancellationToken);

    public async Task AddCarryingValueAndLotAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken)
    {
        _ = await AddCarryingValueAndLotWithIdAsync(
            productId,
            baseQuantity,
            unitCost,
            sourceMovementId,
            sourcePurchaseItemId,
            cancellationToken);
    }

    public Task<Guid> AddCarryingValueAndLotWithIdAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        CancellationToken cancellationToken) =>
        AddCarryingValueAndLotWithIdAsync(
            productId,
            baseQuantity,
            unitCost,
            sourceMovementId,
            sourcePurchaseItemId,
            InventoryBucket.Sellable,
            cancellationToken);

    public async Task<Guid> AddCarryingValueAndLotWithIdAsync(
        Guid productId,
        decimal baseQuantity,
        decimal unitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        InventoryBucket initialBucket,
        CancellationToken cancellationToken)
    {
        var quantity = RequirePositiveQuantity(baseQuantity);

        if (unitCost < 0)
        {
            throw new BusinessRuleException(
                "inventory.cost_negative",
                "Inventory unit cost cannot be negative.");
        }

        var normalizedCost = decimal.Round(unitCost, 6, MidpointRounding.AwayFromZero);
        var state = await _inventory.GetCostStateForUpdateAsync(productId, cancellationToken);

        if (state is null)
        {
            state = new ProductCostState { ProductId = productId };
            _inventory.AddCostState(state);
        }

        state.CostedQty = QuantityMath.RoundQuantity(state.CostedQty + quantity);
        state.TotalInventoryCost = decimal.Round(
            state.TotalInventoryCost + (quantity * normalizedCost),
            6,
            MidpointRounding.AwayFromZero);
        state.MovingAverageCost = decimal.Round(
            state.TotalInventoryCost / state.CostedQty,
            6,
            MidpointRounding.AwayFromZero);
        state.Version++;

        var lot = new InventoryLot
        {
            ProductId = productId,
            SourceMovementId = sourceMovementId,
            PurchaseItemId = sourcePurchaseItemId,
            ReceivedQuantity = quantity,
            OriginalUnitCost = normalizedCost,
            EffectiveUnitCost = normalizedCost,
            CreatedAt = _clock.UtcNow
        };

        _inventory.AddLot(lot);
        _inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = initialBucket,
            Quantity = quantity
        });

        return lot.Id;
    }

    public Task<Guid> AddZeroCarryingLotAsync(
        Guid productId,
        decimal baseQuantity,
        decimal originalUnitCost,
        Guid sourceMovementId,
        Guid? sourcePurchaseItemId,
        InventoryBucket initialBucket,
        CancellationToken cancellationToken)
    {
        var quantity = RequirePositiveQuantity(baseQuantity);
        if (originalUnitCost < 0)
        {
            throw new BusinessRuleException(
                "inventory.cost_negative",
                "Inventory unit cost cannot be negative.");
        }

        if (initialBucket != InventoryBucket.Scrap)
        {
            throw new BusinessRuleException(
                "inventory.zero_cost_bucket_invalid",
                "Zero-carrying inventory lots are only valid for Scrap.");
        }

        var lot = new InventoryLot
        {
            ProductId = productId,
            SourceMovementId = sourceMovementId,
            PurchaseItemId = sourcePurchaseItemId,
            ReceivedQuantity = quantity,
            OriginalUnitCost = decimal.Round(
                originalUnitCost,
                6,
                MidpointRounding.AwayFromZero),
            EffectiveUnitCost = 0m,
            CreatedAt = _clock.UtcNow
        };

        _inventory.AddLot(lot);
        _inventory.AddLotBucketBalance(new InventoryLotBucketBalance
        {
            LotId = lot.Id,
            StockBucket = initialBucket,
            Quantity = quantity
        });

        return Task.FromResult(lot.Id);
    }

    public async Task<decimal?> GetCurrentUnitCostAsync(
        Guid productId,
        CancellationToken cancellationToken)
    {
        var state = await _db.ProductCostStates
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.ProductId == productId, cancellationToken);

        return state is null || state.CostedQty <= 0
            ? null
            : state.MovingAverageCost;
    }

    public async Task ConsumeBucketAsync(
        Guid productId,
        InventoryBucket bucket,
        decimal baseQuantity,
        Guid movementId,
        decimal unitCostSnapshot,
        CancellationToken cancellationToken)
    {
        if (unitCostSnapshot < 0)
        {
            throw new BusinessRuleException(
                "inventory.cost_negative",
                "Lot-consumption unit cost cannot be negative.");
        }
        var remaining = RequirePositiveQuantity(baseQuantity);
        var positions = await _inventory.GetLotBucketPositionsForUpdateAsync(
            productId,
            bucket,
            cancellationToken);

        EnsureAvailable(positions, remaining, bucket);

        foreach (var position in positions)
        {
            if (remaining == 0)
            {
                break;
            }

            var take = QuantityMath.RoundQuantity(
                Math.Min(position.Balance.Quantity, remaining));
            position.Balance.Quantity = QuantityMath.RoundQuantity(
                position.Balance.Quantity - take);

            _inventory.AddLotConsumption(new InventoryLotConsumption
            {
                LotId = position.Lot.Id,
                MovementId = movementId,
                Quantity = take,
                UnitCostSnapshot = decimal.Round(
                    unitCostSnapshot,
                    6,
                    MidpointRounding.AwayFromZero),
                TotalCostSnapshot = decimal.Round(
                    take * unitCostSnapshot,
                    6,
                    MidpointRounding.AwayFromZero),
                OccurredAt = _clock.UtcNow
            });

            remaining = QuantityMath.RoundQuantity(remaining - take);
        }
    }

    private async Task<InventoryLotBucketBalance?> GetLotBucketForUpdateAsync(
        Guid lotId,
        InventoryBucket bucket,
        CancellationToken cancellationToken)
    {
        var bucketValue = (int)bucket;
        return await _db.InventoryLotBucketBalances
            .FromSqlInterpolated(
                $"SELECT * FROM inventory.lot_bucket_balances WHERE lot_id = {lotId} AND stock_bucket = {bucketValue} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static decimal RequirePositiveQuantity(decimal quantity)
    {
        var normalized = QuantityMath.RoundQuantity(quantity);
        if (normalized <= 0)
        {
            throw new BusinessRuleException(
                "inventory.quantity_positive",
                "Inventory quantity must be greater than zero.");
        }

        return normalized;
    }

    private static void EnsureAvailable(
        IReadOnlyList<LotBucketPosition> positions,
        decimal required,
        InventoryBucket bucket)
    {
        var available = QuantityMath.RoundQuantity(
            positions.Sum(x => x.Balance.Quantity));

        if (available < required)
        {
            throw new BusinessRuleException(
                "inventory.lot_bucket_insufficient",
                $"Insufficient cost-bearing quantity in {bucket}.");
        }
    }
}

