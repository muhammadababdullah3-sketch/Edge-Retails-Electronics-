using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Application.Features.Finance;

public sealed record ExpenseCategoryDto(Guid Id, string Name);

public sealed record ExpenseRowDto(
    Guid ExpenseId,
    string ExpenseNumber,
    Guid CategoryId,
    string CategoryName,
    Guid? SubcategoryId,
    string? SubcategoryName,
    DateOnly ExpenseDate,
    decimal Amount,
    ExpensePaymentMethod PaymentMethod,
    string Description,
    string? Reference,
    ExpenseStatus Status,
    Guid CreatedBy,
    string CreatedByName);

public interface IExpenseReadService
{
    Task<IReadOnlyList<ExpenseCategoryDto>> GetCategoriesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpenseRowDto>> GetExpensesAsync(
        CancellationToken cancellationToken);
}
