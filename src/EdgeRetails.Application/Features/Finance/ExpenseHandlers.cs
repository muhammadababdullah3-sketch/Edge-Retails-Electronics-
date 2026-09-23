using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Common;
using EdgeRetails.Application.Features.Identity;
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
        IUnitOfWork unitOfWork)
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
    }

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

            await _operationLock.AcquireAsync(command.ClientOperationId, ct);
            var existing = await _expenses.GetByClientOperationIdAsync(
                command.ClientOperationId,
                ct); if (existing is not null)
            {
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
                Amount = decimal.Round(command.Amount, 2),
                PaymentMethod = command.PaymentMethod,
                Reference = Normalize(command.Reference),
                Description = command.Description.Trim(),
                ClientOperationId = command.ClientOperationId,
                CreatedBy = command.ActorId,
                CreatedAt = _clock.UtcNow
            }; _expenses.AddExpense(expense);

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
    Guid CorrelationId,
    string Reason);

public sealed class VoidExpenseHandler
{
    private readonly IExpenseRepository _expenses;
    private readonly IBusinessAuditWriter _audit;
    private readonly IClock _clock;
    private readonly ITransactionRunner _transactions;
    private readonly IApplicationPermissionAuthorizer _authorization;
    private readonly IUnitOfWork _unitOfWork;

    public VoidExpenseHandler(
        IExpenseRepository expenses,
        IBusinessAuditWriter audit,
        IClock clock,
        ITransactionRunner transactions,
        IApplicationPermissionAuthorizer authorization,
        IUnitOfWork unitOfWork)
    {
        _expenses = expenses;
        _audit = audit;
        _clock = clock;
        _transactions = transactions;
        _authorization = authorization;
        _unitOfWork = unitOfWork;
    }

    public Task<Result> HandleAsync(
        VoidExpenseCommand command,
        CancellationToken cancellationToken) =>
        _transactions.ExecuteAsync(async ct =>
        {
            var authorization = await _authorization.AuthorizeAsync(
                command.ActorId,
                PermissionKeys.ExpensesManage,
                ct);
            if (!authorization.IsSuccess)
            {
                return authorization;
            }

            if (string.IsNullOrWhiteSpace(command.Reason))
            {
                return Result.Failure(
                    "expense.void_reason_required",
                    "Voiding an expense requires a reason.");
            }

            var expense = await _expenses.GetForUpdateAsync(command.ExpenseId, ct);
            if (expense is null)
            {
                return Result.Failure(
                    "expense.not_found",
                    "Expense was not found.");
            }

            if (expense.Status == ExpenseStatus.Voided)
            {
                return Result.Success();
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
                command.CorrelationId,
                command.Reason.Trim());

            await _unitOfWork.SaveChangesAsync(ct);
            return Result.Success();
        }, cancellationToken);
}
