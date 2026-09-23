using EdgeRetails.Application.Abstractions;
using EdgeRetails.Desktop.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendDashboardSnapshot(
    decimal? TodaySales,
    decimal? TodayProfit,
    decimal? Expenses,
    decimal? TodayThakaMaterial,
    int? ActiveThakaCount,
    decimal? ActiveThakaOutstanding,
    IReadOnlyList<ThakaProjectListItemViewModel> ActiveProjects,
    bool? IsDatabaseConnected,
    string DatabaseStatusText,
    bool? IsBackupUpToDate,
    string BackupStatusText,
    IReadOnlyList<string> Issues);

public interface IBackendDashboardService
{
    Task<BackendDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default);
}

public sealed class BackendDashboardService : IBackendDashboardService
{
    private readonly IBackendBusinessOperationsService _businessOperations;
    private readonly IBackendThakaService _thaka;
    private readonly IServiceScopeFactory _scopeFactory;

    public BackendDashboardService(
        IBackendBusinessOperationsService businessOperations,
        IBackendThakaService thaka,
        IServiceScopeFactory scopeFactory)
    {
        _businessOperations = businessOperations;
        _thaka = thaka;
        _scopeFactory = scopeFactory;
    }

    public async Task<BackendDashboardSnapshot> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        decimal? todaySales = null;
        decimal? todayProfit = null;
        decimal? expenses = null;
        decimal? todayThakaMaterial = null;
        int? activeThakaCount = null;
        decimal? activeThakaOutstanding = null;
        IReadOnlyList<ThakaProjectListItemViewModel> activeProjects = [];
        bool? databaseConnected = null;
        var databaseStatus = "Database status unavailable";
        var issues = new List<string>();

        try
        {
            // Reporting remains the financial authority. BusinessDate is a later
            // Sprint 9 boundary; this preserves the existing report date contract.
            var today = DateTime.Today;
            var report = await _businessOperations.GetReportAsync(
                ReportPeriodMode.Daily,
                today,
                today.Month,
                today.Year,
                cancellationToken);

            todaySales = report.NetSales;
            todayProfit = report.NetProfit;
            expenses = report.Expenses;
            todayThakaMaterial = report.ThakaMaterial;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"Financial dashboard reads unavailable: {ex.Message}");
        }

        try
        {
            var page = await _thaka.GetProjectsPageAsync(
                search: null,
                filter: "ACTIVE",
                pageSize: 10,
                cancellationToken: cancellationToken);

            activeProjects = page.Items
                .OrderByDescending(project => project.Balance)
                .ThenBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            activeThakaCount = page.TotalActiveCount;
            activeThakaOutstanding = page.TotalActiveBalance;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"Thaka dashboard reads unavailable: {ex.Message}");
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var readiness = scope.ServiceProvider
                .GetRequiredService<IDatabaseReadinessService>();
            var result = await readiness.CheckAsync(cancellationToken);

            databaseConnected = result.CanConnect && !result.HasPendingMigrations;
            databaseStatus = !result.CanConnect
                ? "Database Unavailable"
                : result.HasPendingMigrations
                    ? "Database Migration Pending"
                    : "Database Connected";

            if (!result.IsReady && !string.IsNullOrWhiteSpace(result.FailureReason))
            {
                issues.Add(result.FailureReason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add($"Database readiness unavailable: {ex.Message}");
        }

        // Sprint 8 owns backup machinery, but the current Desktop runtime does not
        // compose a safe operator diagnostics adapter for it. Phase 2 must report
        // this honestly instead of inventing a healthy backup state.
        return new BackendDashboardSnapshot(
            todaySales,
            todayProfit,
            expenses,
            todayThakaMaterial,
            activeThakaCount,
            activeThakaOutstanding,
            activeProjects,
            databaseConnected,
            databaseStatus,
            null,
            "Backup Status Unavailable",
            issues);
    }
}
