using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class ExpenseRepository : IExpenseRepository
{
    private readonly EdgeRetailsDbContext _db;

    public ExpenseRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<Expense?> GetByClientOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.Expenses.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<Expense?> GetForUpdateAsync(
        Guid expenseId,
        CancellationToken cancellationToken) =>
        _db.Expenses
            .FromSqlInterpolated(
                $"SELECT * FROM finance.expenses WHERE id = {expenseId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken); public Task<ExpenseCategory?> GetCategoryAsync(
        Guid categoryId,
        CancellationToken cancellationToken) =>
        _db.ExpenseCategories.SingleOrDefaultAsync(
            x => x.Id == categoryId,
            cancellationToken);

    public Task<ExpenseSubcategory?> GetSubcategoryAsync(
        Guid subcategoryId,
        CancellationToken cancellationToken) =>
        _db.ExpenseSubcategories.SingleOrDefaultAsync(
            x => x.Id == subcategoryId,
            cancellationToken);

    public async Task<IReadOnlyList<ExpenseCategory>> GetCategoriesAsync(
        bool includeInactive,
        CancellationToken cancellationToken) =>
        await _db.ExpenseCategories
            .Where(x => includeInactive || x.IsActive)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExpenseSubcategory>> GetSubcategoriesAsync(
        Guid? categoryId,
        bool includeInactive,
        CancellationToken cancellationToken) =>
        await _db.ExpenseSubcategories
            .Where(x =>
                (categoryId == null || x.CategoryId == categoryId) &&
                (includeInactive || x.IsActive))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public void AddCategory(ExpenseCategory category) => _db.ExpenseCategories.Add(category);
    public void AddSubcategory(ExpenseSubcategory subcategory) => _db.ExpenseSubcategories.Add(subcategory);
    public void AddExpense(Expense expense) => _db.Expenses.Add(expense);
}
public sealed class ThakaRepository : IThakaRepository
{
    private readonly EdgeRetailsDbContext _db;

    public ThakaRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public Task<ThakaProject?> GetProjectAsync(
        Guid projectId,
        CancellationToken cancellationToken) =>
        _db.ThakaProjects
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == projectId, cancellationToken);

    public Task<ThakaProject?> GetProjectForUpdateAsync(
        Guid projectId,
        CancellationToken cancellationToken) =>
        _db.ThakaProjects
            .FromSqlInterpolated(
                $"SELECT * FROM thaka.projects WHERE id = {projectId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ThakaMaterialIssue?> GetIssueByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.ThakaMaterialIssues.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<ThakaMaterialIssue?> GetIssueForUpdateAsync(
        Guid materialIssueId,
        CancellationToken cancellationToken) =>
        _db.ThakaMaterialIssues
            .FromSqlInterpolated(
                $"SELECT * FROM thaka.material_issues WHERE id = {materialIssueId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken); public Task<ThakaPayment?> GetPaymentByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.ThakaPayments.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<ThakaPayment?> GetPaymentForUpdateAsync(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        _db.ThakaPayments
            .FromSqlInterpolated(
                $"SELECT * FROM thaka.payments WHERE id = {paymentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    public Task<ThakaSettlement?> GetSettlementByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.ThakaSettlements.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<ThakaMaterialReversal?> GetMaterialReversalByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.ThakaMaterialReversals.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<ThakaMaterialReversal?> GetMaterialReversalByIssueAsync(
        Guid materialIssueId,
        CancellationToken cancellationToken) =>
        _db.ThakaMaterialReversals.SingleOrDefaultAsync(
            x => x.MaterialIssueId == materialIssueId,
            cancellationToken);

    public Task<ThakaPaymentReversal?> GetPaymentReversalByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken) =>
        _db.ThakaPaymentReversals.SingleOrDefaultAsync(
            x => x.ClientOperationId == clientOperationId,
            cancellationToken);

    public Task<ThakaPaymentReversal?> GetPaymentReversalByPaymentAsync(
        Guid paymentId,
        CancellationToken cancellationToken) =>
        _db.ThakaPaymentReversals.SingleOrDefaultAsync(
            x => x.PaymentId == paymentId,
            cancellationToken);

    public async Task<IReadOnlyList<ThakaMaterialIssueItem>> GetIssueItemsAsync(
        Guid materialIssueId,
        CancellationToken cancellationToken) =>
        await _db.ThakaMaterialIssueItems
            .Where(x => x.MaterialIssueId == materialIssueId)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken); public async Task<IReadOnlyList<ThakaMaterialIssueUnit>> GetIssueUnitsAsync(
        Guid materialIssueItemId,
        CancellationToken cancellationToken) =>
        await _db.ThakaMaterialIssueUnits
            .Where(x => x.MaterialIssueItemId == materialIssueItemId)
            .OrderBy(x => x.InventoryUnitId)
            .ToListAsync(cancellationToken);

    public async Task<decimal> GetGrossMaterialChargesAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var issued = await _db.ThakaMaterialIssues
            .Where(x => x.ProjectId == projectId)
            .SumAsync(x => (decimal?)x.TotalCharge, cancellationToken) ?? 0m;
        var reversed = await _db.ThakaMaterialReversals
            .Where(x => x.ProjectId == projectId)
            .SumAsync(x => (decimal?)x.ReversedCharge, cancellationToken) ?? 0m;
        return decimal.Round(issued - reversed, 2);
    }

    public async Task<decimal> GetPaymentsCollectedAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var paid = await _db.ThakaPayments
            .Where(x => x.ProjectId == projectId)
            .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;
        var reversed = await _db.ThakaPaymentReversals
            .Where(x => x.ProjectId == projectId)
            .SumAsync(x => (decimal?)x.Amount, cancellationToken) ?? 0m;
        return decimal.Round(paid - reversed, 2);
    }

    public async Task<decimal> GetSettlementDiscountsAsync(
        Guid projectId,
        CancellationToken cancellationToken) =>
        decimal.Round(
            await _db.ThakaSettlements
                .Where(x => x.ProjectId == projectId)
                .SumAsync(x => (decimal?)x.SettlementDiscount, cancellationToken) ?? 0m,
            2); public Task<ThakaSettlement?> GetLatestSettlementAsync(
        Guid projectId,
        CancellationToken cancellationToken) =>
        _db.ThakaSettlements
            .Where(x => x.ProjectId == projectId)
            .OrderByDescending(x => x.SettledAt)
            .ThenByDescending(x => x.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public void AddProject(ThakaProject project) => _db.ThakaProjects.Add(project);
    public void AddMaterialIssue(ThakaMaterialIssue issue) => _db.ThakaMaterialIssues.Add(issue);
    public void AddMaterialIssueItem(ThakaMaterialIssueItem item) => _db.ThakaMaterialIssueItems.Add(item);
    public void AddMaterialIssueUnit(ThakaMaterialIssueUnit unit) => _db.ThakaMaterialIssueUnits.Add(unit);
    public void AddPayment(ThakaPayment payment) => _db.ThakaPayments.Add(payment);
    public void AddSettlement(ThakaSettlement settlement) => _db.ThakaSettlements.Add(settlement);
    public void AddReopening(ThakaReopening reopening) => _db.ThakaReopenings.Add(reopening);
    public void AddMaterialReversal(ThakaMaterialReversal reversal) => _db.ThakaMaterialReversals.Add(reversal);
    public void AddPaymentReversal(ThakaPaymentReversal reversal) => _db.ThakaPaymentReversals.Add(reversal);
}
