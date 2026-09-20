using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record TransferInventoryConditionCommand(
    Guid ProductId,
    InventoryBucket From,
    InventoryBucket To,
    decimal BaseQuantity,
    Guid ActorId,
    string Reason,
    string? Note = null,
    string ReferenceType = "INVENTORY_CONDITION",
    Guid? ReferenceId = null,
    IReadOnlyCollection<Guid>? InventoryUnitIds = null);

public interface IInventoryConditionService
{
    Task<Result<Guid>> TransferAsync(
        TransferInventoryConditionCommand command,
        CancellationToken cancellationToken);
}

public sealed class InventoryConditionService : IInventoryConditionService
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costAllocator;
    private readonly IClock _clock;

    public InventoryConditionService(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costAllocator,
        IClock clock)
    {
        _catalog = catalog;
        _inventory = inventory;
        _costAllocator = costAllocator;
        _clock = clock;
    }

    public async Task<Result<Guid>> TransferAsync(
        TransferInventoryConditionCommand command,
        CancellationToken cancellationToken)
    {
        if (!IsAllowedTransition(command.From, command.To))
        {
            return Result<Guid>.Failure(
                "inventory.invalid_condition_transition",
                $"Inventory cannot move directly from {command.From} to {command.To}.");
        }

        if (command.BaseQuantity <= 0)
        {
            return Result<Guid>.Failure(
                "inventory.quantity_positive",
                "Inventory quantity must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result<Guid>.Failure(
                "inventory.reason_required",
                "A reason is required for inventory condition changes.");
        }

        var product = await _catalog.GetProductAsync(command.ProductId, cancellationToken);
        if (product is null)
        {
            return Result<Guid>.Failure("catalog.product_not_found", "Product was not found.");
        }

        if (await _inventory.IsProductBlockedByCountingStocktakeAsync(
                command.ProductId,
                cancellationToken))
        {
            return Result<Guid>.Failure(
                "inventory.stocktake_in_progress",
                "Stock-affecting operations are blocked while this product is being counted.");
        }

        var balance = await _inventory.GetStockBalanceForUpdateAsync(
            command.ProductId,
            cancellationToken);

        if (balance is null)
        {
            return Result<Guid>.Failure(
                "inventory.balance_not_found",
                "Inventory balance was not found for the product.");
        }

        var quantity = EdgeRetails.Domain.Common.QuantityMath.RoundQuantity(command.BaseQuantity);
        var beforeFrom = balance.Get(command.From);
        var beforeTo = balance.Get(command.To);

        if (beforeFrom < quantity)
        {
            return Result<Guid>.Failure(
                "inventory.insufficient_bucket_quantity",
                $"Insufficient quantity in {command.From}.");
        }

        var exactUnitsResult = await ValidateAndTransitionSerializedUnitsAsync(
            product,
            command,
            quantity,
            cancellationToken);

        if (!exactUnitsResult.IsSuccess)
        {
            return Result<Guid>.Failure(
                exactUnitsResult.Error!.Code,
                exactUnitsResult.Error.Message);
        }

        balance.Transfer(command.From, command.To, quantity);
        await _costAllocator.TransferBucketAsync(
            product.Id,
            command.From,
            command.To,
            quantity,
            cancellationToken);

        decimal recognizedLoss = 0m;
        if (command.To == InventoryBucket.Scrap)
        {
            decimal? exactCost = null;
            if (product.TrackingMode == TrackingMode.Serialized)
            {
                exactCost = exactUnitsResult.Value!
                    .Sum(x => x.AcquisitionCost);
            }

            recognizedLoss = await _costAllocator.RemoveCarryingValueAsync(
                product.Id,
                quantity,
                exactCost,
                cancellationToken);
        }

        var movement = new InventoryMovement
        {
            ProductId = product.Id,
            MovementType = ResolveMovementType(command.From, command.To),
            ReferenceType = command.ReferenceType,
            ReferenceId = command.ReferenceId,
            RecognizedLossAmount = decimal.Round(
                recognizedLoss,
                2,
                MidpointRounding.AwayFromZero),
            ActorId = command.ActorId,
            OccurredAt = _clock.UtcNow,
            CorrelationId = Guid.CreateVersion7(),
            Reason = command.Reason.Trim(),
            Note = command.Note?.Trim()
        };

        _inventory.AddMovement(movement);
        _inventory.AddMovementEffect(new InventoryMovementEffect
        {
            MovementId = movement.Id,
            StockBucket = command.From,
            QuantityDelta = -quantity,
            QuantityBefore = beforeFrom,
            QuantityAfter = balance.Get(command.From)
        });
        _inventory.AddMovementEffect(new InventoryMovementEffect
        {
            MovementId = movement.Id,
            StockBucket = command.To,
            QuantityDelta = quantity,
            QuantityBefore = beforeTo,
            QuantityAfter = balance.Get(command.To)
        });

        foreach (var unit in exactUnitsResult.Value!)
        {
            _inventory.AddMovementUnit(new InventoryMovementUnit
            {
                MovementId = movement.Id,
                InventoryUnitId = unit.Id,
                FromStatus = BucketStatus(command.From),
                ToStatus = BucketStatus(command.To)
            });
        }

        return Result<Guid>.Success(movement.Id);
    }

    private async Task<Result<IReadOnlyList<InventoryUnit>>> ValidateAndTransitionSerializedUnitsAsync(
        Product product,
        TransferInventoryConditionCommand command,
        decimal quantity,
        CancellationToken cancellationToken)
    {
        if (product.TrackingMode != TrackingMode.Serialized)
        {
            if (command.InventoryUnitIds is { Count: > 0 })
            {
                return Result<IReadOnlyList<InventoryUnit>>.Failure(
                    "inventory.unit_ids_not_allowed",
                    "Exact inventory unit IDs are only valid for serialized products.");
            }

            return Result<IReadOnlyList<InventoryUnit>>.Success(Array.Empty<InventoryUnit>());
        }

        if (!EdgeRetails.Domain.Common.QuantityMath.IsWhole(quantity))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.serialized_quantity_whole",
                "Serialized inventory quantity must be whole.");
        }

        if (command.InventoryUnitIds is null ||
            command.InventoryUnitIds.Count != decimal.ToInt32(quantity))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.serialized_unit_count",
                "Select one exact inventory unit for every serialized quantity.");
        }

        if (command.InventoryUnitIds.Distinct().Count() != command.InventoryUnitIds.Count)
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.serialized_duplicate_unit",
                "The same serialized unit cannot be selected twice.");
        }

        var units = await _inventory.GetInventoryUnitsForUpdateAsync(
            product.Id,
            command.InventoryUnitIds,
            cancellationToken);

        if (units.Count != command.InventoryUnitIds.Count)
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.serialized_unit_not_found",
                "One or more selected serialized units were not found.");
        }

        var expected = BucketStatus(command.From);
        var target = BucketStatus(command.To);

        if (units.Any(x => x.Status != expected))
        {
            return Result<IReadOnlyList<InventoryUnit>>.Failure(
                "inventory.serialized_wrong_status",
                "One or more selected serialized units are not in the expected source state.");
        }

        foreach (var unit in units)
        {
            unit.Status = target;
            unit.Version++;
        }

        return Result<IReadOnlyList<InventoryUnit>>.Success(units);
    }

    private static bool IsAllowedTransition(InventoryBucket from, InventoryBucket to)
    {
        return (from, to) switch
        {
            (InventoryBucket.Sellable, InventoryBucket.Damaged) => true,
            (InventoryBucket.Sellable, InventoryBucket.Defective) => true,
            (InventoryBucket.Damaged, InventoryBucket.Sellable) => true,
            (InventoryBucket.Defective, InventoryBucket.Sellable) => true,
            (InventoryBucket.Damaged, InventoryBucket.WithSupplier) => true,
            (InventoryBucket.Defective, InventoryBucket.WithSupplier) => true,
            (InventoryBucket.WithSupplier, InventoryBucket.Sellable) => true,
            (InventoryBucket.WithSupplier, InventoryBucket.Defective) => true,
            (InventoryBucket.Damaged, InventoryBucket.Scrap) => true,
            (InventoryBucket.Defective, InventoryBucket.Scrap) => true,
            (InventoryBucket.WithSupplier, InventoryBucket.Scrap) => true,
            _ => false
        };
    }

    private static InventoryMovementType ResolveMovementType(
        InventoryBucket from,
        InventoryBucket to)
    {
        return (from, to) switch
        {
            (InventoryBucket.Sellable, InventoryBucket.Damaged) =>
                InventoryMovementType.MarkDamaged,
            (InventoryBucket.Sellable, InventoryBucket.Defective) =>
                InventoryMovementType.MarkDefective,
            (_, InventoryBucket.Sellable) =>
                from == InventoryBucket.WithSupplier
                    ? InventoryMovementType.ReceiveRepairedFromSupplier
                    : InventoryMovementType.RestoreToSellable,
            (_, InventoryBucket.WithSupplier) =>
                InventoryMovementType.SendToSupplierWarranty,
            (_, InventoryBucket.Scrap) =>
                InventoryMovementType.WriteOffToScrap,
            (InventoryBucket.WithSupplier, InventoryBucket.Defective) =>
                InventoryMovementType.WarrantyRejectedReturn,
            _ => InventoryMovementType.StockAdjustment
        };
    }

    private static InventoryUnitStatus BucketStatus(InventoryBucket bucket) => bucket switch
    {
        InventoryBucket.Sellable => InventoryUnitStatus.InStock,
        InventoryBucket.Damaged => InventoryUnitStatus.Damaged,
        InventoryBucket.Defective => InventoryUnitStatus.Defective,
        InventoryBucket.WithSupplier => InventoryUnitStatus.WithSupplier,
        InventoryBucket.Scrap => InventoryUnitStatus.Scrapped,
        _ => throw new ArgumentOutOfRangeException(nameof(bucket))
    };
}

public sealed class TransferInventoryConditionHandler
{
    private readonly IInventoryConditionService _service;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public TransferInventoryConditionHandler(
        IInventoryConditionService service,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _service = service;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<Guid>> HandleAsync(
        TransferInventoryConditionCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var result = await _service.TransferAsync(command, ct);
            if (!result.IsSuccess)
            {
                return result;
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
    }
}
