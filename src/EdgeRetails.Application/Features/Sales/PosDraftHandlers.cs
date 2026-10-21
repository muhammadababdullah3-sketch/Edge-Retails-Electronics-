using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Catalog;
using EdgeRetails.Domain.Common;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Domain.Sales;
using System.Text.Json;

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
    IReadOnlyList<SavePosDraftItemInput> Items,
    Guid ClientOperationId = default,
    Guid? RegisteredTerminalId = null,
    Guid? SessionId = null);

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
    private readonly IOperationLock? _operationLock;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public SavePosDraftHandler(
        IPosDraftRepository drafts,
        ICatalogRepository catalog,
        IDocumentNumberService numbers,
        IClock clock,
        IApplicationPermissionAuthorizer authorization,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock? operationLock = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _drafts = drafts;
        _catalog = catalog;
        _numbers = numbers;
        _clock = clock;
        _authorization = authorization;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _operationLock = operationLock;
        _outcomeLedger = outcomeLedger;
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

        if (command.ClientOperationId != Guid.Empty &&
            (_operationLock is null || _outcomeLedger is null))
        {
            return Task.FromResult(Result<SavePosDraftResult>.Failure(
                "system.idempotency_unavailable",
                "Draft save cannot proceed without an operation ledger."));
        }

        var fingerprint = command.ClientOperationId == Guid.Empty
            ? null
            : Fingerprint(command);

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

            if (command.ClientOperationId != Guid.Empty)
            {
                await _operationLock!.AcquireAsync(command.ClientOperationId, ct);
                var prior = await _outcomeLedger!.GetOutcomeAsync(command.ClientOperationId, ct);
                if (prior is not null)
                {
                    if (!string.Equals(prior.OperationType, "PosDraftSave", StringComparison.Ordinal) ||
                        prior.ActorId != command.ActorId ||
                        prior.TerminalId != command.RegisteredTerminalId ||
                        !string.Equals(prior.PayloadFingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        return Result<SavePosDraftResult>.Failure(
                            "idempotency.payload_mismatch",
                            "This operation ID was previously used for a different draft save.");
                    }

                    if (prior.State == OperationOutcomeState.Succeeded &&
                        prior.EntityId is Guid savedDraftId)
                    {
                        var savedDraft = await _drafts.GetForUpdateAsync(savedDraftId, ct);
                        if (savedDraft is null)
                        {
                            return Result<SavePosDraftResult>.Failure(
                                "sales.draft_outcome_missing",
                                "The committed draft could not be loaded safely.");
                        }

                        return Result<SavePosDraftResult>.Success(new SavePosDraftResult(
                            savedDraft.Id,
                            savedDraft.DraftNumber,
                            savedDraft.Version,
                            command.DraftId is null));
                    }

                    return Result<SavePosDraftResult>.Failure(
                        "sales.draft_outcome_unknown",
                        "The draft save outcome is not confirmed. Recheck it before retrying.");
                }
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

                if (product.TrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container &&
                    ((product.TrackingMode == TrackingMode.Container ? quantity.EnteredQuantity : quantity.BaseQuantity) != 1m || input.SelectedInventoryUnitId is null))
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
            if (command.ClientOperationId != Guid.Empty)
            {
                await _outcomeLedger!.RecordSuccessAsync(
                    command.ClientOperationId,
                    "PosDraftSave",
                    draft.Id,
                    draft.DraftNumber,
                    actorId: command.ActorId,
                    terminalId: command.RegisteredTerminalId,
                    sessionId: command.SessionId,
                    payloadFingerprint: fingerprint,
                    cancellationToken: ct);
            }
            return Result<SavePosDraftResult>.Success(
                new(draft.Id, draft.DraftNumber, draft.Version, created));
        }, cancellationToken);
    }

    private static string Fingerprint(SavePosDraftCommand command) =>
        OperationPayloadFingerprint.ComputeSha256(JsonSerializer.Serialize(new
        {
            command.DraftId,
            command.ExpectedVersion,
            command.CustomerId,
            command.ActorId,
            command.TerminalId,
            command.Note,
            command.Items
        }));

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
    string? PaymentReference,
    DateOnly? PostingDate = null,
    SupervisorApprovalRequest? SupervisorApproval = null,
    Guid? RegisteredTerminalId = null);

public sealed class CompletePosDraftHandler
{
    private readonly IPosDraftRepository _drafts;
    private readonly IResourceLock _resourceLock;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly CompleteSaleHandler _completeSale;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IOperationLock _operationLock;
    private readonly ISalesRepository? _sales;
    private readonly IOperationOutcomeLedger? _outcomeLedger;

    public CompletePosDraftHandler(
        IPosDraftRepository drafts,
        IResourceLock resourceLock,
        IApplicationPermissionAuthorizer authorization,
        CompleteSaleHandler completeSale,
        IClock clock,
        ITransactionRunner transactions,
        IUnitOfWork unitOfWork,
        IOperationLock operationLock,
        ISalesRepository? sales = null,
        IOperationOutcomeLedger? outcomeLedger = null)
    {
        _drafts = drafts;
        _resourceLock = resourceLock;
        _authorization = authorization;
        _completeSale = completeSale;
        _clock = clock;
        _transactions = transactions;
        _unitOfWork = unitOfWork;
        _operationLock = operationLock;
        _sales = sales;
        _outcomeLedger = outcomeLedger;
    }

    public async Task<Result<CompleteSaleResult>> HandleAsync(
        CompletePosDraftCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty)
        {
            return Result<CompleteSaleResult>.Failure(
                "sales.operation_id_required",
                "Client operation id is required.");
        }

        // Area 12G / F07 / H01: Check durable outcome first for command.ClientOperationId before mutable state validation
        var expectedDraftFingerprint = "PosDraft:v1:" + OperationPayloadFingerprint.ComputeSha256(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                SourceDraftId = (Guid?)command.DraftId,
                SourceDraftVersion = (long?)command.ExpectedVersion
            }));

        if (_outcomeLedger is not null)
        {
            var outcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, cancellationToken);
            if (outcome is not null)
            {
                var evaluated = await EvaluateGuardedOutcomeAsync(outcome, command, expectedDraftFingerprint, cancellationToken);
                if (evaluated is not null)
                {
                    return evaluated.Value;
                }
            }
        }

        return await _transactions.ExecuteAsync(async ct =>
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

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            await _resourceLock.AcquireAsync("pos-draft", command.DraftId, ct);

            if (_outcomeLedger is not null)
            {
                var postLockOutcome = await _outcomeLedger.GetOutcomeAsync(command.ClientOperationId, ct);
                if (postLockOutcome is not null)
                {
                    var evaluatedPostLock = await EvaluateGuardedOutcomeAsync(postLockOutcome, command, expectedDraftFingerprint, ct);
                    if (evaluatedPostLock is not null)
                    {
                        return evaluatedPostLock.Value;
                    }
                }
            }

            var draft = await _drafts.GetForUpdateAsync(command.DraftId, ct);
            if (draft is null)
            {
                return Result<CompleteSaleResult>.Failure(
                    "sales.draft_not_found",
                    "POS draft was not found.");
            }

            if (draft.Status == PosDraftStatus.Converted)
            {
                return Result<CompleteSaleResult>.Failure(
                    "sales.draft_already_converted",
                    "Converted draft requires its original committed sale outcome; a new operation cannot charge it again.");
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
                    lines,
                    command.PostingDate,
                    command.SupervisorApproval,
                    RegisteredTerminalId: command.RegisteredTerminalId)
                {
                    SourceDraftId = draft.Id,
                    SourceDraftVersion = command.ExpectedVersion,
                    SourceDraftReplayOnly = false
                },
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

    private async Task<Result<CompleteSaleResult>?> EvaluateGuardedOutcomeAsync(
        OperationOutcomeRecord outcome,
        CompletePosDraftCommand command,
        string expectedDraftFingerprint,
        CancellationToken ct)
    {
        if (outcome.OperationType != "Sale")
        {
            return Result<CompleteSaleResult>.Failure(
                "idempotency.operation_type_mismatch",
                $"Operation was registered as '{outcome.OperationType}', not 'Sale'.");
        }

        if (outcome.ActorId.HasValue && outcome.ActorId.Value != command.CashierUserId)
        {
            return Result<CompleteSaleResult>.Failure(
                "idempotency.context_mismatch",
                "Operation actor mismatch.");
        }

        if (command.RegisteredTerminalId.HasValue && outcome.TerminalId.HasValue &&
            outcome.TerminalId.Value != command.RegisteredTerminalId.Value)
        {
            return Result<CompleteSaleResult>.Failure(
                "idempotency.context_mismatch",
                "Operation terminal mismatch.");
        }

        if (outcome.PayloadFingerprint != expectedDraftFingerprint)
        {
            return Result<CompleteSaleResult>.Failure(
                "idempotency.payload_mismatch",
                "Operation belongs to another draft completion intent.");
        }

        if (outcome.State == OperationOutcomeState.Succeeded && outcome.WasCommitted)
        {
            if (_sales is not null)
            {
                var sale = await _sales.GetSaleByClientOperationIdAsync(command.ClientOperationId, ct)
                    ?? (outcome.EntityId.HasValue ? await _sales.GetSaleForUpdateAsync(outcome.EntityId.Value, ct) : null);
                if (sale is not null)
                {
                    var payment = await _sales.GetSalePaymentAsync(sale.Id, ct);
                    if (payment is not null && (payment.Method != command.PaymentMethod ||
                        payment.AmountTendered != Math.Round(command.AmountTendered, 2, MidpointRounding.AwayFromZero) ||
                        sale.InvoiceDiscount != Math.Round(command.InvoiceDiscount, 2, MidpointRounding.AwayFromZero)))
                    {
                        return Result<CompleteSaleResult>.Failure(
                            "idempotency.payload_mismatch",
                            "Operation was previously submitted with different sale details.");
                    }

                    return Result<CompleteSaleResult>.Success(new CompleteSaleResult(
                        sale.Id,
                        sale.InvoiceNumber,
                        sale.GrandTotal,
                        payment?.ChangeGiven ?? 0m,
                        WasExisting: true));
                }
            }

            if (!string.IsNullOrEmpty(outcome.ResponsePayload))
            {
                try
                {
                    var cached = System.Text.Json.JsonSerializer.Deserialize<CompleteSaleResult>(outcome.ResponsePayload);
                    if (cached is not null && cached.SaleId != Guid.Empty && !string.IsNullOrEmpty(cached.InvoiceNumber))
                    {
                        return Result<CompleteSaleResult>.Success(cached with { WasExisting = true });
                    }
                }
                catch
                {
                    return Result<CompleteSaleResult>.Failure(
                        "idempotency.outcome_corrupted",
                        "The operation was recorded as succeeded, but the cached response payload is corrupted.");
                }
            }
        }
        else if (outcome.State == OperationOutcomeState.Failed)
        {
            return Result<CompleteSaleResult>.Failure(
                outcome.ErrorCode ?? "sales.already_failed",
                outcome.ErrorMessage ?? "The operation previously failed.");
        }

        return null;
    }
}
