using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Inventory;

namespace EdgeRetails.Application.Features.Inventory;

internal static class StocktakeOperationOutcome
{
    public static async Task<Result?> BeginAsync(
        Guid operationId,
        string operationType,
        Guid actorId,
        string payload,
        IOperationLock? operationLock,
        IOperationOutcomeLedger? ledger,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty || ledger is null)
            return null;
        if (operationLock is not null)
            await operationLock.AcquireAsync(operationId, cancellationToken);

        var existing = await ledger.GetOutcomeAsync(operationId, cancellationToken);
        if (existing is null)
            return null;

        var fingerprint = Fingerprint(operationType, payload);
        if (existing.State == OperationOutcomeState.Succeeded &&
            existing.OperationType == operationType &&
            string.Equals(existing.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
            return Result.Success();

        return Result.Failure(
            "inventory.stocktake_operation_conflict",
            "This stocktake operation id is already associated with another or unresolved operation.");
    }

    public static Task CompleteAsync(
        Guid operationId,
        string operationType,
        Guid stocktakeId,
        Guid actorId,
        string payload,
        IOperationOutcomeLedger? ledger,
        CancellationToken cancellationToken) =>
        operationId == Guid.Empty || ledger is null
            ? Task.CompletedTask
            : ledger.RecordSuccessAsync(
                operationId,
                operationType,
                stocktakeId,
                stocktakeId.ToString("D"),
                actorId: actorId,
                payloadFingerprint: Fingerprint(operationType, payload),
                cancellationToken: cancellationToken);

    public static string Fingerprint(string operationType, string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{operationType}|{payload}")));
}

public sealed record CreateStocktakeCommand(
    StocktakeScope Scope,
    Guid? CategoryId,
    Guid ActorId,
    string? Note,
    Guid ClientOperationId = default);

public sealed class CreateStocktakeHandler
{
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IApplicationPermissionAuthorizer? _authorizer;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public CreateStocktakeHandler(
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IApplicationPermissionAuthorizer? authorizer = null,
        IOperationOutcomeLedger? outcomeLedger = null,
        IOperationLock? operationLock = null)
    {
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _authorizer = authorizer;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public Task<Result<Guid>> HandleAsync(
        CreateStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            if (_authorizer is not null && command.ActorId != Guid.Empty)
            {
                var auth = await _authorizer.AuthorizeAsync(
                    command.ActorId,
                    PermissionKeys.InventoryManage,
                    ct);
                if (!auth.IsSuccess)
                {
                    return Result<Guid>.Failure(auth.Error!.Code, auth.Error.Message);
                }
            }

            if (command.Scope == StocktakeScope.Category && command.CategoryId is null)
            {
                return Result<Guid>.Failure(
                    "inventory.stocktake_category_required",
                    "Category is required for a category stocktake.");
            }

            var payload = $"{command.Scope}|{command.CategoryId:D}|{command.Note?.Trim()}";
            var payloadFingerprint = StocktakeOperationOutcome.Fingerprint("CreateStocktake", payload);
            if (command.ClientOperationId != Guid.Empty && _outcomeLedger is not null)
            {
                if (_operationLock is not null)
                    await _operationLock.AcquireAsync(command.ClientOperationId, ct);

                var existingOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (existingOutcome is not null)
                {
                    if (existingOutcome.State == OperationOutcomeState.Succeeded &&
                        existingOutcome.OperationType == "CreateStocktake" &&
                        string.Equals(existingOutcome.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal) &&
                        existingOutcome.EntityId.HasValue)
                    {
                        return Result<Guid>.Success(existingOutcome.EntityId.Value);
                    }

                    return Result<Guid>.Failure(
                        "inventory.stocktake_operation_conflict",
                        "This stocktake operation id is already associated with another or unresolved operation.");
                }
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

            if (_outcomeLedger is not null && command.ClientOperationId != Guid.Empty)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "CreateStocktake",
                    stocktake.Id,
                    stocktake.Id.ToString("D"),
                    actorId: command.ActorId,
                    payloadFingerprint: payloadFingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<Guid>.Success(stocktake.Id);
        }, cancellationToken);
    }
}

public sealed record StartStocktakeCommand(Guid StocktakeId, Guid ActorId = default, Guid ClientOperationId = default);

public sealed class StartStocktakeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IResourceLock _resourceLock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public StartStocktakeHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IResourceLock resourceLock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _catalog = catalog;
        _inventory = inventory;
        _resourceLock = resourceLock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public Task<Result> HandleAsync(
        StartStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var payload = command.StocktakeId.ToString("D");
            var replay = await StocktakeOperationOutcome.BeginAsync(command.ClientOperationId,
                "Stocktake.Start", command.ActorId, payload, _operationLock, _outcomeLedger, ct);
            if (replay is { } completedReplay)
                return completedReplay;

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
            await StocktakeOperationOutcome.CompleteAsync(command.ClientOperationId,
                "Stocktake.Start", stocktake.Id, command.ActorId, payload, _outcomeLedger, ct);
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
    string? ReviewNote,
    Guid ClientOperationId = default);

public sealed class RecordStocktakeCountHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public RecordStocktakeCountHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _catalog = catalog;
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public Task<Result> HandleAsync(
        RecordStocktakeCountCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var payload = $"{command.StocktakeId:D}|{command.ProductId:D}|{command.CountedSellableQty}|{command.ReviewNote?.Trim()}";
            var replay = await StocktakeOperationOutcome.BeginAsync(command.ClientOperationId,
                "Stocktake.Count", command.ActorId, payload, _operationLock, _outcomeLedger, ct);
            if (replay is { } completedReplay)
                return completedReplay;

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

            if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
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

            await StocktakeOperationOutcome.CompleteAsync(command.ClientOperationId,
                "Stocktake.Count", stocktake.Id, command.ActorId, payload, _outcomeLedger, ct);
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
    string? ReviewNote,
    Guid ClientOperationId = default);

public sealed class RecordSerializedStocktakeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public RecordSerializedStocktakeHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _catalog = catalog;
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public Task<Result> HandleAsync(
        RecordSerializedStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var payload = $"{command.StocktakeId:D}|{command.ProductId:D}|{string.Join(',', command.FoundInventoryUnitIds.Order())}|{string.Join('|', command.UnexpectedIdentitySnapshots.Order(StringComparer.Ordinal))}|{command.ReviewNote?.Trim()}";
            var replay = await StocktakeOperationOutcome.BeginAsync(command.ClientOperationId,
                "Stocktake.SerializedCount", command.ActorId, payload, _operationLock, _outcomeLedger, ct);
            if (replay is { } completedReplay)
                return completedReplay;

            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null || stocktake.Status != StocktakeStatus.Counting)
            {
                return Result.Failure(
                    "inventory.stocktake_not_counting",
                    "Stocktake is not in the counting state.");
            }

            var product = await _catalog.GetProductAsync(command.ProductId, ct);
            if (product is null || product.TrackingMode is not (TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container))
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
            if (product.TrackingMode == TrackingMode.Container)
            {
                item.CountedSellableQty = 0m;
                foreach (var unit in foundKnown.Where(x => x.Status == InventoryUnitStatus.InStock))
                    item.CountedSellableQty += await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, ct);
            }
            item.CountedBy = command.ActorId;
            item.CountedAt = _clock.UtcNow;
            item.ReviewNote = command.ReviewNote?.Trim();

            await StocktakeOperationOutcome.CompleteAsync(command.ClientOperationId,
                "Stocktake.SerializedCount", stocktake.Id, command.ActorId, payload, _outcomeLedger, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }

    private static string BestIdentity(InventoryUnit unit) =>
        unit.TrackingCode ?? unit.SerialNumber ?? unit.Imei1 ?? unit.Imei2 ?? unit.Id.ToString();
}

public sealed record ReviewStocktakeCommand(Guid StocktakeId, Guid ActorId = default, Guid ClientOperationId = default);

public sealed class ReviewStocktakeHandler
{
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public ReviewStocktakeHandler(
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public Task<Result> HandleAsync(
        ReviewStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var payload = command.StocktakeId.ToString("D");
            var replay = await StocktakeOperationOutcome.BeginAsync(command.ClientOperationId,
                "Stocktake.Review", command.ActorId, payload, _operationLock, _outcomeLedger, ct);
            if (replay is { } completedReplay)
                return completedReplay;

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
            await StocktakeOperationOutcome.CompleteAsync(command.ClientOperationId,
                "Stocktake.Review", stocktake.Id, command.ActorId, payload, _outcomeLedger, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}

public sealed record PostStocktakeCommand(
    Guid StocktakeId,
    Guid ActorId,
    IReadOnlyDictionary<Guid, decimal>? PositiveVarianceUnitCosts = null,
    Guid ClientOperationId = default);

public static class StocktakeFingerprintHelper
{
    public static string Compute(
        Stocktake stocktake,
        IReadOnlyList<StocktakeItem> items,
        IReadOnlyDictionary<Guid, IReadOnlyList<StocktakeUnitCheck>> unitChecksByItemId,
        IReadOnlyDictionary<Guid, decimal>? positiveVarianceUnitCosts)
    {
        var sb = new StringBuilder();
        sb.Append(stocktake.Id.ToString("D")).Append('|');
        sb.Append(stocktake.Scope.ToString()).Append('|');
        if (stocktake.CategoryId.HasValue)
        {
            sb.Append(stocktake.CategoryId.Value.ToString("D")).Append('|');
        }

        foreach (var item in items.OrderBy(x => x.ProductId))
        {
            sb.Append(item.ProductId.ToString("D")).Append(':');
            sb.Append(item.ExpectedSellableQty.ToString("0.######", CultureInfo.InvariantCulture)).Append(':');
            sb.Append((item.CountedSellableQty ?? 0m).ToString("0.######", CultureInfo.InvariantCulture)).Append(':');

            if (unitChecksByItemId.TryGetValue(item.Id, out var checks) && checks.Count > 0)
            {
                var checkTokens = checks
                    .OrderBy(c => c.InventoryUnitId ?? Guid.Empty)
                    .ThenBy(c => c.IdentitySnapshot, StringComparer.OrdinalIgnoreCase)
                    .Select(c => $"{c.InventoryUnitId?.ToString("D") ?? "none"}_{(int)c.Result}_{c.IdentitySnapshot.Trim()}");
                sb.Append(string.Join(",", checkTokens));
            }

            sb.Append(';');
        }

        if (positiveVarianceUnitCosts is { Count: > 0 })
        {
            sb.Append('|');
            var costTokens = positiveVarianceUnitCosts
                .OrderBy(kv => kv.Key)
                .Select(kv => $"{kv.Key:D}:{kv.Value.ToString("0.######", CultureInfo.InvariantCulture)}");
            sb.Append(string.Join(",", costTokens));
        }

        return OperationPayloadFingerprint.ComputeSha256(sb.ToString());
    }
}

public sealed class PostStocktakeHandler
{
    private readonly ICatalogRepository _catalog;
    private readonly IInventoryRepository _inventory;
    private readonly IInventoryCostAllocator _costAllocator;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public PostStocktakeHandler(
        ICatalogRepository catalog,
        IInventoryRepository inventory,
        IInventoryCostAllocator costAllocator,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _catalog = catalog;
        _inventory = inventory;
        _costAllocator = costAllocator;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public async Task<Result> HandleAsync(
        PostStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return Result.Failure(
                "inventory.client_operation_id_required",
                "Client operation id is required.");
        }

        string? payloadFingerprint = null;

        var result = await _transactions.ExecuteAsync(async ct =>
        {
            if (_operationLock is not null)
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            }

            var stocktake = await _inventory.GetStocktakeForUpdateAsync(command.StocktakeId, ct);
            if (stocktake is null)
            {
                return Result.Failure(
                    "inventory.stocktake_not_found",
                    "Stocktake was not found.");
            }

            var items = await _inventory.GetStocktakeItemsAsync(stocktake.Id, ct);
            var unitChecksByItemId = new Dictionary<Guid, IReadOnlyList<StocktakeUnitCheck>>();
            foreach (var item in items)
            {
                var checks = await _inventory.GetStocktakeUnitChecksAsync(item.Id, ct);
                unitChecksByItemId[item.Id] = checks;
            }

            payloadFingerprint = StocktakeFingerprintHelper.Compute(
                stocktake,
                items,
                unitChecksByItemId,
                command.PositiveVarianceUnitCosts);

            if (_outcomeLedger is not null)
            {
                var existingOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (existingOutcome is not null)
                {
                    if (!string.IsNullOrEmpty(existingOutcome.PayloadFingerprint) &&
                        !string.Equals(existingOutcome.PayloadFingerprint, payloadFingerprint, StringComparison.Ordinal))
                    {
                        return Result.Failure(
                            "idempotency.payload_mismatch",
                            "Operation was previously submitted with a different payload.");
                    }

                    if (existingOutcome.State == OperationOutcomeState.Succeeded)
                    {
                        return Result.Success();
                    }

                    if (existingOutcome.State == OperationOutcomeState.Failed)
                    {
                        return Result.Failure(
                            existingOutcome.ErrorCode ?? "inventory.stocktake_failed",
                            existingOutcome.ErrorMessage ?? "Stocktake reconciliation previously failed.");
                    }
                }
            }
            else
            {
                var existingMovement = await _inventory.GetMovementByCorrelationIdAsync(command.ClientOperationId, ct);
                if (existingMovement is not null)
                {
                    return Result.Success();
                }
            }

            if (stocktake.Status != StocktakeStatus.Review)
            {
                return Result.Failure(
                    "inventory.stocktake_not_in_review",
                    "Stocktake must be in review before posting.");
            }

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

                var unitChecks = unitChecksByItemId.TryGetValue(item.Id, out var existingChecks)
                    ? existingChecks
                    : await _inventory.GetStocktakeUnitChecksAsync(item.Id, ct);

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

                if (variance < 0 &&
                    product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
                {
                    var removalQty = Math.Abs(variance);
                    var missingChecks = unitChecks
                        .Where(x => x.Result == StocktakeUnitCheckResult.Missing)
                        .ToArray();

                    if (missingChecks.Any(x => x.InventoryUnitId is null) ||
                        missingChecks.Select(x => x.InventoryUnitId).Distinct().Count() != missingChecks.Length ||
                        (product.TrackingMode != TrackingMode.Container && missingChecks.Length != removalQty))
                    {
                        return Result.Failure(
                            "inventory.stocktake_serialized_missing_mismatch",
                            "Distinct missing physical units must match the quantity variance.");
                    }

                    var missingIds = missingChecks.Select(x => x.InventoryUnitId!.Value).ToArray();
                    var missingUnits = await _inventory.GetInventoryUnitsForUpdateAsync(product.Id, missingIds, ct);
                    if (missingUnits.Count != missingIds.Length ||
                        missingUnits.Any(x => x.Status != InventoryUnitStatus.InStock || x.InventoryLotId is null))
                    {
                        return Result.Failure(
                            "inventory.stocktake_serialized_state_changed",
                            "A serialized unit changed state or lost its lot origin before posting.");
                    }

                    var baseQuantities = new Dictionary<Guid, decimal>();
                    foreach (var unit in missingUnits)
                        baseQuantities[unit.Id] = await _inventory.GetPhysicalUnitBaseQuantitySnapshotAsync(unit, ct);
                    if (baseQuantities.Values.Any(x => x <= 0m) || baseQuantities.Values.Sum() != removalQty)
                    {
                        return Result.Failure(
                            "inventory.stocktake_serialized_missing_mismatch",
                            "Missing physical units do not match the quantity variance.");
                    }

                    try
                    {
                        await ExactMissingSourcePosting.PostAsync(
                            _inventory, _costAllocator, product.Id, missingUnits, baseQuantities, balance,
                            InventoryBucket.Sellable, InventoryMovementType.PhysicalCountCorrection,
                            "STOCKTAKE", stocktake.Id, command.ActorId, command.ClientOperationId,
                            _clock.UtcNow, "PHYSICAL_COUNT", item.ReviewNote, ct);
                    }
                    catch (BusinessRuleException ex)
                    {
                        return Result.Failure(ex.Code, ex.Message);
                    }

                    // The exact-unit source movements already carry the real removal
                    // effects and allocated loss. Do not add an aggregate duplicate.
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
                    CorrelationId = command.ClientOperationId,
                    Reason = "PHYSICAL_COUNT",
                    Note = item.ReviewNote
                };
                _inventory.AddMovement(movement);

                if (variance > 0)
                {
                    if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container)
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

                    if (unitCost is null || unitCost < 0m || (unitCost == 0m && suppliedCost is null))
                    {
                        return Result.Failure(
                            "inventory.stocktake_positive_cost_required",
                            "Positive stock variance requires a valid cost basis; free stock must provide zero explicitly.");
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
                    var loss = await _costAllocator.RemoveCarryingValueAsync(
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

            if (_outcomeLedger is not null)
            {
                await _outcomeLedger.RecordSuccessAsync(
                    command.ClientOperationId,
                    "Stocktake",
                    stocktake.Id,
                    stocktake.Id.ToString("D"),
                    actorId: command.ActorId,
                    payloadFingerprint: payloadFingerprint,
                    cancellationToken: ct);
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);

        if (!result.IsSuccess && _outcomeLedger is not null && command.ClientOperationId != Guid.Empty)
        {
            await _outcomeLedger.RecordFailureAsync(
                command.ClientOperationId,
                "Stocktake",
                result.Error?.Code ?? "inventory.stocktake_failed",
                result.Error?.Message ?? "Stocktake reconciliation failed.",
                actorId: command.ActorId,
                payloadFingerprint: payloadFingerprint,
                cancellationToken: cancellationToken);
        }

        return result;
    }
}

public sealed record CancelStocktakeCommand(Guid StocktakeId, Guid ActorId = default, Guid ClientOperationId = default);

public sealed class CancelStocktakeHandler
{
    private readonly IInventoryRepository _inventory;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public CancelStocktakeHandler(
        IInventoryRepository inventory,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IClock clock,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _inventory = inventory;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
    }

    public Task<Result> HandleAsync(
        CancelStocktakeCommand command,
        CancellationToken cancellationToken)
    {
        return _transactions.ExecuteAsync(async ct =>
        {
            var payload = command.StocktakeId.ToString("D");
            var replay = await StocktakeOperationOutcome.BeginAsync(command.ClientOperationId,
                "Stocktake.Cancel", command.ActorId, payload, _operationLock, _outcomeLedger, ct);
            if (replay is { } completedReplay)
                return completedReplay;

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

            await StocktakeOperationOutcome.CompleteAsync(command.ClientOperationId,
                "Stocktake.Cancel", stocktake.Id, command.ActorId, payload, _outcomeLedger, ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
    }
}
