using System.Globalization;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Application.Features.Parties;
using EdgeRetails.Application.Features.Reporting;
using EdgeRetails.Domain.Finance;
using EdgeRetails.Domain.Purchasing;
using EdgeRetails.Domain.Thaka;
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
            orderby expense.ExpenseDate descending, expense.CreatedAt descending
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
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(pageSize, 1, 200);
        var query = _db.Customers.AsNoTracking()
            .Where(x => x.IsActive && !x.IsWalkIn);

        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(x =>
                x.Name.Contains(term) ||
                (x.Phone != null && x.Phone.Contains(term)));
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

        return rows.Select(x => new CustomerDirectoryDto(
            x.Id,
            x.Name,
            x.Phone,
            x.Address,
            x.Notes,
            Math.Max(0m, decimal.Round(x.Gross - x.Refunds, 2)),
            x.LastSaleAt,
            x.ActiveThakaProject))
            .ToArray();
    }

    public async Task<IReadOnlyList<SupplierDirectoryDto>> GetSuppliersAsync(
        string? search,
        int pageSize = 100,
        CancellationToken cancellationToken = default)
    {
        var take = Math.Clamp(pageSize, 1, 200);
        var query = _db.Suppliers.AsNoTracking()
            .Where(x => x.IsActive);

        var term = search?.Trim();
        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(x =>
                x.Name.Contains(term) ||
                (x.Phone != null && x.Phone.Contains(term)) ||
                (x.City != null && x.City.Contains(term)));
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

    public ReportingReadService(EdgeRetailsDbContext db) => _db = db;

    public async Task<ReportingSnapshotDto> GetSnapshotAsync(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken)
    {
        var (startLocal, endLocal, label) = ResolvePeriod(
            mode,
            selectedDate,
            selectedMonth,
            selectedYear);
        var start = ToOffset(startLocal);
        var end = ToOffset(endLocal);

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
                x.ExpenseDate >= DateOnly.FromDateTime(startLocal) &&
                x.ExpenseDate < DateOnly.FromDateTime(endLocal));
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
                x.PurchaseDate >= DateOnly.FromDateTime(startLocal) &&
                x.PurchaseDate < DateOnly.FromDateTime(endLocal))
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
            .Select(g => new { ProjectId = g.Key, Amount = g.Sum(x => x.ReversedCharge) })
            .ToDictionaryAsync(x => x.ProjectId, x => x.Amount, cancellationToken);
        var projects = await _db.ThakaProjects.AsNoTracking()
            .Where(x => thakaIssueRaw.Select(v => v.ProjectId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.ProjectName, cancellationToken);

        var netSales = grossSales - refunds;
        var grossProfit = netSales - (cogs - reversedCogs);
        var netProfit = grossProfit - expenseTotal;
        var netPurchases = purchaseTotal - purchaseReturnTotal;
        var thakaTotal = thakaIssueRaw.Sum(x => x.Amount) - thakaReversalRaw.Values.Sum();

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

        var activity = thakaIssueRaw
            .Select(item => new ReportingThakaActivityDto(
                projects.GetValueOrDefault(item.ProjectId, "Unknown"),
                item.Count,
                Math.Max(0m, item.Amount - thakaReversalRaw.GetValueOrDefault(item.ProjectId))))
            .OrderByDescending(x => x.MaterialValue)
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
            activity);
    }
    private async Task<IReadOnlyList<ReportingTrendPointDto>> BuildTrendAsync(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken)
    {
        var (startLocal, endLocal, _) = ResolvePeriod(
            mode,
            selectedDate,
            selectedMonth,
            selectedYear);
        var start = ToOffset(startLocal);
        var end = ToOffset(endLocal);

        var sales = await _db.Sales.AsNoTracking()
            .Where(x => x.CompletedAt >= start && x.CompletedAt < end)
            .GroupBy(x => new { Date = x.CompletedAt.Date, x.CompletedAt.Hour })
            .Select(g => new { g.Key.Date, g.Key.Hour, Amount = g.Sum(x => x.GrandTotal) })
            .ToListAsync(cancellationToken);

        var saleItems = await (
            from item in _db.SaleItems.AsNoTracking()
            join sale in _db.Sales.AsNoTracking() on item.SaleId equals sale.Id
            where sale.CompletedAt >= start && sale.CompletedAt < end
            group item by new { Date = sale.CompletedAt.Date, Hour = sale.CompletedAt.Hour } into g
            select new { g.Key.Date, g.Key.Hour, Amount = g.Sum(x => x.TotalCostSnapshot) })
            .ToListAsync(cancellationToken);

        var returns = await _db.SaleReturns.AsNoTracking()
            .Where(x => x.CreatedAt >= start && x.CreatedAt < end)
            .GroupBy(x => new { Date = x.CreatedAt.Date, x.CreatedAt.Hour })
            .Select(g => new { g.Key.Date, g.Key.Hour, Amount = g.Sum(x => x.RefundAmount) })
            .ToListAsync(cancellationToken);

        var returnItems = await (
            from item in _db.SaleReturnItems.AsNoTracking()
            join saleReturn in _db.SaleReturns.AsNoTracking() on item.SaleReturnId equals saleReturn.Id
            where saleReturn.CreatedAt >= start && saleReturn.CreatedAt < end
            group item by new { Date = saleReturn.CreatedAt.Date, Hour = saleReturn.CreatedAt.Hour } into g
            select new { g.Key.Date, g.Key.Hour, Amount = g.Sum(x => x.CostReversalAmount) })
            .ToListAsync(cancellationToken);

        var expenses = mode == ReportingPeriodKind.Daily
            ? new List<(DateTime Date, decimal Amount)>()
            : await _db.Expenses.AsNoTracking()
                .Where(x =>
                    x.Status == ExpenseStatus.Posted &&
                    x.ExpenseDate >= DateOnly.FromDateTime(startLocal) &&
                    x.ExpenseDate < DateOnly.FromDateTime(endLocal))
                .GroupBy(x => x.ExpenseDate)
                .Select(g => new ValueTuple<DateTime, decimal>(
                    g.Key.ToDateTime(TimeOnly.MinValue),
                    g.Sum(x => x.Amount)))
                .ToListAsync(cancellationToken);

        var salesMap = sales
            .GroupBy(x => new { x.Date, x.Hour })
            .ToDictionary(g => (g.Key.Date, g.Key.Hour), g => g.Sum(x => x.Amount));
        var itemMap = saleItems
            .GroupBy(x => new { x.Date, x.Hour })
            .ToDictionary(g => (g.Key.Date, g.Key.Hour), g => g.Sum(x => x.Amount));
        var returnMap = returns
            .GroupBy(x => new { x.Date, x.Hour })
            .ToDictionary(g => (g.Key.Date, g.Key.Hour), g => g.Sum(x => x.Amount));
        var returnItemMap = returnItems
            .GroupBy(x => new { x.Date, x.Hour })
            .ToDictionary(g => (g.Key.Date, g.Key.Hour), g => g.Sum(x => x.Amount));
        var expenseMap = expenses.ToDictionary(x => DateOnly.FromDateTime(x.Item1), x => x.Item2);

        var results = new List<ReportingTrendPointDto>();
        foreach (var slice in BuildTrendSlices(mode, selectedDate, selectedMonth, selectedYear))
        {
            var days = Enumerable.Range(
                    0,
                    Math.Max(1, (int)(slice.End - slice.Start).TotalDays))
                .Select(offset => slice.Start.Date.AddDays(offset))
                .ToArray();

            decimal salesAmount = 0m;
            decimal cogsAmount = 0m;
            decimal refundsAmount = 0m;
            decimal reversedCogsAmount = 0m;
            decimal expenseAmount = 0m;

            foreach (var day in days)
            {
                var maxHour = mode == ReportingPeriodKind.Daily ? 23 : 23;
                for (var hour = 0; hour <= maxHour; hour++)
                {
                    salesAmount += salesMap.GetValueOrDefault((day, hour));
                    cogsAmount += itemMap.GetValueOrDefault((day, hour));
                    refundsAmount += returnMap.GetValueOrDefault((day, hour));
                    reversedCogsAmount += returnItemMap.GetValueOrDefault((day, hour));
                }

                if (mode != ReportingPeriodKind.Daily)
                {
                    expenseAmount += expenseMap.GetValueOrDefault(DateOnly.FromDateTime(day));
                }
            }

            var netSales = salesAmount - refundsAmount;
            var grossProfit = netSales - (cogsAmount - reversedCogsAmount);
            var profit = mode == ReportingPeriodKind.Daily
                ? grossProfit
                : grossProfit - expenseAmount;

            results.Add(new ReportingTrendPointDto(
                slice.Label,
                Round(netSales),
                Round(profit),
                Round(expenseAmount)));
        }

        return results;
    }

    private static IReadOnlyList<(DateTime Start, DateTime End, string Label)> BuildTrendSlices(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear)
    {
        var slices = new List<(DateTime Start, DateTime End, string Label)>();
        if (mode == ReportingPeriodKind.Daily)
        {
            var day = selectedDate.ToDateTime(TimeOnly.MinValue);
            for (var hour = 0; hour < 24; hour++)
            {
                var start = day.AddHours(hour);
                slices.Add((
                    start,
                    start.AddHours(1),
                    hour % 3 == 0 ? start.ToString("htt").ToLowerInvariant() : string.Empty));
            }
            return slices;
        }

        if (mode == ReportingPeriodKind.Monthly)
        {
            var days = DateTime.DaysInMonth(selectedYear, selectedMonth);
            for (var day = 1; day <= days; day++)
            {
                var start = new DateTime(selectedYear, selectedMonth, day);
                slices.Add((
                    start,
                    start.AddDays(1),
                    day == 1 || day == days || day % 5 == 0
                        ? day.ToString()
                        : string.Empty));
            }
            return slices;
        }

        for (var month = 1; month <= 12; month++)
        {
            var start = new DateTime(selectedYear, month, 1);
            slices.Add((start, start.AddMonths(1), start.ToString("MMM")));
        }

        return slices;
    }

    private static (DateTime Start, DateTime End, string Label) ResolvePeriod(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear) =>
        mode switch
        {
            ReportingPeriodKind.Daily => (
                selectedDate.ToDateTime(TimeOnly.MinValue),
                selectedDate.AddDays(1).ToDateTime(TimeOnly.MinValue),
                selectedDate.ToString("dd MMM yyyy")),
            ReportingPeriodKind.Monthly => (
                new DateTime(selectedYear, selectedMonth, 1),
                new DateTime(selectedYear, selectedMonth, 1).AddMonths(1),
                new DateTime(selectedYear, selectedMonth, 1).ToString("MMMM yyyy")),
            ReportingPeriodKind.Yearly => (
                new DateTime(selectedYear, 1, 1),
                new DateTime(selectedYear + 1, 1, 1),
                selectedYear.ToString(CultureInfo.InvariantCulture)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

    private static DateTimeOffset ToOffset(DateTime local) =>
        new(local, TimeZoneInfo.Local.GetUtcOffset(local));

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
