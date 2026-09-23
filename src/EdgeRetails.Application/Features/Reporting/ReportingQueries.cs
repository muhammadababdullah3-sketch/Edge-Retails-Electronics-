namespace EdgeRetails.Application.Features.Reporting;

public enum ReportingPeriodKind
{
    Daily = 1,
    Monthly = 2,
    Yearly = 3
}

public sealed record ReportingTrendPointDto(
    string Label,
    decimal Sales,
    decimal Profit,
    decimal Expenses);

public sealed record ReportingExpenseBreakdownDto(
    string Category,
    decimal Amount,
    decimal Share);

public sealed record ReportingThakaActivityDto(
    string ProjectName,
    int IssueCount,
    decimal MaterialValue);

public sealed record ReportingSnapshotDto(
    ReportingPeriodKind Mode,
    string PeriodLabel,
    decimal NetSales,
    decimal GrossProfit,
    decimal Expenses,
    decimal NetProfit,
    decimal Purchases,
    decimal ThakaMaterial,
    int SalesCount,
    decimal AverageInvoiceValue,
    decimal AverageGrossProfitPerInvoice,
    decimal AverageItemsPerInvoice,
    decimal AverageDailySales,
    decimal AverageDailyGrossProfit,
    decimal AverageDailyNetProfit,
    decimal AverageDailyExpenses,
    decimal AverageMonthlySales,
    decimal AverageMonthlyNetProfit,
    decimal AverageMonthlyExpenses,
    string ProfitSeriesLabel,
    bool ShowExpenseSeries,
    IReadOnlyList<ReportingTrendPointDto> Trend,
    IReadOnlyList<ReportingExpenseBreakdownDto> ExpenseBreakdown,
    IReadOnlyList<ReportingThakaActivityDto> ThakaActivity);

public interface IReportingReadService
{
    Task<ReportingSnapshotDto> GetSnapshotAsync(
        ReportingPeriodKind mode,
        DateOnly selectedDate,
        int selectedMonth,
        int selectedYear,
        CancellationToken cancellationToken);
}
