using System.Globalization;
using System.Linq.Expressions;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Posting;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Inventory;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Thaka;
using EdgeRetails.Domain.Warranty;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Services;

public sealed class ExpenseReadService : IExpenseReadService
{
    private readonly EdgeRetailsDbContext _db;

    public ExpenseReadService(EdgeRetailsDbContext db) => _db = db;

    public async Task<IReadOnlyList<ExpenseCategoryDto>> GetCategoriesAsync(
        CancellationToken cancellationToken) =>
        await _db.ExpenseCategories
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new ExpenseCategoryDto(x.Id, x.Name))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExpenseSubcategoryDto>> GetSubcategoriesAsync(
        CancellationToken cancellationToken) =>
        await _db.ExpenseSubcategories
            .AsNoTracking()
            .Where(x => x.IsActive)
            .OrderBy(x => x.Name)
            .Select(x => new ExpenseSubcategoryDto(x.Id, x.CategoryId, x.Name))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<ExpenseRowDto>> GetExpensesPageAsync(
        GetExpensesPageQuery query,
        CancellationToken cancellationToken)
    {
        var dbQuery = (
            from expense in _db.Expenses.AsNoTracking()
            join category in _db.ExpenseCategories.AsNoTracking()
                on expense.CategoryId equals category.Id
            join subcategoryJoin in _db.ExpenseSubcategories.AsNoTracking()
                on expense.SubcategoryId equals subcategoryJoin.Id into subcategoryGroup
            from subcategory in subcategoryGroup.DefaultIfEmpty()
            join user in _db.Users.AsNoTracking()
                on expense.CreatedBy equals user.Id
            where expense.Status == ExpenseStatus.Posted
            select new { expense, category, subcategory, user });

        if (query.FromDate.HasValue)
        {
            dbQuery = dbQuery.Where(x => x.expense.ExpenseDate >= query.FromDate.Value);
        }

        if (query.ToDate.HasValue)
        {
            dbQuery = dbQuery.Where(x => x.expense.ExpenseDate <= query.ToDate.Value);
        }

        if (query.CategoryId.HasValue)
        {
            dbQuery = dbQuery.Where(x => x.expense.CategoryId == query.CategoryId.Value);
        }

        if (query.BeforeExpenseDate.HasValue && query.BeforeExpenseId.HasValue)
        {
            dbQuery = dbQuery.Where(x =>
                x.expense.ExpenseDate < query.BeforeExpenseDate.Value ||
                (x.expense.ExpenseDate == query.BeforeExpenseDate.Value &&
                 x.expense.Id < query.BeforeExpenseId.Value));
        }
        else if (query.BeforeExpenseDate.HasValue)
        {
            dbQuery = dbQuery.Where(x => x.expense.ExpenseDate < query.BeforeExpenseDate.Value);
        }

        var take = Math.Clamp(query.PageSize <= 0 ? 50 : query.PageSize, 1, 500);

        return await dbQuery
            .OrderByDescending(x => x.expense.ExpenseDate)
            .ThenByDescending(x => x.expense.Id)
            .Take(take)
            .Select(x => new ExpenseRowDto(
                x.expense.Id,
                x.expense.ExpenseNumber,
                x.expense.CategoryId,
                x.category.Name,
                x.expense.SubcategoryId,
                x.subcategory == null ? null : x.subcategory.Name,
                x.expense.ExpenseDate,
                x.expense.Amount,
                x.expense.PaymentMethod,
                x.expense.Description,
                x.expense.Reference,
                x.expense.Status,
                x.expense.CreatedBy,
                x.user.DisplayName))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExpenseRowDto>> GetExpensesAsync(
        CancellationToken cancellationToken)
    {
        return await (
            from expense in _db.Expenses.AsNoTracking()
            join category in _db.ExpenseCategories.AsNoTracking()
                on expense.CategoryId equals category.Id
            join subcategoryJoin in _db.ExpenseSubcategories.AsNoTracking()
                on expense.SubcategoryId equals subcategoryJoin.Id into subcategoryGroup
            from subcategory in subcategoryGroup.DefaultIfEmpty()
            join user in _db.Users.AsNoTracking()
                on expense.CreatedBy equals user.Id
            where expense.Status == ExpenseStatus.Posted
            orderby expense.ExpenseDate descending, expense.CreatedAt descending, expense.Id descending
            select new ExpenseRowDto(
                expense.Id,
                expense.ExpenseNumber,
                expense.CategoryId,
                category.Name,
                expense.SubcategoryId,
                subcategory == null ? null : subcategory.Name,
                expense.ExpenseDate,
                expense.Amount,
                expense.PaymentMethod,
                expense.Description,
                expense.Reference,
                expense.Status,
                expense.CreatedBy,
                user.DisplayName))
            .Take(500)
            .ToListAsync(cancellationToken);
    }
}

public sealed class PartyDirectoryReadService : IPartyDirectoryReadService
{
    private readonly EdgeRetailsDbContext _db;

    public PartyDirectoryReadService(EdgeRetailsDbContext db) => _db = db;

    public async Task<IReadOnlyList<CustomerDirectoryDto>> GetCustomersAsync(
        string? search,
        int pageSize = 100,
        CancellationToken cancellationToken = default,
        string? beforeName = null,
        Guid? beforeCustomerId = null,
        bool includeInactive = false)
    {
        var take = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500);
        var query = _db.Customers.AsNoTracking()
            .Where(x => (includeInactive || x.IsActive) && !x.IsWalkIn);

        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(x =>
                x.Name.Contains(term) ||
                (x.Phone != null && x.Phone.Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(beforeName) && beforeCustomerId is Guid cursorId)
        {
            query = query.Where(x =>
                x.Name.CompareTo(beforeName) > 0 ||
                (x.Name == beforeName && x.Id.CompareTo(cursorId) > 0));
        }

        var rows = await query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(customer => new
            {
                customer.Id,
                customer.Name,
                customer.Phone,
                customer.Address,
                customer.Notes,
                customer.IsActive,
                Gross = _db.Sales
                    .Where(x => x.CustomerId == customer.Id)
                    .Select(sale => (decimal?)sale.GrandTotal)
                    .Sum() ?? 0m,
                Refunds = _db.SaleReturns
                    .Join(
                        _db.Sales,
                        saleReturn => saleReturn.SaleId,
                        sale => sale.Id,
                        (saleReturn, sale) => new { saleReturn, sale })
                    .Where(x => x.sale.CustomerId == customer.Id)
                    .Select(x => (decimal?)x.saleReturn.RefundAmount)
                    .Sum() ?? 0m,
                LastSaleAt = _db.Sales
                    .Where(sale => sale.CustomerId == customer.Id)
                    .OrderByDescending(sale => sale.CompletedAt)
                    .Select(sale => (DateTimeOffset?)sale.CompletedAt)
                    .FirstOrDefault(),
                ActiveThakaProject = _db.ThakaProjects
                    .Where(project =>
                        project.CustomerId == customer.Id &&
                        project.Status == ThakaProjectStatus.Active)
                    .OrderByDescending(project => project.StartedOn)
                    .ThenByDescending(project => project.Id)
                    .Select(project => project.ProjectName)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var customerIds = rows.Select(x => x.Id).ToArray();
        var projects = await _db.ThakaProjects.AsNoTracking()
            .Where(x => customerIds.Contains(x.CustomerId) && x.Status == ThakaProjectStatus.Active)
            .Select(project => new
            {
                project.CustomerId,
                Balance = Math.Max(0m,
                    (_db.ThakaMaterialIssues.Where(x => x.ProjectId == project.Id).Sum(x => (decimal?)x.TotalCharge) ?? 0m)
                    - (_db.ThakaMaterialReversals.Where(x => x.ProjectId == project.Id).Sum(x => (decimal?)x.ReversedCharge) ?? 0m)
                    - (_db.ThakaSettlements.Where(x => x.ProjectId == project.Id).Sum(x => (decimal?)x.SettlementDiscount) ?? 0m)
                    - (_db.ThakaPayments.Where(x => x.ProjectId == project.Id).Sum(x => (decimal?)x.Amount) ?? 0m)
                    + (_db.ThakaPaymentReversals.Where(x => x.ProjectId == project.Id).Sum(x => (decimal?)x.Amount) ?? 0m)
                    + (_db.ThakaCreditRefunds.Where(x => x.ProjectId == project.Id).Sum(x => (decimal?)x.Amount) ?? 0m))
            })
            .ToListAsync(cancellationToken);
        var metrics = projects.GroupBy(x => x.CustomerId)
            .ToDictionary(x => x.Key, x => (Count: x.Count(), Balance: x.Sum(p => p.Balance)));

        return rows.Select(x => new CustomerDirectoryDto(
            x.Id,
            x.Name,
            x.Phone,
            x.Address,
            x.Notes,
            Math.Max(0m, decimal.Round(x.Gross - x.Refunds, 2)),
            x.LastSaleAt,
            x.ActiveThakaProject,
            metrics.GetValueOrDefault(x.Id).Count,
            metrics.GetValueOrDefault(x.Id).Balance,
            x.IsActive))
            .ToArray();
    }

    public async Task<IReadOnlyList<SupplierDirectoryDto>> GetSuppliersAsync(
        string? search,
        int pageSize = 100,
        CancellationToken cancellationToken = default,
        string? beforeName = null,
        Guid? beforeSupplierId = null)
    {
        var take = Math.Clamp(pageSize <= 0 ? 50 : pageSize, 1, 500);
        var query = _db.Suppliers.AsNoTracking()
            .Where(x => x.IsActive);

        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(x =>
                EF.Functions.ILike(x.Name, "%" + term + "%") ||
                (x.Phone != null && EF.Functions.ILike(x.Phone, "%" + term + "%")) ||
                (x.City != null && EF.Functions.ILike(x.City, "%" + term + "%")));
        }

        if (!string.IsNullOrWhiteSpace(beforeName) && beforeSupplierId is Guid cursorId)
        {
            query = query.Where(x =>
                x.Name.CompareTo(beforeName) > 0 ||
                (x.Name == beforeName && x.Id.CompareTo(cursorId) > 0));
        }

        var rows = await query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Take(take)
            .Select(supplier => new
            {
                supplier.Id,
                supplier.Name,
                supplier.Phone,
                supplier.City,
                supplier.Address,
                supplier.Notes,
                Gross = _db.Purchases
                    .Where(x =>
                        x.SupplierId == supplier.Id &&
                        x.Status == PurchaseStatus.Completed)
                    .Select(purchase => (decimal?)purchase.GrandTotal)
                    .Sum() ?? 0m,
                Returned = _db.PurchaseReturns
                    .Join(
                        _db.Purchases,
                        purchaseReturn => purchaseReturn.PurchaseId,
                        purchase => purchase.Id,
                        (purchaseReturn, purchase) => new { purchaseReturn, purchase })
                    .Where(x => x.purchase.SupplierId == supplier.Id)
                    .Select(x => (decimal?)x.purchaseReturn.SupplierReturnValue)
                    .Sum() ?? 0m,
                LastPurchaseDate = _db.Purchases
                    .Where(purchase =>
                        purchase.SupplierId == supplier.Id &&
                        purchase.Status == PurchaseStatus.Completed)
                    .OrderByDescending(purchase => purchase.PurchaseDate)
                    .Select(purchase => (DateOnly?)purchase.PurchaseDate)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        return rows.Select(x => new SupplierDirectoryDto(
            x.Id,
            x.Name,
            x.Phone,
            x.City,
            x.Address,
            x.Notes,
            Math.Max(0m, decimal.Round(x.Gross - x.Returned, 2)),
            x.LastPurchaseDate))
            .ToArray();
    }
}

public sealed class ReportingReadService : IReportingReadService
{
    private readonly EdgeRetailsDbContext _db;
    private readonly IShopPeriodBoundaryService _periodBoundaryService;
    private readonly TimeZoneInfo _shopTimeZone;

    public ReportingReadService(
        EdgeRetailsDbContext db,
        IShopPeriodBoundaryService? periodBoundaryService = null,
        IShopClock? shopClock = null)
    {
        _db = db;
        var clock = shopClock ?? new ShopClock();
        _periodBoundaryService = periodBoundaryService ?? new ShopPeriodBoundaryService(clock);
        _shopTimeZone = clock.ShopTimeZone;
    }

    public async Task<ReportingSnapshotDto> GetSnapshotAsync(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken)
    {
        var (start, end, startDay, endDay, label) = ResolvePeriod(
            mode,
            selectedDate,
            selectedMonth,
            selectedYear);

        var salesBase = _db.Sales.AsNoTracking()
            .Where(x => x.CompletedAt >= start && x.CompletedAt < end);
        var salesCount = await salesBase.CountAsync(cancellationToken);
        var grossSales = await salesBase
            .Select(x => (decimal?)x.GrandTotal)
            .SumAsync(cancellationToken) ?? 0m;

        var saleItemBase =
            from item in _db.SaleItems.AsNoTracking()
            join sale in _db.Sales.AsNoTracking() on item.SaleId equals sale.Id
            where sale.CompletedAt >= start && sale.CompletedAt < end
            select item;
        var totalItems = await saleItemBase.Select(x => (decimal?)x.EnteredQuantity).SumAsync(cancellationToken) ?? 0m;
        var cogs = await saleItemBase.Select(x => (decimal?)x.TotalCostSnapshot).SumAsync(cancellationToken) ?? 0m;

        var returnsBase = _db.SaleReturns.AsNoTracking()
            .Where(x => x.CreatedAt >= start && x.CreatedAt < end);
        var refunds = await returnsBase.Select(x => (decimal?)x.RefundAmount).SumAsync(cancellationToken) ?? 0m;
        var reversedCogs = await (
            from item in _db.SaleReturnItems.AsNoTracking()
            join saleReturn in _db.SaleReturns.AsNoTracking() on item.SaleReturnId equals saleReturn.Id
            where saleReturn.CreatedAt >= start && saleReturn.CreatedAt < end
            select item.CostReversalAmount)
            .Select(x => (decimal?)x)
            .SumAsync(cancellationToken) ?? 0m;

        var expenseBase = _db.Expenses.AsNoTracking()
            .Where(x =>
                x.Status == ExpenseStatus.Posted &&
                x.ExpenseDate >= startDay &&
                x.ExpenseDate < endDay);
        var expenseTotal = await expenseBase.Select(x => (decimal?)x.Amount).SumAsync(cancellationToken) ?? 0m;
        var expenseBreakdownRaw = await expenseBase
            .GroupBy(x => x.CategoryId)
            .Select(g => new { CategoryId = g.Key, Amount = g.Sum(x => x.Amount) })
            .OrderByDescending(x => x.Amount)
            .ToListAsync(cancellationToken);
        var expenseCategories = await _db.ExpenseCategories.AsNoTracking()
            .Where(x => expenseBreakdownRaw.Select(v => v.CategoryId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);

        var purchaseTotal = await _db.Purchases.AsNoTracking()
            .Where(x =>
                x.Status == PurchaseStatus.Completed &&
                x.PurchaseDate >= startDay &&
                x.PurchaseDate < endDay)
            .Select(x => (decimal?)x.GrandTotal)
            .SumAsync(cancellationToken) ?? 0m;
        var purchaseReturnTotal = await _db.PurchaseReturns.AsNoTracking()
            .Where(x => x.CreatedAt >= start && x.CreatedAt < end)
            .Select(x => (decimal?)x.SupplierReturnValue)
            .SumAsync(cancellationToken) ?? 0m;

        var thakaIssueRaw = await _db.ThakaMaterialIssues.AsNoTracking()
            .Where(x => x.IssuedAt >= start && x.IssuedAt < end)
            .GroupBy(x => x.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count(), Amount = g.Sum(x => x.TotalCharge) })
            .OrderByDescending(x => x.Amount)
            .ToListAsync(cancellationToken);
        var thakaReversalRaw = await _db.ThakaMaterialReversals.AsNoTracking()
            .Where(x => x.ReversedAt >= start && x.ReversedAt < end)
            .GroupBy(x => x.ProjectId)
            .Select(g => new { ProjectId = g.Key, Count = g.Count(), Amount = g.Sum(x => x.ReversedCharge) })
            .ToDictionaryAsync(x => x.ProjectId, cancellationToken);
        var allProjectIds = thakaIssueRaw.Select(x => x.ProjectId)
            .Union(thakaReversalRaw.Keys)
            .Distinct()
            .ToArray();
        var projects = await _db.ThakaProjects.AsNoTracking()
            .Where(x => allProjectIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ProjectName, cancellationToken);

        var recognizedLossTotal = await _db.InventoryMovements.AsNoTracking()
            .Where(x => x.OccurredAt >= start && x.OccurredAt < end && x.RecognizedLossAmount > 0m)
            .Select(x => (decimal?)x.RecognizedLossAmount)
            .SumAsync(cancellationToken) ?? 0m;

        // D17: Canonical InventoryLossRecoveryGain from persisted Found movements linked to Missing sources
        var inventoryLossRecoveryGains = await (
            from recovery in _db.InventoryMovements.AsNoTracking()
            join source in _db.InventoryMovements.AsNoTracking()
                on recovery.ReferenceId equals source.Id
            where recovery.ReferenceType == "InventoryLossRecoveryGain" &&
                  recovery.OccurredAt >= start && recovery.OccurredAt < end
            select source.RecognizedLossAmount)
            .ToListAsync(cancellationToken);
        var inventoryLossRecoveryGainTotal = inventoryLossRecoveryGains.Sum();

        // D17: Canonical WarrantyRecoveryGain from persisted ShopWarrantyResolutionAllocations
        var warrantyRecoveryGains = await _db.ShopWarrantyResolutionAllocations.AsNoTracking()
            .Where(x => x.OccurredAt >= start && x.OccurredAt < end &&
                        x.ResolutionOutcome == WarrantyResolutionType.Credited &&
                        x.SupplierCreditAmount.HasValue &&
                        x.SupplierCreditAmount.Value > x.ActualResolvedCarryingValue)
            .Select(x => x.SupplierCreditAmount!.Value - x.ActualResolvedCarryingValue)
            .ToListAsync(cancellationToken);

        // Backwards compatibility for legacy cases prior to resolution allocation table
        var legacyWarrantyGains = await _db.ShopStockWarrantyCases.AsNoTracking()
            .Where(x => x.Status == ShopWarrantyCaseStatus.Closed &&
                        x.ResolutionType == WarrantyResolutionType.Credited &&
                        x.ClosedAt >= start && x.ClosedAt < end &&
                        (x.RecoveryDifference > 0m ||
                         ((x.SupplierCreditAmount ?? 0m) > (x.InventoryCarryingCostResolved ?? 0m))) &&
                        !_db.ShopWarrantySendAllocations.Any(s => s.CaseId == x.Id))
            .Select(x => x.RecoveryDifference ?? ((x.SupplierCreditAmount ?? 0m) - (x.InventoryCarryingCostResolved ?? 0m)))
            .ToListAsync(cancellationToken);

        var warrantyRecoveryGainTotal = warrantyRecoveryGains.Sum() + legacyWarrantyGains.Sum();

        var netSales = grossSales - refunds;
        var netCogs = cogs - reversedCogs;
        var grossProfit = netSales - netCogs;
        var netProfit = grossProfit - expenseTotal - recognizedLossTotal + inventoryLossRecoveryGainTotal + warrantyRecoveryGainTotal;
        var netPurchases = purchaseTotal - purchaseReturnTotal;
        var thakaTotal = thakaIssueRaw.Sum(x => x.Amount) - thakaReversalRaw.Values.Sum(x => x.Amount);

        var averageInvoice = salesCount == 0 ? 0m : netSales / salesCount;
        var averageGrossProfit = salesCount == 0 ? 0m : grossProfit / salesCount;
        var averageItems = salesCount == 0 ? 0m : totalItems / salesCount;

        var calendarDays = mode switch
        {
            ReportingPeriodKind.Daily => 1,
            ReportingPeriodKind.Monthly => DateTime.DaysInMonth(selectedYear, selectedMonth),
            ReportingPeriodKind.Yearly => DateTime.IsLeapYear(selectedYear) ? 366 : 365,
            _ => 1
        };

        var monthDivisor = mode == ReportingPeriodKind.Yearly ? 12 : 1;
        var trend = await BuildTrendAsync(
            mode,
            selectedDate,
            selectedMonth,
            selectedYear,
            cancellationToken);

        var breakdown = expenseBreakdownRaw
            .Select(item => new ReportingExpenseBreakdownDto(
                expenseCategories.GetValueOrDefault(item.CategoryId, "Unknown"),
                decimal.Round(item.Amount, 2),
                expenseTotal <= 0m ? 0m : item.Amount / expenseTotal))
            .ToArray();

        var issueMap = thakaIssueRaw.ToDictionary(x => x.ProjectId);
        var activity = allProjectIds
            .Select(projectId =>
            {
                var issue = issueMap.GetValueOrDefault(projectId);
                var issueCount = issue?.Count ?? 0;
                var issueAmount = issue?.Amount ?? 0m;
                var reversal = thakaReversalRaw.GetValueOrDefault(projectId);
                var reversalCount = reversal?.Count ?? 0;
                var reversalAmount = reversal?.Amount ?? 0m;
                var netMaterialValue = issueAmount - reversalAmount;
                return new ReportingThakaActivityDto(
                    projects.GetValueOrDefault(projectId, "Unknown"),
                    issueCount,
                    issueAmount,
                    reversalCount,
                    reversalAmount,
                    netMaterialValue);
            })
            .OrderByDescending(x => x.NetMaterialValue)
            .ToArray();

        return new ReportingSnapshotDto(
            mode,
            label,
            Round(netSales),
            Round(grossProfit),
            Round(expenseTotal),
            Round(netProfit),
            Round(Math.Max(0m, netPurchases)),
            Round(Math.Max(0m, thakaTotal)),
            salesCount,
            Round(averageInvoice),
            Round(averageGrossProfit),
            Round(averageItems),
            Round(netSales / calendarDays),
            Round(grossProfit / calendarDays),
            Round(netProfit / calendarDays),
            Round(expenseTotal / calendarDays),
            mode == ReportingPeriodKind.Yearly ? Round(netSales / monthDivisor) : 0m,
            mode == ReportingPeriodKind.Yearly ? Round(netProfit / monthDivisor) : 0m,
            mode == ReportingPeriodKind.Yearly ? Round(expenseTotal / monthDivisor) : 0m,
            mode == ReportingPeriodKind.Daily ? "Gross Profit" : "Net Profit",
            mode != ReportingPeriodKind.Daily,
            trend,
            breakdown,
            activity,
            Round(netCogs),
            Round(recognizedLossTotal),
            Round(inventoryLossRecoveryGainTotal),
            Round(warrantyRecoveryGainTotal));
    }
    private async Task<IReadOnlyList<ReportingTrendPointDto>> BuildTrendAsync(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken)
    {
        var (start, end, startDay, endDay, _) = ResolvePeriod(
            mode,
            selectedDate,
            selectedMonth,
            selectedYear);
        var slices = BuildTrendSlices(mode, selectedDate, selectedMonth, selectedYear);

        var salesBySlice = await SumIntoTrendSlicesAsync(_db.Sales.AsNoTracking()
            .Where(x => x.CompletedAt >= start && x.CompletedAt < end)
            .Select(x => new { Timestamp = x.CompletedAt, Amount = x.GrandTotal }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);

        var cogsBySlice = await SumIntoTrendSlicesAsync((
            from item in _db.SaleItems.AsNoTracking()
            join sale in _db.Sales.AsNoTracking() on item.SaleId equals sale.Id
            where sale.CompletedAt >= start && sale.CompletedAt < end
            select new { Timestamp = sale.CompletedAt, Amount = item.TotalCostSnapshot }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);

        var refundsBySlice = await SumIntoTrendSlicesAsync(_db.SaleReturns.AsNoTracking()
            .Where(x => x.CreatedAt >= start && x.CreatedAt < end)
            .Select(x => new { Timestamp = x.CreatedAt, Amount = x.RefundAmount }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);

        var reversedCogsBySlice = await SumIntoTrendSlicesAsync((
            from item in _db.SaleReturnItems.AsNoTracking()
            join saleReturn in _db.SaleReturns.AsNoTracking() on item.SaleReturnId equals saleReturn.Id
            where saleReturn.CreatedAt >= start && saleReturn.CreatedAt < end
            select new { Timestamp = saleReturn.CreatedAt, Amount = item.CostReversalAmount }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);

        var expenses = mode == ReportingPeriodKind.Daily
            ? new List<(DateOnly Date, decimal Amount)>()
            : await _db.Expenses.AsNoTracking()
                .Where(x =>
                    x.Status == ExpenseStatus.Posted &&
                    x.ExpenseDate >= startDay &&
                    x.ExpenseDate < endDay)
                .GroupBy(x => x.ExpenseDate)
                .Select(g => new ValueTuple<DateOnly, decimal>(g.Key, g.Sum(x => x.Amount)))
                .ToListAsync(cancellationToken);

        var lossesBySlice = await SumIntoTrendSlicesAsync(_db.InventoryMovements.AsNoTracking()
            .Where(x => x.OccurredAt >= start && x.OccurredAt < end && x.RecognizedLossAmount > 0m)
            .Select(x => new { Timestamp = x.OccurredAt, Amount = x.RecognizedLossAmount }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);

        var inventoryGainsBySlice = await SumIntoTrendSlicesAsync((
            from recovery in _db.InventoryMovements.AsNoTracking()
            join source in _db.InventoryMovements.AsNoTracking()
                on recovery.ReferenceId equals source.Id
            where recovery.ReferenceType == "InventoryLossRecoveryGain" &&
                  recovery.OccurredAt >= start && recovery.OccurredAt < end
            select new { Timestamp = recovery.OccurredAt, Amount = source.RecognizedLossAmount }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);

        var warrantyGainsBySlice = await SumIntoTrendSlicesAsync(_db.ShopWarrantyResolutionAllocations.AsNoTracking()
            .Where(x => x.OccurredAt >= start && x.OccurredAt < end &&
                        x.ResolutionOutcome == WarrantyResolutionType.Credited &&
                        x.SupplierCreditAmount.HasValue &&
                        x.SupplierCreditAmount.Value > x.ActualResolvedCarryingValue)
            .Select(x => new
            {
                Timestamp = x.OccurredAt,
                Amount = x.SupplierCreditAmount!.Value - x.ActualResolvedCarryingValue
            }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);

        var legacyWarrantyGainsBySlice = await SumIntoTrendSlicesAsync(_db.ShopStockWarrantyCases.AsNoTracking()
            .Where(x => x.Status == ShopWarrantyCaseStatus.Closed &&
                        x.ResolutionType == WarrantyResolutionType.Credited &&
                        x.ClosedAt >= start && x.ClosedAt < end &&
                        (x.RecoveryDifference > 0m || 
                         ((x.SupplierCreditAmount ?? 0m) > (x.InventoryCarryingCostResolved ?? 0m))) &&
                        !_db.ShopWarrantySendAllocations.Any(s => s.CaseId == x.Id))
            .Select(x => new
            {
                Timestamp = x.ClosedAt!.Value,
                Amount = x.RecoveryDifference ?? ((x.SupplierCreditAmount ?? 0m) - (x.InventoryCarryingCostResolved ?? 0m))
            }),
            x => x.Timestamp, x => x.Amount, slices, cancellationToken);
        var expensesByDate = expenses.ToDictionary(x => x.Item1, x => x.Item2);
        var expensesByMonth = expenses
            .GroupBy(x => x.Item1.Month)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Item2));

        var results = new List<ReportingTrendPointDto>();
        for (var index = 0; index < slices.Count; index++)
        {
            var slice = slices[index];
            var salesAmount = salesBySlice[index];
            var cogsAmount = cogsBySlice[index];
            var refundsAmount = refundsBySlice[index];
            var reversedCogsAmount = reversedCogsBySlice[index];
            var lossAmount = lossesBySlice[index];
            var inventoryGainAmount = inventoryGainsBySlice[index];
            var warrantyGainAmount = warrantyGainsBySlice[index] + legacyWarrantyGainsBySlice[index];
            decimal expenseAmount = 0m;

            if (mode == ReportingPeriodKind.Monthly)
            {
                expenseAmount = expensesByDate.GetValueOrDefault(new DateOnly(selectedYear, selectedMonth, index + 1));
            }
            else if (mode == ReportingPeriodKind.Yearly)
            {
                expenseAmount = expensesByMonth.GetValueOrDefault(index + 1);
            }

            var netSales = salesAmount - refundsAmount;
            var grossProfit = netSales - (cogsAmount - reversedCogsAmount);
            var profit = mode == ReportingPeriodKind.Daily
                ? grossProfit
                : grossProfit - expenseAmount - lossAmount + inventoryGainAmount + warrantyGainAmount;

            results.Add(new ReportingTrendPointDto(
                slice.Label,
                Round(netSales),
                Round(profit),
                Round(expenseAmount)));
        }

        return results;
    }

    private static async Task<decimal[]> SumIntoTrendSlicesAsync<T>(
        IQueryable<T> query,
        Expression<Func<T, DateTimeOffset>> timestamp,
        Expression<Func<T, decimal>> amount,
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> slices,
        CancellationToken cancellationToken)
    {
        var totals = new decimal[slices.Count];
        var source = Expression.Parameter(typeof(T), "source");
        var timestampBody = new ReplaceParameterVisitor(timestamp.Parameters[0], source).Visit(timestamp.Body)!;
        var amountBody = new ReplaceParameterVisitor(amount.Parameters[0], source).Visit(amount.Body)!;
        var amountSelector = Expression.Lambda<Func<T, decimal>>(amountBody, source);
        Expression sliceIndex = Expression.Constant(-1);
        for (var index = slices.Count - 1; index >= 0; index--)
        {
            var start = Expression.Constant(slices[index].Start);
            var end = Expression.Constant(slices[index].End);
            var matches = Expression.AndAlso(
                Expression.GreaterThanOrEqual(timestampBody, start),
                Expression.LessThan(timestampBody, end));
            sliceIndex = Expression.Condition(matches, Expression.Constant(index), sliceIndex);
        }

        var indexSelector = Expression.Lambda<Func<T, int>>(sliceIndex, source);
        var groups = query.GroupBy(indexSelector);
        var group = Expression.Parameter(typeof(IGrouping<int, T>), "group");
        var asQueryableMethod = typeof(Queryable).GetMethods().Single(method =>
            method.Name == nameof(Queryable.AsQueryable) && method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 1).MakeGenericMethod(typeof(T));
        var asQueryable = Expression.Call(asQueryableMethod, group);
        var sum = Expression.Call(typeof(Queryable), nameof(Queryable.Sum), [typeof(T)], asQueryable, amountSelector);
        var totalConstructor = typeof(TrendSliceTotal).GetConstructor([typeof(int), typeof(decimal)])!;
        var aggregateSelector = Expression.Lambda<Func<IGrouping<int, T>, TrendSliceTotal>>(
            Expression.New(totalConstructor, Expression.Property(group, nameof(IGrouping<int, T>.Key)), sum), group);
        var aggregated = await groups.Select(aggregateSelector).ToListAsync(cancellationToken);
        foreach (var row in aggregated)
        {
            if (row.Index < 0)
            {
                throw new InvalidOperationException("A trend event is outside the configured shop-time slices.");
            }
            totals[row.Index] = row.Amount;
        }

        return totals;
    }

    private sealed record TrendSliceTotal(int Index, decimal Amount);

    private sealed class ReplaceParameterVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End, string Label)> BuildTrendSlices(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear)
    {
        var slices = new List<(DateTimeOffset Start, DateTimeOffset End, string Label)>();
        if (mode == ReportingPeriodKind.Daily)
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var localStart = selectedDate.ToDateTime(new TimeOnly(hour, 0));
                if (_shopTimeZone.IsInvalidTime(localStart))
                {
                    continue;
                }
                var offsets = _shopTimeZone.IsAmbiguousTime(localStart)
                    ? _shopTimeZone.GetAmbiguousTimeOffsets(localStart).OrderByDescending(x => x).ToArray()
                    : [_shopTimeZone.GetUtcOffset(localStart)];
                for (var occurrence = 0; occurrence < offsets.Length; occurrence++)
                {
                    var offset = offsets[occurrence];
                    var startUtc = new DateTimeOffset(localStart, offset).ToUniversalTime();
                    var localEnd = localStart.AddHours(1);
                    var endUtc = new DateTimeOffset(localEnd, offset).ToUniversalTime();
                    var label = hour % 3 == 0 || offsets.Length > 1
                        ? localStart.ToString("htt", CultureInfo.InvariantCulture).ToLowerInvariant()
                        : string.Empty;
                    if (offsets.Length > 1)
                    {
                        label += occurrence == 0 ? "¹" : "²";
                    }
                    slices.Add((startUtc, endUtc, label));
                }
            }
            return slices.OrderBy(x => x.Start).ToArray();
        }

        if (mode == ReportingPeriodKind.Monthly)
        {
            var days = DateTime.DaysInMonth(selectedYear, selectedMonth);
            for (var day = 1; day <= days; day++)
            {
                var date = new DateOnly(selectedYear, selectedMonth, day);
                var (startUtc, endUtc) = _periodBoundaryService.GetDailyUtcBoundary(date);
                slices.Add((
                    startUtc,
                    endUtc,
                    day == 1 || day == days || day % 5 == 0
                        ? day.ToString()
                        : string.Empty));
            }
            return slices;
        }

        for (var month = 1; month <= 12; month++)
        {
            var (startUtc, endUtc) = _periodBoundaryService.GetMonthlyUtcBoundary(selectedYear, month);
            var start = new DateTime(selectedYear, month, 1);
            slices.Add((startUtc, endUtc, start.ToString("MMM")));
        }

        return slices;
    }

    private (DateTimeOffset Start, DateTimeOffset End, DateOnly StartDay, DateOnly EndDay, string Label) ResolvePeriod(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear)
    {
        switch (mode)
        {
            case ReportingPeriodKind.Daily:
            {
                var (startUtc, endUtc) = _periodBoundaryService.GetDailyUtcBoundary(selectedDate);
                return (startUtc, endUtc, selectedDate, selectedDate.AddDays(1), selectedDate.ToString("dd MMM yyyy"));
            }
            case ReportingPeriodKind.Monthly:
            {
                var (startUtc, endUtc) = _periodBoundaryService.GetMonthlyUtcBoundary(selectedYear, selectedMonth);
                var startDay = new DateOnly(selectedYear, selectedMonth, 1);
                return (startUtc, endUtc, startDay, startDay.AddMonths(1), new DateTime(selectedYear, selectedMonth, 1).ToString("MMMM yyyy"));
            }
            case ReportingPeriodKind.Yearly:
            {
                var (startUtc, endUtc) = _periodBoundaryService.GetYearlyUtcBoundary(selectedYear);
                var startDay = new DateOnly(selectedYear, 1, 1);
                return (startUtc, endUtc, startDay, startDay.AddYears(1), selectedYear.ToString(CultureInfo.InvariantCulture));
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
