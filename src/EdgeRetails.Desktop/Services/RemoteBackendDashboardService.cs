using EdgeRetails.Application.Features.Reporting;

namespace EdgeRetails.Desktop.Services;

/// <summary>Dashboard summary through report, Thaka, and readiness APIs.</summary>
public sealed class RemoteBackendDashboardService(
    DesktopApiClient apiClient,
    IBackendThakaService thaka) : IBackendDashboardService
{
    public async Task<BackendDashboardSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        decimal? todaySales = null;
        decimal? todayProfit = null;
        decimal? expenses = null;
        decimal? thakaMaterial = null;
        int? activeCount = null;
        decimal? activeBalance = null;
        IReadOnlyList<EdgeRetails.Desktop.ViewModels.ThakaProjectListItemViewModel> projects = [];
        bool? databaseConnected = null;
        var databaseStatus = "Server status unavailable";
        var backupStatus = "Backup Status Unavailable";

        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var report = await apiClient.GetAsync<ReportingSnapshotDto>(
                $"/api/reports/snapshot?mode=Daily&date={today:yyyy-MM-dd}&month={today.Month}&year={today.Year}", cancellationToken);
            todaySales = report.NetSales;
            todayProfit = report.NetProfit;
            expenses = report.Expenses;
            thakaMaterial = report.ThakaMaterial;
        }
        catch (DesktopApiException ex)
        {
            issues.Add($"Financial dashboard reads unavailable: {ex.Code}");
        }

        try
        {
            var page = await thaka.GetProjectsPageAsync(null, "ACTIVE", 10, cancellationToken: cancellationToken);
            projects = [.. page.Items.OrderByDescending(row => row.Balance).ThenBy(row => row.ProjectName, StringComparer.OrdinalIgnoreCase)];
            activeCount = page.TotalActiveCount;
            activeBalance = page.TotalActiveBalance;
        }
        catch (DesktopApiException ex)
        {
            issues.Add($"Thaka dashboard reads unavailable: {ex.Code}");
        }

        try
        {
            var readiness = await apiClient.GetAsync<ReadyDto>("/api/system/ready", cancellationToken);
            databaseConnected = readiness.CanConnect == true || (readiness.CanConnect is null && string.Equals(readiness.Status, "Ready", StringComparison.Ordinal));
            databaseStatus = databaseConnected.Value ? "Database Connected" : "Database Unavailable";
        }
        catch (DesktopApiException ex)
        {
            databaseConnected = false;
            databaseStatus = "Server Unavailable";
            issues.Add($"Server readiness unavailable: {ex.Code}");
        }

        try
        {
            var diagnostics = await apiClient.GetAsync<BackupHistoryDiagnosticsResponse>("/api/backups/diagnostics", cancellationToken);
            backupStatus = BackupDiagnosticsDisplay.Format(diagnostics);
        }
        catch (Exception ex) when (ex is DesktopApiException or System.Text.Json.JsonException)
        {
            issues.Add("Backup diagnostics unavailable; no backup health confirmation.");
        }

        return new BackendDashboardSnapshot(todaySales, todayProfit, expenses, thakaMaterial, activeCount,
            activeBalance, projects, databaseConnected, databaseStatus, null,
            backupStatus, issues);
    }

    private sealed record ReadyDto(string Status, bool? CanConnect = null, bool? HasPendingMigrations = null, string? FailureReason = null);
}
