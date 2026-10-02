using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Application.Features.Finance;

public sealed record ExpenseCategoryDto(Guid Id, string Name);

public sealed record ExpenseSubcategoryDto(Guid Id, Guid CategoryId, string Name);

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

// Phase 2: paginated expense query
public sealed record GetExpensesPageQuery(
    DateOnly? FromDate = null,
    DateOnly? ToDate = null,
    Guid? CategoryId = null,
    int PageSize = 100,
    DateOnly? BeforeExpenseDate = null,
    Guid? BeforeExpenseId = null);

public interface IExpenseReadService
{
    Task<IReadOnlyList<ExpenseCategoryDto>> GetCategoriesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpenseSubcategoryDto>> GetSubcategoriesAsync(
        CancellationToken cancellationToken);

    /// <summary>Full unbounded list — kept for backward compat; prefer GetExpensesPageAsync.</summary>
    Task<IReadOnlyList<ExpenseRowDto>> GetExpensesAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ExpenseRowDto>> GetExpensesPageAsync(
        GetExpensesPageQuery query,
        CancellationToken cancellationToken);
}

// Phase 2: handler for paginated expenses
public sealed class GetExpensesPageHandler
{
    private readonly IExpenseReadService _reads;

    public GetExpensesPageHandler(IExpenseReadService reads) => _reads = reads;

    public Task<IReadOnlyList<ExpenseRowDto>> HandleAsync(
        GetExpensesPageQuery query,
        CancellationToken cancellationToken) =>
        _reads.GetExpensesPageAsync(
            query with { PageSize = Math.Clamp(query.PageSize, 1, 500) },
            cancellationToken);
}
