using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Sales;

namespace EdgeRetails.Application.Features.Sales;

public sealed record SavePosDraftItemInput(
    Guid ProductId,
    Guid ProductUnitId,
    decimal EnteredQuantity,
    Guid? SelectedInventoryUnitId = null,
    string? Note = null);

public sealed record SavePosDraftCommand(
    Guid? DraftId,
    long? ExpectedVersion,
    Guid? CustomerId,
    Guid ActorId,
    string? TerminalId,
    string? Note,
    IReadOnlyList<SavePosDraftItemInput> Items);

public sealed record SavePosDraftResult(
    Guid DraftId,
    string DraftNumber,
    long Version,
    bool Created);

public sealed class SavePosDraftHandler
{
    private readonly IPosDraftRepository _drafts;
    private readonly ICatalogRepository _catalog;
    private readonly IDocumentNumberService _numbers;
    private readonly IClock _clock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public SavePosDraftHandler(
        IPosDraftRepository drafts,
        ICatalogRepository catalog,
        IDocumentNumberService numbers,
        IClock clock,
        IApplicationPermissionAuthorizer authorization,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _drafts = drafts;
        _catalog = catalog;
        _numbers = numbers;
        _clock = clock;
        _authorization = authorization;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<SavePosDraftResult>> HandleAsync(
        SavePosDraftCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ActorId == Guid.Empty || command.Items.Count == 0)
        {
            return Task.FromResult(Result<SavePosDraftResult>.Failure(
                "sales.draft_invalid",
                "Draft requires an actor and at least one item."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var permission = command.DraftId is null
                ? PermissionKeys.SalesDraftCreate
                : PermissionKeys.SalesDraftResume;
            var authorization = await _authorization.AuthorizeAsync(command.ActorId, permission, ct);
            if (!authorization.IsSuccess)
            {
                return Result<SavePosDraftResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            PosDraft draft;
            var created = command.DraftId is null;
            if (created)
            {
                draft = new PosDraft
                {
                    DraftNumber = await _numbers.NextAsync("PD", ct),
                    CreatedBy = command.ActorId,
                    CreatedAt = _clock.UtcNow,
                    UpdatedAt = _clock.UtcNow
                };
                _drafts.AddDraft(draft);
            }
            else
            {
                draft = await _drafts.GetForUpdateAsync(command.DraftId!.Value, ct)
                    ?? throw new InvalidOperationException("POS draft was not found.");

                if (draft.Status != PosDraftStatus.Open)
                {
                    return Result<SavePosDraftResult>.Failure(
                        "sales.draft_not_open",
                        "Only an open POS draft can be edited.");
                }

                if (command.ExpectedVersion is not null && draft.Version != command.ExpectedVersion.Value)
                {
                    return Result<SavePosDraftResult>.Failure(
                        "sales.draft_stale",
                        "The POS draft changed on another terminal. Reload before saving.");
                }

                var existingItems = await _drafts.GetItemsAsync(draft.Id, ct);
                foreach (var existingItem in existingItems)
                {
                    _drafts.RemoveItem(existingItem);
                }
            }

            draft.CustomerId = command.CustomerId;
            draft.TerminalId = Normalize(command.TerminalId);
            draft.Note = Normalize(command.Note);
            draft.UpdatedAt = _clock.UtcNow;
            draft.Version++;

            foreach (var input in command.Items)
            {
                var product = await _catalog.GetProductAsync(input.ProductId, ct);
                var unit = await _catalog.GetProductUnitAsync(input.ProductUnitId, ct);
                if (product is null || !product.IsActive ||
                    unit is null || unit.ProductId != product.Id || !unit.IsActive || !unit.CanSell)
                {
                    return Result<SavePosDraftResult>.Failure(
                        "sales.draft_product_invalid",
                        "A draft product or selling unit is invalid/inactive.");
                }

                TransactionQuantitySnapshot quantity;
                try
                {
                    quantity = TransactionQuantitySnapshot.Create(unit, input.EnteredQuantity, product.TrackingMode);
                }
                catch (BusinessRuleException ex)
                {
                    return Result<SavePosDraftResult>.Failure(ex.Code, ex.Message);
                }

                if (product.TrackingMode == TrackingMode.Serialized &&
                    (quantity.BaseQuantity != 1m || input.SelectedInventoryUnitId is null))
                {
                    return Result<SavePosDraftResult>.Failure(
                        "sales.draft_exact_unit_required",
                        "Each serialized draft line represents exactly one selected physical unit.");
                }

                _drafts.AddItem(new PosDraftItem
                {
                    DraftId = draft.Id,
                    ProductId = product.Id,
                    ProductUnitId = unit.Id,
                    EnteredQuantity = input.EnteredQuantity,
                    FactorToBaseSnapshot = unit.FactorToBaseUnit,
                    BaseQuantity = quantity.BaseQuantity,
                    DisplayedUnitPriceSnapshot = decimal.Round(
                        product.DefaultSalePrice * unit.FactorToBaseUnit,
                        2,
                        MidpointRounding.AwayFromZero),
                    SelectedInventoryUnitId = input.SelectedInventoryUnitId,
                    Note = Normalize(input.Note)
                });
            }

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<SavePosDraftResult>.Success(
                new(draft.Id, draft.DraftNumber, draft.Version, created));
        }, cancellationToken);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}

public sealed record CancelPosDraftCommand(
    Guid DraftId,
    long? ExpectedVersion,
    Guid ActorId);

public sealed class CancelPosDraftHandler
{
    private readonly IPosDraftRepository _drafts;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CancelPosDraftHandler(
        IPosDraftRepository drafts,
        IApplicationPermissionAuthorizer authorization,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _drafts = drafts;
        _authorization = authorization;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(CancelPosDraftCommand command, CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.SalesDraftCancel,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result.Failure(authorization.Error!.Code, authorization.Error.Message);
            }

            var draft = await _drafts.GetForUpdateAsync(command.DraftId, ct);
            if (draft is null)
            {
                return Result.Failure("sales.draft_not_found", "POS draft was not found.");
            }

            if (draft.Status == PosDraftStatus.Cancelled)
            {
                return Result.Success();
            }

            if (draft.Status != PosDraftStatus.Open)
            {
                return Result.Failure("sales.draft_not_open", "Only an open draft can be cancelled.");
            }

            if (command.ExpectedVersion is not null && draft.Version != command.ExpectedVersion.Value)
            {
                return Result.Failure("sales.draft_stale", "Reload the draft before cancelling.");
            }

            draft.Status = PosDraftStatus.Cancelled;
            draft.UpdatedAt = _clock.UtcNow;
            draft.Version++;
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}

public sealed record CompletePosDraftCommand(
    Guid DraftId,
    long ExpectedVersion,
    Guid ClientOperationId,
    Guid CashierUserId,
    Guid? SessionId,
    decimal InvoiceDiscount,
    SalePaymentMethod PaymentMethod,
    decimal AmountTendered,
    string? PaymentReference);

public sealed class CompletePosDraftHandler
{
    private readonly IPosDraftRepository _drafts;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly CompleteSaleHandler _completeSale;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;

    public CompletePosDraftHandler(
        IPosDraftRepository drafts,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        CompleteSaleHandler completeSale,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork)
    {
        _drafts = drafts;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _completeSale = completeSale;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
    }

    public Task<Result<CompleteSaleResult>> HandleAsync(
        CompletePosDraftCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return Task.FromResult(Result<CompleteSaleResult>.Failure(
                "sales.operation_id_required",
                "Client operation id is required."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.CashierUserId,
                PermissionKeys.SalesDraftResume,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<CompleteSaleResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            await _resourceLock.AcquireAsync("pos-draft", command.DraftId, ct);
            var draft = await _drafts.GetForUpdateAsync(command.DraftId, ct);
            if (draft is null)
            {
                return Result<CompleteSaleResult>.Failure(
                    "sales.draft_not_found",
                    "POS draft was not found.");
            }

            if (draft.Status != PosDraftStatus.Open)
            {
                return Result<CompleteSaleResult>.Failure(
                    "sales.draft_not_open",
                    "Only an open draft can be completed.");
            }

            if (draft.Version != command.ExpectedVersion)
            {
                return Result<CompleteSaleResult>.Failure(
                    "sales.draft_stale",
                    "The POS draft changed. Reload before completing.");
            }

            var items = await _drafts.GetItemsAsync(draft.Id, ct);
            if (items.Count == 0)
            {
                return Result<CompleteSaleResult>.Failure(
                    "sales.draft_empty",
                    "The POS draft contains no items.");
            }

            var lines = items
                .GroupBy(x => new { x.ProductId, x.ProductUnitId, x.DisplayedUnitPriceSnapshot })
                .Select(group =>
                {
                    var selectedIds = group
                        .Where(x => x.SelectedInventoryUnitId is not null)
                        .Select(x => x.SelectedInventoryUnitId!.Value)
                        .Distinct()
                        .ToArray();

                    return new CompleteSaleLineInput(
                        group.Key.ProductId,
                        group.Key.ProductUnitId,
                        group.Sum(x => x.EnteredQuantity),
                        group.Key.DisplayedUnitPriceSnapshot,
                        selectedIds);
                })
                .ToArray();

            var result = await _completeSale.HandleAsync(
                new CompleteSaleCommand(
                    command.ClientOperationId,
                    draft.CustomerId,
                    command.CashierUserId,
                    command.SessionId,
                    command.InvoiceDiscount,
                    command.PaymentMethod,
                    command.AmountTendered,
                    command.PaymentReference,
                    null,
                    lines),
                ct);

            if (!result.IsSuccess)
            {
                return result;
            }

            draft.Status = PosDraftStatus.Converted;
            draft.UpdatedAt = _clock.UtcNow;
            draft.Version++;
            await _unitOfWork.SaveChangesAsync(ct);
            return result;
        }, cancellationToken);
    }
}
