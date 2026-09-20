using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Services;

public enum ReportPeriodMode
{
    Daily,
    Monthly,
    Yearly
}

public sealed class ReportTrendPoint
{
    public required string Label { get; init; }
    public decimal Sales { get; init; }
    public decimal Profit { get; init; }
    public decimal Expenses { get; init; }
}

public sealed class ReportExpenseBreakdownItem
{
    public required string Category { get; init; }
    public decimal Amount { get; init; }
    public decimal Share { get; init; }
    public string AmountDisplay => $"Rs. {Amount:N0}";
    public string ShareDisplay => $"{Share:P0}";
}

public sealed class ReportThakaActivityItem
{
    public required string ProjectName { get; init; }
    public int IssueCount { get; init; }
    public decimal MaterialValue { get; init; }
    public string IssueCountDisplay => $"{IssueCount} {(IssueCount == 1 ? "issue" : "issues")}";
    public string MaterialValueDisplay => $"Rs. {MaterialValue:N0}";
}

public sealed class ReportSnapshot
{
    public ReportPeriodMode Mode { get; init; }
    public required string PeriodLabel { get; init; }

    public decimal NetSales { get; init; }
    public decimal GrossProfit { get; init; }
    public decimal Expenses { get; init; }
    public decimal NetProfit { get; init; }
    public decimal Purchases { get; init; }
    public decimal ThakaMaterial { get; init; }

    public int SalesCount { get; init; }
    public decimal AverageInvoiceValue { get; init; }
    public decimal AverageGrossProfitPerInvoice { get; init; }
    public decimal AverageItemsPerInvoice { get; init; }

    public decimal AverageDailySales { get; init; }
    public decimal AverageDailyGrossProfit { get; init; }
    public decimal AverageDailyNetProfit { get; init; }
    public decimal AverageDailyExpenses { get; init; }

    public decimal AverageMonthlySales { get; init; }
    public decimal AverageMonthlyNetProfit { get; init; }
    public decimal AverageMonthlyExpenses { get; init; }

    public required string ProfitSeriesLabel { get; init; }
    public bool ShowExpenseSeries { get; init; }

    public required IReadOnlyList<ReportTrendPoint> Trend { get; init; }
    public required IReadOnlyList<ReportExpenseBreakdownItem> ExpenseBreakdown { get; init; }
    public required IReadOnlyList<ReportThakaActivityItem> ThakaActivity { get; init; }
}

public sealed class DemoReportingService
{
    private static readonly Lazy<DemoReportingService> s_instance =
        new(() => new DemoReportingService());

    private readonly ITransactionService _transactions = DemoTransactionService.Instance;
    private readonly DemoPurchaseInventoryService _purchases = DemoPurchaseInventoryService.Instance;
    private readonly DemoBusinessDirectoryService _directory = DemoBusinessDirectoryService.Instance;
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;

    public static DemoReportingService Instance => s_instance.Value;

    public event EventHandler? StateChanged;

    private DemoReportingService()
    {
        _transactions.TransactionRecorded += (_, _) => RaiseStateChanged();
        _transactions.ReturnRecorded += (_, _) => RaiseStateChanged();
        _purchases.StateChanged += (_, _) => RaiseStateChanged();
        _directory.StateChanged += (_, _) => RaiseStateChanged();
        _retailState.StateChanged += (_, _) => RaiseStateChanged();
    }

    public ReportSnapshot GetSnapshot(
        ReportPeriodMode mode,
        DateTime selectedDate,
        int selectedMonth,
        int selectedYear)
    {
        var (start, end, periodLabel) = ResolvePeriod(mode, selectedDate, selectedMonth, selectedYear);
        var allTransactions = _transactions.GetAllTransactions();
        var allReturns = _transactions.GetAllReturns();

        var periodTransactions = allTransactions
            .Where(record => record.Timestamp >= start && record.Timestamp < end)
            .ToArray();
        var periodReturns = allReturns
            .Where(record => record.Timestamp >= start && record.Timestamp < end)
            .ToArray();

        var (netSales, grossProfit) = CalculateLocalFinancials(
            periodTransactions,
            periodReturns,
            allTransactions);

        var expenses = _directory.Expenses
            .Where(record => record.Date >= start && record.Date < end)
            .ToArray();
        var expenseTotal = expenses.Sum(record => record.Amount);
        var netProfit = Math.Round(grossProfit - expenseTotal, 2);

        var purchases = _purchases.Purchases
            .Where(record => record.Date >= start && record.Date < end)
            .Sum(record => record.Total);

        var thakaEntries = GetThakaEntries(start, end);
        var thakaTotal = thakaEntries.Sum(item => item.Entry.TotalValue);

        var salesCount = periodTransactions.Length;
        var averageInvoice = salesCount > 0 ? netSales / salesCount : 0m;
        var averageGrossProfitPerInvoice = salesCount > 0 ? grossProfit / salesCount : 0m;
        var averageItems = salesCount > 0
            ? periodTransactions.Sum(record => record.Items.Sum(item => item.Quantity)) / salesCount
            : 0m;

        var calendarDays = mode switch
        {
            ReportPeriodMode.Daily => 1,
            ReportPeriodMode.Monthly => DateTime.DaysInMonth(selectedYear, selectedMonth),
            ReportPeriodMode.Yearly => DateTime.IsLeapYear(selectedYear) ? 366 : 365,
            _ => 1
        };

        var averageDailySales = netSales / calendarDays;
        var averageDailyGrossProfit = grossProfit / calendarDays;
        var averageDailyNetProfit = netProfit / calendarDays;
        var averageDailyExpenses = expenseTotal / calendarDays;

        var selectedMonths = ResolveAverageMonthDivisor(mode, selectedYear);
        var averageMonthlySales = mode == ReportPeriodMode.Yearly ? netSales / selectedMonths : 0m;
        var averageMonthlyNetProfit = mode == ReportPeriodMode.Yearly ? netProfit / selectedMonths : 0m;
        var averageMonthlyExpenses = mode == ReportPeriodMode.Yearly ? expenseTotal / selectedMonths : 0m;

        return new ReportSnapshot
        {
            Mode = mode,
            PeriodLabel = periodLabel,
            NetSales = Math.Round(netSales, 2),
            GrossProfit = Math.Round(grossProfit, 2),
            Expenses = Math.Round(expenseTotal, 2),
            NetProfit = Math.Round(netProfit, 2),
            Purchases = Math.Round(purchases, 2),
            ThakaMaterial = Math.Round(thakaTotal, 2),
            SalesCount = salesCount,
            AverageInvoiceValue = Math.Round(averageInvoice, 2),
            AverageGrossProfitPerInvoice = Math.Round(averageGrossProfitPerInvoice, 2),
            AverageItemsPerInvoice = Math.Round(averageItems, 2),
            AverageDailySales = Math.Round(averageDailySales, 2),
            AverageDailyGrossProfit = Math.Round(averageDailyGrossProfit, 2),
            AverageDailyNetProfit = Math.Round(averageDailyNetProfit, 2),
            AverageDailyExpenses = Math.Round(averageDailyExpenses, 2),
            AverageMonthlySales = Math.Round(averageMonthlySales, 2),
            AverageMonthlyNetProfit = Math.Round(averageMonthlyNetProfit, 2),
            AverageMonthlyExpenses = Math.Round(averageMonthlyExpenses, 2),
            ProfitSeriesLabel = mode == ReportPeriodMode.Daily ? "Gross Profit" : "Net Profit",
            ShowExpenseSeries = mode != ReportPeriodMode.Daily,
            Trend = BuildTrend(mode, start, selectedMonth, selectedYear, allTransactions, allReturns),
            ExpenseBreakdown = BuildExpenseBreakdown(expenses),
            ThakaActivity = BuildThakaActivity(thakaEntries)
        };
    }
    private IReadOnlyList<ReportTrendPoint> BuildTrend(
        ReportPeriodMode mode,
        DateTime periodStart,
        int selectedMonth,
        int selectedYear,
        IReadOnlyList<SaleTransactionRecord> allTransactions,
        IReadOnlyList<SaleReturnRecord> allReturns)
    {
        return mode switch
        {
            ReportPeriodMode.Daily => BuildDailyTrend(periodStart, allTransactions, allReturns),
            ReportPeriodMode.Monthly => BuildMonthlyTrend(selectedMonth, selectedYear, allTransactions, allReturns),
            ReportPeriodMode.Yearly => BuildYearlyTrend(selectedYear, allTransactions, allReturns),
            _ => []
        };
    }

    private IReadOnlyList<ReportTrendPoint> BuildDailyTrend(
        DateTime day,
        IReadOnlyList<SaleTransactionRecord> allTransactions,
        IReadOnlyList<SaleReturnRecord> allReturns)
    {
        var points = new List<ReportTrendPoint>(24);
        for (var hour = 0; hour < 24; hour++)
        {
            var start = day.Date.AddHours(hour);
            var end = start.AddHours(1);
            var sales = allTransactions
                .Where(record => record.Timestamp >= start && record.Timestamp < end)
                .ToArray();
            var returns = allReturns
                .Where(record => record.Timestamp >= start && record.Timestamp < end)
                .ToArray();
            var (netSales, grossProfit) = CalculateLocalFinancials(sales, returns, allTransactions);

            points.Add(new ReportTrendPoint
            {
                Label = hour % 3 == 0 ? start.ToString("htt").ToLowerInvariant() : string.Empty,
                Sales = Math.Round(netSales, 2),
                Profit = Math.Round(grossProfit, 2),
                Expenses = 0m
            });
        }

        return points;
    }

    private IReadOnlyList<ReportTrendPoint> BuildMonthlyTrend(
        int month,
        int year,
        IReadOnlyList<SaleTransactionRecord> allTransactions,
        IReadOnlyList<SaleReturnRecord> allReturns)
    {
        var days = DateTime.DaysInMonth(year, month);
        var points = new List<ReportTrendPoint>(days);

        for (var day = 1; day <= days; day++)
        {
            var start = new DateTime(year, month, day);
            var end = start.AddDays(1);
            var sales = allTransactions
                .Where(record => record.Timestamp >= start && record.Timestamp < end)
                .ToArray();
            var returns = allReturns
                .Where(record => record.Timestamp >= start && record.Timestamp < end)
                .ToArray();
            var (netSales, grossProfit) = CalculateLocalFinancials(sales, returns, allTransactions);
            var expenses = _directory.Expenses
                .Where(record => record.Date >= start && record.Date < end)
                .Sum(record => record.Amount);

            points.Add(new ReportTrendPoint
            {
                Label = day == 1 || day == days || day % 5 == 0 ? day.ToString() : string.Empty,
                Sales = Math.Round(netSales, 2),
                Profit = Math.Round(grossProfit - expenses, 2),
                Expenses = Math.Round(expenses, 2)
            });
        }

        return points;
    }

    private IReadOnlyList<ReportTrendPoint> BuildYearlyTrend(
        int year,
        IReadOnlyList<SaleTransactionRecord> allTransactions,
        IReadOnlyList<SaleReturnRecord> allReturns)
    {
        var points = new List<ReportTrendPoint>(12);

        for (var month = 1; month <= 12; month++)
        {
            var start = new DateTime(year, month, 1);
            var end = start.AddMonths(1);
            var sales = allTransactions
                .Where(record => record.Timestamp >= start && record.Timestamp < end)
                .ToArray();
            var returns = allReturns
                .Where(record => record.Timestamp >= start && record.Timestamp < end)
                .ToArray();
            var (netSales, grossProfit) = CalculateLocalFinancials(sales, returns, allTransactions);
            var expenses = _directory.Expenses
                .Where(record => record.Date >= start && record.Date < end)
                .Sum(record => record.Amount);

            points.Add(new ReportTrendPoint
            {
                Label = start.ToString("MMM"),
                Sales = Math.Round(netSales, 2),
                Profit = Math.Round(grossProfit - expenses, 2),
                Expenses = Math.Round(expenses, 2)
            });
        }

        return points;
    }

    private static (decimal NetSales, decimal GrossProfit) CalculateLocalFinancials(
        IReadOnlyCollection<SaleTransactionRecord> periodTransactions,
        IReadOnlyCollection<SaleReturnRecord> periodReturns,
        IReadOnlyList<SaleTransactionRecord> allTransactions)
    {
        var grossSales = periodTransactions.Sum(record => record.TotalAmount);
        var refunds = periodReturns.Sum(record => record.TotalRefundAmount);
        var netSales = grossSales - refunds;

        var cogs = periodTransactions.Sum(record =>
            record.Items.Sum(item => item.TotalCostSnapshot));

        var reversedCogs = periodReturns
            .Where(record => record.Disposition == SaleReturnDisposition.RestockSellable)
            .Sum(record => CalculateRestockedReturnCost(record, allTransactions));

        var netCogs = cogs - reversedCogs;
        return (
            Math.Round(netSales, 2),
            Math.Round(netSales - netCogs, 2));
    }

    private static decimal CalculateRestockedReturnCost(
        SaleReturnRecord returnRecord,
        IReadOnlyList<SaleTransactionRecord> allTransactions)
    {
        var original = allTransactions.FirstOrDefault(record =>
            string.Equals(
                record.InvoiceNumber,
                returnRecord.InvoiceNumber,
                StringComparison.OrdinalIgnoreCase));
        if (original is null)
        {
            return 0m;
        }

        decimal reversedCost = 0m;
        foreach (var returnItem in returnRecord.Items)
        {
            var originalItem = original.Items.FirstOrDefault(item =>
                string.Equals(item.ProductId, returnItem.ProductId, StringComparison.OrdinalIgnoreCase) ||
                (!string.IsNullOrWhiteSpace(returnItem.Sku) &&
                 string.Equals(item.Sku, returnItem.Sku, StringComparison.OrdinalIgnoreCase)));
            if (originalItem is null)
            {
                continue;
            }

            var eligibleQty = Math.Min(returnItem.Quantity, originalItem.Quantity);
            reversedCost += eligibleQty * originalItem.UnitCostSnapshot;
        }

        return Math.Round(reversedCost, 2);
    }
    private IReadOnlyList<(string ProjectName, MaterialLedgerEntry Entry)> GetThakaEntries(
        DateTime start,
        DateTime end)
    {
        var entries = new List<(string ProjectName, MaterialLedgerEntry Entry)>();

        foreach (var project in _retailState.ThakaProjects)
        {
            foreach (var entry in _retailState.GetMaterialLedger(project))
            {
                if (entry.Date >= start && entry.Date < end)
                {
                    entries.Add((project.ProjectName, entry));
                }
            }
        }

        return entries;
    }

    private static IReadOnlyList<ReportExpenseBreakdownItem> BuildExpenseBreakdown(
        IReadOnlyCollection<ExpenseRecord> expenses)
    {
        var total = expenses.Sum(item => item.Amount);
        return [.. expenses
            .GroupBy(item => item.Category)
            .Select(group => new ReportExpenseBreakdownItem
            {
                Category = group.Key,
                Amount = Math.Round(group.Sum(item => item.Amount), 2),
                Share = total > 0m ? group.Sum(item => item.Amount) / total : 0m
            })
            .OrderByDescending(item => item.Amount)];
    }

    private static IReadOnlyList<ReportThakaActivityItem> BuildThakaActivity(
        IReadOnlyCollection<(string ProjectName, MaterialLedgerEntry Entry)> entries) =>
        [.. entries
            .GroupBy(item => item.ProjectName)
            .Select(group => new ReportThakaActivityItem
            {
                ProjectName = group.Key,
                IssueCount = group.Count(),
                MaterialValue = Math.Round(group.Sum(item => item.Entry.TotalValue), 2)
            })
            .OrderByDescending(item => item.MaterialValue)];

    private static (DateTime Start, DateTime End, string Label) ResolvePeriod(
        ReportPeriodMode mode,
        DateTime selectedDate,
        int selectedMonth,
        int selectedYear)
    {
        return mode switch
        {
            ReportPeriodMode.Daily => (
                selectedDate.Date,
                selectedDate.Date.AddDays(1),
                selectedDate.ToString("dd MMM yyyy")),

            ReportPeriodMode.Monthly => (
                new DateTime(selectedYear, selectedMonth, 1),
                new DateTime(selectedYear, selectedMonth, 1).AddMonths(1),
                new DateTime(selectedYear, selectedMonth, 1).ToString("MMMM yyyy")),

            ReportPeriodMode.Yearly => (
                new DateTime(selectedYear, 1, 1),
                new DateTime(selectedYear + 1, 1, 1),
                selectedYear.ToString()),

            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static int ResolveAverageMonthDivisor(
        ReportPeriodMode mode,
        int selectedYear)
    {
        if (mode != ReportPeriodMode.Yearly)
        {
            return 1;
        }

        if (selectedYear == DateTime.Today.Year)
        {
            return Math.Max(1, DateTime.Today.Month);
        }

        return 12;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
