using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Application.Features.Finance;

public sealed record PostExpenseCommand(
    Guid ClientOperationId,
    Guid CategoryId,
    Guid? SubcategoryId,
    DateOnly ExpenseDate,
    decimal Amount,
    ExpensePaymentMethod PaymentMethod,
    string Description,
    string? Reference,
    Guid ActorId);

public sealed record PostExpenseResult(
    Guid ExpenseId,
    string ExpenseNumber,
    bool WasExisting);

public sealed class PostExpenseHandler
{
    private readonly IExpenseRepository _expenses;
    private readonly ICashMovementService _cashMovements;
    private readonly IOperationLock _operationLock;
    private readonly IDocumentNumberService _numbers;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork; public PostExpenseHandler(
        IExpenseRepository expenses,
        ICashMovementService cashMovements,
        IOperationLock operationLock,
        IDocumentNumberService numbers,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationOutcomeLedger outcomeLedger)
    {
        _expenses = expenses;
        _cashMovements = cashMovements;
        _operationLock = operationLock;
        _numbers = numbers;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _outcomes = outcomeLedger;
    }

    private readonly IOperationOutcomeLedger _outcomes;

    public Task<Result<PostExpenseResult>> HandleAsync(
        PostExpenseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty ||
            command.Amount <= 0 ||
            string.IsNullOrWhiteSpace(command.Description))
        {
            return Task.FromResult(Result<PostExpenseResult>.Failure(
                "expense.invalid_request",
                "Expense requires operation id, positive amount and description."));
        }

        return _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ExpensesManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return Result<PostExpenseResult>.Failure(
                    authorization.Error!.Code,
                    authorization.Error.Message);
            }

            var roundedAmount = decimal.Round(command.Amount, 2, MidpointRounding.AwayFromZero);
            var normalizedRef = Normalize(command.Reference);
            var normalizedDesc = command.Description.Trim();
            var fingerprint = OperationPayloadFingerprint.ComputeSha256(System.Text.Json.JsonSerializer.Serialize(new
            {
                Version = 1,
                command.CategoryId,
                command.SubcategoryId,
                ExpenseDate = command.ExpenseDate.ToString("O"),
                Amount = roundedAmount,
                PaymentMethod = command.PaymentMethod.ToString(),
                Reference = normalizedRef,
                Description = normalizedDesc,
                command.ActorId
            }));

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var outcome = await _outcomes.GetOutcomeAsync(command.ClientOperationId, ct);
            var existing = await _expenses.GetByClientOperationIdAsync(command.ClientOperationId, ct);
            if (outcome is not null)
            {
                if (!string.Equals(outcome.OperationType, "Expense", StringComparison.OrdinalIgnoreCase))
                {
                    return Result<PostExpenseResult>.Failure(
                        "idempotency.payload_mismatch",
                        $"Operation identity already belongs to another durable operation ({outcome.OperationType}).");
                }

                if (outcome.State != OperationOutcomeState.Succeeded ||
                    !outcome.WasCommitted || existing is null || outcome.EntityId != existing.Id ||
                    (outcome.ActorId.HasValue && outcome.ActorId != command.ActorId) ||
                    (outcome.PayloadFingerprint is not null && outcome.PayloadFingerprint != fingerprint))
                {
                    return Result<PostExpenseResult>.Failure("idempotency.payload_mismatch",
                        "Operation identity already belongs to another durable operation.");
                }
            }
            if (existing is not null)
            {
                if (existing.CategoryId != command.CategoryId ||
                    existing.SubcategoryId != command.SubcategoryId ||
                    existing.ExpenseDate != command.ExpenseDate ||
                    existing.Amount != roundedAmount ||
                    existing.PaymentMethod != command.PaymentMethod ||
                    existing.Reference != normalizedRef ||
                    existing.Description != normalizedDesc ||
                    existing.CreatedBy != command.ActorId)
                {
                    return Result<PostExpenseResult>.Failure(
                        "idempotency.payload_mismatch",
                        "Operation was previously submitted with different expense parameters.");
                }

                await _outcomes.RecordSuccessAsync(
                    command.ClientOperationId,
                    "Expense",
                    existing.Id,
                    existing.ExpenseNumber,
                    actorId: command.ActorId,
                    payloadFingerprint: fingerprint,
                    cancellationToken: ct);

                return Result<PostExpenseResult>.Success(
                    new(existing.Id, existing.ExpenseNumber, true));
            }

            var category = await _expenses.GetCategoryAsync(command.CategoryId, ct);
            if (category is null || !category.IsActive)
            {
                return Result<PostExpenseResult>.Failure(
                    "expense.category_inactive",
                    "Expense category was not found or is inactive.");
            }

            if (command.SubcategoryId is not null)
            {
                var subcategory = await _expenses.GetSubcategoryAsync(
                    command.SubcategoryId.Value,
                    ct);
                if (subcategory is null ||
                    !subcategory.IsActive ||
                    subcategory.CategoryId != category.Id)
                {
                    return Result<PostExpenseResult>.Failure(
                        "expense.subcategory_invalid",
                        "Expense subcategory is invalid for the selected category.");
                }
            }

            var expense = new Expense
            {
                ExpenseNumber = await _numbers.NextAsync("EXP", ct),
                CategoryId = command.CategoryId,
                SubcategoryId = command.SubcategoryId,
                ExpenseDate = command.ExpenseDate,
                Amount = roundedAmount,
                PaymentMethod = command.PaymentMethod,
                Reference = normalizedRef,
                Description = normalizedDesc,
                ClientOperationId = command.ClientOperationId,
                CreatedBy = command.ActorId,
                CreatedAt = _clock.UtcNow
            };
            _expenses.AddExpense(expense);

            if (command.PaymentMethod == ExpensePaymentMethod.Cash)
            {
                var cash = await _cashMovements.RecordAsync(
                    new RecordCashMovementRequest(
                        CashMovementType.ExpenseCashOut,
                        CashMovementDirection.Out,
                        expense.Amount,
                        command.ActorId,
                        "EXPENSE",
                        expense.Id,
                        "Operating expense",
                        expense.Description),
                    ct);
                if (!cash.IsSuccess)
                {
                    return Result<PostExpenseResult>.Failure(
                        cash.Error!.Code,
                        cash.Error.Message);
                }
            }

            _audit.Record(
                "EXPENSE_POSTED",
                "EXPENSE",
                expense.Id,
                command.ActorId,
                command.ClientOperationId,
                $"{expense.ExpenseNumber}: {expense.Amount:0.00}");

            await _outcomes.RecordSuccessAsync(
                command.ClientOperationId,
                "Expense",
                expense.Id,
                expense.ExpenseNumber,
                actorId: command.ActorId,
                payloadFingerprint: fingerprint,
                cancellationToken: ct);

            await _unitOfWork.SaveChangesAsync(ct);
            return Result<PostExpenseResult>.Success(
                new(expense.Id, expense.ExpenseNumber, false));
        }, cancellationToken);
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
public sealed record VoidExpenseCommand(
    Guid ExpenseId,
    Guid ActorId,
    Guid ClientOperationId,
    string Reason);

public sealed class VoidExpenseHandler
{
    private readonly IOperationLock _operationLock;
    private readonly IOperationOutcomeLedger _outcomes;
    private readonly IExpenseRepository _expenses;
    private readonly ICashMovementService _cashMovements;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public VoidExpenseHandler(
        IExpenseRepository expenses,
        ICashMovementService cashMovements,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork,
        IOperationLock operationLock,
        IOperationOutcomeLedger outcomeLedger)
    {
        _expenses = expenses;
        _cashMovements = cashMovements;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
        _operationLock = operationLock;
        _outcomes = outcomeLedger;
    }

    public async Task<Result> HandleAsync(
        VoidExpenseCommand command,
        CancellationToken cancellationToken)
    {
        if (command.ClientOperationId == Guid.Empty || command.ExpenseId == Guid.Empty ||
            command.ActorId == Guid.Empty)
        {
            return Result.Failure("expense.void_operation_id_required", "Expense, actor and operation identity are required.");
        }
        if (string.IsNullOrWhiteSpace(command.Reason))
        {
            return Result.Failure("expense.void_reason_required", "Voiding an expense requires a reason.");
        }
        var fingerprint = OperationPayloadFingerprint.ComputeSha256(System.Text.Json.JsonSerializer.Serialize(new
        {
            Version = 1, command.ExpenseId, command.ActorId, Reason = command.Reason.Trim()
        }));
        var ownsOutcome = false;
        var result = await _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ExpensesManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return authorization;
            }

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var outcome = await _outcomes.GetOutcomeAsync(command.ClientOperationId, ct);
            if (outcome is not null)
            {
                if (outcome.OperationType != "ExpenseVoid" || outcome.ActorId != command.ActorId ||
                    outcome.PayloadFingerprint != fingerprint ||
                    (outcome.EntityId.HasValue && outcome.EntityId != command.ExpenseId))
                {
                    return Result.Failure("idempotency.payload_mismatch", "Operation was submitted with a different expense void intent.");
                }
                if (outcome.State == OperationOutcomeState.Succeeded && outcome.WasCommitted &&
                    outcome.EntityId == command.ExpenseId)
                {
                    return Result.Success();
                }
                return Result.Failure(outcome.ErrorCode ?? "idempotency.outcome_unknown",
                    outcome.ErrorMessage ?? "Reconcile the previous void outcome before another execution.");
            }
            if (await _expenses.GetByClientOperationIdAsync(command.ClientOperationId, ct) is not null)
            {
                return Result.Failure("idempotency.payload_mismatch", "Operation identity already belongs to an expense posting.");
            }
            ownsOutcome = true;

            var expense = await _expenses.GetForUpdateAsync(command.ExpenseId, ct);
            if (expense is null)
            {
                return Result.Failure(
                    "expense.not_found",
                    "Expense was not found.");
            }

            if (expense.Status == ExpenseStatus.Voided)
            {
                await _outcomes.RecordSuccessAsync(command.ClientOperationId, "ExpenseVoid", expense.Id,
                    expense.ExpenseNumber, actorId: command.ActorId, payloadFingerprint: fingerprint, cancellationToken: ct);
                await _unitOfWork.SaveChangesAsync(ct);
                return Result.Success();
            }

            if (expense.PaymentMethod == ExpensePaymentMethod.Cash)
            {
                var cashResult = await _cashMovements.RecordAsync(
                    new RecordCashMovementRequest(
                        CashMovementType.ManualCashIn,
                        CashMovementDirection.In,
                        expense.Amount,
                        command.ActorId,
                        "EXPENSE",
                        expense.Id,
                        $"Expense void {expense.ExpenseNumber}",
                        command.Reason.Trim()),
                    ct);
                if (!cashResult.IsSuccess)
                {
                    return Result.Failure(
                        cashResult.Error!.Code,
                        cashResult.Error.Message);
                }
            }

            expense.Status = ExpenseStatus.Voided;
            expense.VoidedBy = command.ActorId;
            expense.VoidedAt = _clock.UtcNow;
            expense.VoidReason = command.Reason.Trim();
            expense.Version++;

            _audit.Record(
                "EXPENSE_VOIDED",
                "EXPENSE",
                expense.Id,
                command.ActorId,
                command.ClientOperationId,
                command.Reason.Trim());

            await _outcomes.RecordSuccessAsync(command.ClientOperationId, "ExpenseVoid", expense.Id,
                expense.ExpenseNumber, actorId: command.ActorId, payloadFingerprint: fingerprint, cancellationToken: ct);
            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
        if (!result.IsSuccess && ownsOutcome)
        {
            // The failed business transaction released its operation lock. Recheck authority
            // under a new lock before persisting failure; another intent may have won meanwhile.
            var settled = await _transactions.ExecuteAsync(async ct =>
            {
                await _operationLock.AcquireAsync(command.ClientOperationId, ct);
                var latest = await _outcomes.GetOutcomeAsync(command.ClientOperationId, ct);
                if (latest is not null)
                {
                    var matches = latest.OperationType == "ExpenseVoid" && latest.ActorId == command.ActorId &&
                        latest.PayloadFingerprint == fingerprint &&
                        (!latest.EntityId.HasValue || latest.EntityId == command.ExpenseId);
                    var replay = !matches
                        ? Result.Failure("idempotency.payload_mismatch", "Operation identity now belongs to a different intent.")
                        : latest.State == OperationOutcomeState.Succeeded && latest.WasCommitted && latest.EntityId == command.ExpenseId
                            ? Result.Success()
                            : Result.Failure(latest.ErrorCode ?? "idempotency.outcome_unknown",
                                latest.ErrorMessage ?? "Reconcile the previous void outcome.");
                    return (Result: replay, Recorded: false);
                }
                if (await _expenses.GetByClientOperationIdAsync(command.ClientOperationId, ct) is not null)
                {
                    return (Result: Result.Failure("idempotency.payload_mismatch", "Operation identity now belongs to an expense posting."), Recorded: false);
                }
                await _outcomes.RecordFailureAsync(command.ClientOperationId, "ExpenseVoid",
                    result.Error!.Code, result.Error.Message, actorId: command.ActorId,
                    payloadFingerprint: fingerprint, cancellationToken: ct);
                // A tuple commits the failure ledger; returning a failed IResult would roll it back.
                return (Result: result, Recorded: true);
            }, cancellationToken);
            return settled.Result;
        }
        return result;
    }
}
