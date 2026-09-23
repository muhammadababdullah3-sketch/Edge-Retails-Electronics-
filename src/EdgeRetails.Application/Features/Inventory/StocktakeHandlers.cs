using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

public sealed record CreateStocktakeCommand(
    StocktakeScope Scope,
    Guid? CategoryId,
    Guid ActorId,
    string? Note);

public sealed class CreateStocktakeHandler
{
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CreateStocktakeHandler(
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result<Guid>> HandleAsync(
        CreateStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (command.Scope == StocktakeScope.Category && command.CategoryId is null)
            {
                return Result<Guid>.Failure(
                    "inventory.stocktake_category_required",
                    "Category is required for a category stocktake.");
            }

            var existing = await _inventory.GetOpenStocktakeForUpdateAsync(ct);
            if (existing is not null)
            {
                return Result<Guid>.Failure(
                    "inventory.stocktake_already_open",
                    "Finish or cancel the current stocktake before starting another.");
            }

            var stocktake = new Stocktake
            {
                Scope = command.Scope,
                CategoryId = command.CategoryId,
                CreatedBy = command.ActorId,
                CreatedAt = _clock.UtcNow,
                Note = command.Note?.Trim()
            };

            _inventory.AddStocktake(stocktake);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(stocktake.Id);
        }, cancellationToken);
    }
}

public sealed record StartStocktakeCommand(Guid StocktakeId);

public sealed class StartStocktakeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IResourceLock _resourceLock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public StartStocktakeHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IResourceLock resourceLock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _catalog = catalog;
        _inventory = inventory;
        _resourceLock = resourceLock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result> HandleAsync(
        StartStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null)
            {
                return Result.Failure(
                    "inventory.stocktake_not_found",
                    "Stocktake was not found.");
            }

            if (stocktake.Status != StocktakeStatus.Draft)
            {
                return Result.Failure(
                    "inventory.stocktake_not_draft",
                    "Only a draft stocktake can be started.");
            }

            var products = await _catalog.GetActiveProductsAsync(
                stocktake.Scope,
                stocktake.CategoryId,
                ct);

            foreach (var productId in products
                .Select(x => x.Id)
                .OrderBy(x => x))
            {
                await _resourceLock.AcquireAsync("product", productId, ct);
            }

            foreach (var product in products.OrderBy(x => x.Id))
            {
                var lockedProduct = await _catalog.GetProductForUpdateAsync(
                    product.Id,
                    ct);
                if (lockedProduct is null)
                {
                    return Result.Failure(
                        "catalog.product_not_found",
                        "A stocktake product no longer exists.");
                }

                var balance = await _inventory.GetStockBalanceForUpdateAsync(product.Id, ct);
                _inventory.AddStocktakeItem(new StocktakeItem
                {
                    StocktakeId = stocktake.Id,
                    ProductId = product.Id,
                    ExpectedSellableQty = balance?.SellableQty ?? 0m
                });
            }

            stocktake.Start(_clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record RecordStocktakeCountCommand(
    Guid StocktakeId,
    Guid ProductId,
    decimal CountedSellableQty,
    Guid ActorId,
    string? ReviewNote);

public sealed class RecordStocktakeCountHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RecordStocktakeCountHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _catalog = catalog;
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result> HandleAsync(
        RecordStocktakeCountCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (command.CountedSellableQty < 0)
            {
                return Result.Failure(
                    "inventory.stocktake_count_negative",
                    "Physical count cannot be negative.");
            }

            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null || stocktake.Status != StocktakeStatus.Counting)
            {
                return Result.Failure(
                    "inventory.stocktake_not_counting",
                    "Stocktake is not in the counting state.");
            }

            var product = await _catalog.GetProductAsync(command.ProductId, ct);
            if (product is null)
            {
                return Result.Failure(
                    "catalog.product_not_found",
                    "Product was not found.");
            }

            if (product.TrackingMode == TrackingMode.Serialized)
            {
                return Result.Failure(
                    "inventory.serialized_stocktake_scan_required",
                    "Serialized products must be counted by exact unit identity.");
            }

            var item = await _inventory.GetStocktakeItemForUpdateAsync(
                stocktake.Id,
                product.Id,
                ct);

            if (item is null)
            {
                return Result.Failure(
                    "inventory.stocktake_product_out_of_scope",
                    "Product is not part of this stocktake.");
            }

            item.CountedSellableQty = QuantityMath.RoundQuantity(command.CountedSellableQty);
            item.CountedBy = command.ActorId;
            item.CountedAt = _clock.UtcNow;
            item.ReviewNote = command.ReviewNote?.Trim();

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record RecordSerializedStocktakeCommand(
    Guid StocktakeId,
    Guid ProductId,
    IReadOnlyCollection<Guid> FoundInventoryUnitIds,
    IReadOnlyCollection<string> UnexpectedIdentitySnapshots,
    Guid ActorId,
    string? ReviewNote);

public sealed class RecordSerializedStocktakeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public RecordSerializedStocktakeHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _catalog = catalog;
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result> HandleAsync(
        RecordSerializedStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null || stocktake.Status != StocktakeStatus.Counting)
            {
                return Result.Failure(
                    "inventory.stocktake_not_counting",
                    "Stocktake is not in the counting state.");
            }

            var product = await _catalog.GetProductAsync(command.ProductId, ct);
            if (product is null || product.TrackingMode != TrackingMode.Serialized)
            {
                return Result.Failure(
                    "inventory.product_not_serialized",
                    "Product is not configured for serialized tracking.");
            }

            var item = await _inventory.GetStocktakeItemForUpdateAsync(
                stocktake.Id,
                product.Id,
                ct);

            if (item is null)
            {
                return Result.Failure(
                    "inventory.stocktake_product_out_of_scope",
                    "Product is not part of this stocktake.");
            }

            var oldChecks = await _inventory.GetStocktakeUnitChecksAsync(item.Id, ct);
            foreach (var oldCheck in oldChecks)
            {
                _inventory.RemoveStocktakeUnitCheck(oldCheck);
            }

            var foundIds = command.FoundInventoryUnitIds.Distinct().ToArray();
            if (foundIds.Length != command.FoundInventoryUnitIds.Count)
            {
                return Result.Failure(
                    "inventory.stocktake_duplicate_scan",
                    "The same serialized unit was scanned more than once.");
            }

            var expected = await _inventory.GetSellableInventoryUnitsAsync(product.Id, ct);
            var expectedById = expected.ToDictionary(x => x.Id);
            var foundKnown = foundIds.Length == 0
                ? Array.Empty<InventoryUnit>()
                : (await _inventory.GetInventoryUnitsForUpdateAsync(product.Id, foundIds, ct))
                    .ToArray();
            var foundById = foundKnown.ToDictionary(x => x.Id);

            foreach (var unit in expected)
            {
                var wasFound = foundById.ContainsKey(unit.Id);
                _inventory.AddStocktakeUnitCheck(new StocktakeUnitCheck
                {
                    StocktakeItemId = item.Id,
                    InventoryUnitId = unit.Id,
                    IdentitySnapshot = BestIdentity(unit),
                    Result = wasFound
                        ? StocktakeUnitCheckResult.ExpectedAndFound
                        : StocktakeUnitCheckResult.Missing
                });
            }

            foreach (var unit in foundKnown.Where(x => !expectedById.ContainsKey(x.Id)))
            {
                _inventory.AddStocktakeUnitCheck(new StocktakeUnitCheck
                {
                    StocktakeItemId = item.Id,
                    InventoryUnitId = unit.Id,
                    IdentitySnapshot = BestIdentity(unit),
                    Result = StocktakeUnitCheckResult.WrongStatus,
                    Note = $"Current status: {unit.Status}"
                });
            }

            var unknownFoundIds = foundIds.Where(id => !foundById.ContainsKey(id));
            foreach (var unknownId in unknownFoundIds)
            {
                _inventory.AddStocktakeUnitCheck(new StocktakeUnitCheck
                {
                    StocktakeItemId = item.Id,
                    InventoryUnitId = null,
                    IdentitySnapshot = unknownId.ToString(),
                    Result = StocktakeUnitCheckResult.Unexpected,
                    Note = "Scanned unit ID does not exist for this product."
                });
            }

            foreach (var identity in command.UnexpectedIdentitySnapshots
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Select(x => x.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _inventory.AddStocktakeUnitCheck(new StocktakeUnitCheck
                {
                    StocktakeItemId = item.Id,
                    InventoryUnitId = null,
                    IdentitySnapshot = identity,
                    Result = StocktakeUnitCheckResult.Unexpected
                });
            }

            item.CountedSellableQty = foundKnown.Count(x => x.Status == InventoryUnitStatus.InStock);
            item.CountedBy = command.ActorId;
            item.CountedAt = _clock.UtcNow;
            item.ReviewNote = command.ReviewNote?.Trim();

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private static string BestIdentity(InventoryUnit unit) =>
        unit.SerialNumber ?? unit.Imei1 ?? unit.Id.ToString();
}

public sealed record ReviewStocktakeCommand(Guid StocktakeId);

public sealed class ReviewStocktakeHandler
{
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public ReviewStocktakeHandler(
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result> HandleAsync(
        ReviewStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null || stocktake.Status != StocktakeStatus.Counting)
            {
                return Result.Failure(
                    "inventory.stocktake_not_counting",
                    "Stocktake is not in the counting state.");
            }

            var items = await _inventory.GetStocktakeItemsAsync(stocktake.Id, ct);
            if (items.Any(x => x.CountedSellableQty is null))
            {
                return Result.Failure(
                    "inventory.stocktake_incomplete",
                    "Every product in the stocktake must be counted before review.");
            }

            stocktake.MoveToReview(_clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record PostStocktakeCommand(
    Guid StocktakeId,
    Guid ActorId,
    IReadOnlyDictionary<Guid, decimal>? PositiveVarianceUnitCosts);

public sealed class PostStocktakeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costAllocator;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public PostStocktakeHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costAllocator,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _catalog = catalog;
        _inventory = inventory;
        _costAllocator = costAllocator;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result> HandleAsync(
        PostStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null || stocktake.Status != StocktakeStatus.Review)
            {
                return Result.Failure(
                    "inventory.stocktake_not_in_review",
                    "Stocktake must be in review before posting.");
            }

            var items = await _inventory.GetStocktakeItemsAsync(stocktake.Id, ct);
            if (items.Any(x => x.CountedSellableQty is null))
            {
                return Result.Failure(
                    "inventory.stocktake_incomplete",
                    "Every product must have a physical count.");
            }

            foreach (var item in items)
            {
                var product = await _catalog.GetProductAsync(item.ProductId, ct);
                if (product is null)
                {
                    return Result.Failure(
                        "catalog.product_not_found",
                        "A stocktake product no longer exists.");
                }

                var balance = await _inventory.GetStockBalanceForUpdateAsync(product.Id, ct);
                if (balance is null)
                {
                    if (item.ExpectedSellableQty != 0)
                    {
                        return Result.Failure(
                            "inventory.stocktake_balance_missing",
                            "Inventory balance changed during the stocktake.");
                    }

                    balance = new StockBalance { ProductId = product.Id };
                    _inventory.AddStockBalance(balance);
                }

                if (balance.SellableQty != item.ExpectedSellableQty)
                {
                    return Result.Failure(
                        "inventory.stocktake_concurrency_conflict",
                        "Inventory changed after the stocktake snapshot. Review the stocktake again.");
                }

                var unitChecks = await _inventory.GetStocktakeUnitChecksAsync(item.Id, ct);
                if (unitChecks.Any(x =>
                        x.Result is StocktakeUnitCheckResult.Unexpected or
                            StocktakeUnitCheckResult.WrongStatus))
                {
                    return Result.Failure(
                        "inventory.stocktake_serialized_unresolved",
                        "Unexpected or wrong-status serialized units must be resolved before posting.");
                }

                var variance = item.VarianceQty;
                if (variance == 0)
                {
                    continue;
                }

                var before = balance.SellableQty;
                var movement = new InventoryMovement
                {
                    ProductId = product.Id,
                    MovementType = InventoryMovementType.PhysicalCountCorrection,
                    ReferenceType = "STOCKTAKE",
                    ReferenceId = stocktake.Id,
                    ActorId = command.ActorId,
                    OccurredAt = _clock.UtcNow,
                    CorrelationId = stocktake.Id,
                    Reason = "PHYSICAL_COUNT",
                    Note = item.ReviewNote
                };
                _inventory.AddMovement(movement);

                if (variance > 0)
                {
                    if (product.TrackingMode == TrackingMode.Serialized)
                    {
                        return Result.Failure(
                            "inventory.stocktake_serialized_positive_requires_adjustment",
                            "Unexpected serialized stock must be added through an explicit positive serialized adjustment.");
                    }

                    var suppliedCost = command.PositiveVarianceUnitCosts is not null &&
                                       command.PositiveVarianceUnitCosts.TryGetValue(product.Id, out var explicitCost)
                        ? explicitCost
                        : (decimal?)null;

                    var unitCost = suppliedCost ??
                                   product.ReferencePurchaseCost ??
                                   await _costAllocator.GetCurrentUnitCostAsync(product.Id, ct);

                    if (unitCost is null || unitCost <= 0)
                    {
                        return Result.Failure(
                            "inventory.stocktake_positive_cost_required",
                            "Positive stock variance requires a valid unit cost basis.");
                    }

                    balance.ApplyDelta(InventoryBucket.Sellable, variance);
                    movement.UnitCostSnapshot = decimal.Round(
                        unitCost.Value,
                        6,
                        MidpointRounding.AwayFromZero);
                    await _costAllocator.AddCarryingValueAndLotAsync(
                        product.Id,
                        variance,
                        movement.UnitCostSnapshot.Value,
                        movement.Id,
                        ct);
                }
                else
                {
                    var removalQty = Math.Abs(variance);
                    decimal loss = 0m;

                    if (product.TrackingMode == TrackingMode.Serialized)
                    {
                        var missingChecks = unitChecks
                            .Where(x => x.Result == StocktakeUnitCheckResult.Missing &&
                                        x.InventoryUnitId is not null)
                            .ToArray();

                        if (missingChecks.Length != decimal.ToInt32(removalQty))
                        {
                            return Result.Failure(
                                "inventory.stocktake_serialized_missing_mismatch",
                                "Serialized missing-unit count does not match the quantity variance.");
                        }

                        var missingIds = missingChecks
                            .Select(x => x.InventoryUnitId!.Value)
                            .ToArray();

                        var missingUnits = await _inventory.GetInventoryUnitsForUpdateAsync(
                            product.Id,
                            missingIds,
                            ct);

                        if (missingUnits.Count != missingIds.Length ||
                            missingUnits.Any(x =>
                                x.Status != InventoryUnitStatus.InStock ||
                                x.InventoryLotId is null))
                        {
                            return Result.Failure(
                                "inventory.stocktake_serialized_state_changed",
                                "A serialized unit changed state or lost its lot origin before posting.");
                        }

                        foreach (var unit in missingUnits.OrderBy(x => x.Id))
                        {
                            var lotBalance = await _inventory.GetLotBucketBalanceForUpdateAsync(
                                unit.InventoryLotId!.Value,
                                InventoryBucket.Sellable,
                                ct);
                            if (lotBalance is null || lotBalance.Quantity < 1m)
                            {
                                return Result.Failure(
                                    "inventory.stocktake_serialized_lot_changed",
                                    "A serialized unit lot is no longer available for write-off.");
                            }

                            lotBalance.Quantity = QuantityMath.RoundQuantity(
                                lotBalance.Quantity - 1m);

                            _inventory.AddLotConsumption(new InventoryLotConsumption
                            {
                                LotId = unit.InventoryLotId.Value,
                                MovementId = movement.Id,
                                Quantity = 1m,
                                UnitCostSnapshot = unit.AcquisitionCost,
                                TotalCostSnapshot = unit.AcquisitionCost,
                                OccurredAt = _clock.UtcNow
                            });

                            loss += await _costAllocator.RemoveCarryingValueAsync(
                                product.Id,
                                1m,
                                unit.AcquisitionCost,
                                ct);

                            unit.Status = InventoryUnitStatus.Scrapped;
                            unit.Version++;
                            _inventory.AddMovementUnit(new InventoryMovementUnit
                            {
                                MovementId = movement.Id,
                                InventoryUnitId = unit.Id,
                                FromStatus = InventoryUnitStatus.InStock,
                                ToStatus = InventoryUnitStatus.Scrapped
                            });
                        }
                    }
                    else
                    {
                        loss = await _costAllocator.RemoveCarryingValueAsync(
                            product.Id,
                            removalQty,
                            null,
                            ct);
                        var stocktakeUnitCost = decimal.Round(
                            loss / removalQty,
                            6,
                            MidpointRounding.AwayFromZero);
                        await _costAllocator.ConsumeBucketAsync(
                            product.Id,
                            InventoryBucket.Sellable,
                            removalQty,
                            movement.Id,
                            stocktakeUnitCost,
                            ct);
                    }

                    balance.ApplyDelta(InventoryBucket.Sellable, -removalQty);
                    movement.RecognizedLossAmount = decimal.Round(
                        loss,
                        2,
                        MidpointRounding.AwayFromZero);
                }

                _inventory.AddMovementEffect(new InventoryMovementEffect
                {
                    MovementId = movement.Id,
                    StockBucket = InventoryBucket.Sellable,
                    QuantityDelta = variance,
                    QuantityBefore = before,
                    QuantityAfter = balance.SellableQty
                });
            }

            stocktake.MarkPosted(_clock.UtcNow);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record CancelStocktakeCommand(Guid StocktakeId);

public sealed class CancelStocktakeHandler
{
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public CancelStocktakeHandler(
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public Task<Result> HandleAsync(
        CancelStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null)
            {
                return Result.Failure(
                    "inventory.stocktake_not_found",
                    "Stocktake was not found.");
            }

            try
            {
                stocktake.Cancel(_clock.UtcNow);
            }
            catch (BusinessRuleException ex)
            {
                return Result.Failure(ex.Code, ex.Message);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}
