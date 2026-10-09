using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Features.Settings;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Application.Gateways;
using System.Text.Json;

namespace EdgeRetails.Desktop.Services;

/// <summary>Settings and operator identity data from the local Server authority.</summary>
public sealed class RemoteBackendSettingsService(
    DesktopApiClient apiClient,
    Func<Guid?> actorUserId) : IBackendSettingsService
{
    public bool SupportsCashierCreation => true;

    public async Task<CreatedCashierDto> CreateCashierAsync(string displayName, string pin, Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        _ = RequireActor();
        try
        {
            var created = await apiClient.PostAsync<CashierRequest, CreatedCashierDto>("/api/users/cashiers",
                new(displayName, pin, clientOperationId, Guid.CreateVersion7()), cancellationToken);
            if (created.UserId == Guid.Empty || created.RoleName != "Cashier" || created.DisplayName != displayName.Trim())
            {
                throw new DesktopApiException("gateway.invalid_response", "The Server did not confirm the Cashier identity.");
            }
            return created;
        }
        catch (Exception ex) when (IsUncertain(ex))
        {
            // Only operation metadata is queried after a lost response. The PIN stays transient.
            try
            {
                var status = await GetCashierCreationStatusAsync(clientOperationId, cancellationToken);
                if (status.State == "Succeeded" && status.UserId is Guid userId)
                {
                    return new(userId, displayName, "Cashier");
                }
            }
            catch (Exception statusError) when (statusError is DesktopApiException or JsonException)
            {
                // Preserve the original operation identity for authenticated reconciliation/retry.
            }
            throw new BackendOperationException("operation.outcome_unknown",
                "Account creation is unconfirmed. Check status or retry this same creation.");
        }
    }

    public async Task<CashierCreationStatus> GetCashierCreationStatusAsync(Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        _ = RequireActor();
        var status = await apiClient.GetAsync<OperationStatusResult>($"/api/operations/{clientOperationId:D}", cancellationToken);
        if (status.ClientOperationId != clientOperationId ||
            (status.Found && status.OperationType != CreateCashierHandler.OperationType))
        {
            throw new BackendOperationException("idempotency.payload_mismatch", "The operation status does not match this account creation.");
        }
        return new(status.Status ?? "OutcomeUnknown", status.WasCommitted ? status.EntityId : null);
    }

    private static bool IsUncertain(Exception exception) => exception is JsonException ||
        exception is DesktopApiException apiError && (apiError.StatusCode is null || (int)apiError.StatusCode >= 500);

    public async Task<BackendSettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var issues = new List<string>();
        SettingsConfigurationDto? settings = null;
        IReadOnlyList<LoginAccountDto> accounts = [];
        var databaseStatus = "Unavailable";
        var connectionStatus = "Server readiness unavailable";
        var maintenanceStatus = "Unavailable · maintenance diagnostics not attached";
        var lastBackup = "Unavailable · backup diagnostics not attached";

        try
        {
            settings = await apiClient.GetAsync<SettingsConfigurationDto>("/api/settings", cancellationToken);
        }
        catch (DesktopApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            issues.Add("Shop and receipt settings are not configured.");
        }

        try
        {
            accounts = await apiClient.GetAsync<IReadOnlyList<LoginAccountDto>>("/api/auth/accounts", cancellationToken);
        }
        catch (DesktopApiException ex)
        {
            issues.Add($"User directory unavailable: {ex.Code}");
        }

        try
        {
            var ready = await apiClient.GetAsync<ReadyDto>("/api/system/ready", cancellationToken);
            var isDbReady = ready.CanConnect == true || (ready.CanConnect is null && string.Equals(ready.Status, "Ready", StringComparison.Ordinal));
            databaseStatus = isDbReady ? "Ready" : "Unavailable";
            connectionStatus = isDbReady ? "Connected · schema ready" : ready.FailureReason ?? "Database is not ready.";
            maintenanceStatus = string.Equals(ready.MaintenanceState, "Normal", StringComparison.Ordinal)
                ? "Normal · Server maintenance barrier" : "Unavailable · maintenance state not confirmed";
        }
        catch (DesktopApiException ex)
        {
            databaseStatus = "Unavailable";
            connectionStatus = $"Server readiness unavailable: {ex.Code}";
        }

        try
        {
            var backups = await apiClient.GetAsync<BackupHistoryDiagnosticsResponse>("/api/backups/diagnostics", cancellationToken);
            lastBackup = BackupDiagnosticsDisplay.Format(backups);
            if (backups.InvalidArtifactCount > 0)
            {
                issues.Add($"Backup integrity: {backups.InvalidArtifactCount} invalid artifact(s).");
            }
        }
        catch (Exception ex) when (ex is DesktopApiException or JsonException)
        {
            issues.Add("Backup diagnostics unavailable; no backup health confirmation.");
        }

        var users = accounts.Select(row => new SettingsUserRecord(row.DisplayName, row.RoleName)).ToArray();
        var owner = users.FirstOrDefault(row => string.Equals(row.Role, "Owner", StringComparison.OrdinalIgnoreCase));
        return new BackendSettingsSnapshot(
            users,
            settings?.ShopName ?? string.Empty,
            owner?.Name ?? string.Empty,
            settings?.Phone ?? string.Empty,
            settings?.Address ?? string.Empty,
            settings?.ReceiptHeader ?? string.Empty,
            settings?.ReceiptFooter ?? string.Empty,
            settings?.ShowCustomer ?? false,
            settings?.ShowCashier ?? false,
            settings?.AutoPrintDefault ?? false,
            "PostgreSQL",
            "Unavailable · Storage metrics not exposed by server",
            databaseStatus,
            connectionStatus,
            "Unavailable · Background worker heartbeat endpoint not attached",
            "Unavailable",
            "Unavailable",
            "Unavailable",
            "Unavailable",
            "Unavailable",
            "Unavailable · License server endpoint not attached",
            lastBackup,
            maintenanceStatus,
            issues);
    }

    public async Task SaveShopAsync(string shopName, string? phone, string? address,
        CancellationToken cancellationToken = default)
    {
        var result = await apiClient.PutAsync<ShopSettingsRequest, SuccessResponse>(
            "/api/settings/shop",
            new(shopName, phone, address, RequireActor(), Guid.CreateVersion7()), cancellationToken);
        EnsureSuccess(result);
    }

    public async Task SaveReceiptTemplateAsync(string? header, string? footer, bool showCustomer,
        bool showCashier, bool autoPrintDefault, CancellationToken cancellationToken = default)
    {
        var result = await apiClient.PutAsync<ReceiptSettingsRequest, SuccessResponse>(
            "/api/settings/receipt-template",
            new(header, footer, showCustomer, showCashier, autoPrintDefault, RequireActor(), Guid.CreateVersion7()), cancellationToken);
        EnsureSuccess(result);
    }

    private Guid RequireActor() => actorUserId() is Guid actor && actor != Guid.Empty
        ? actor
        : throw new BackendOperationException("identity.session_required", "A persistent backend user session is required.");

    private static void EnsureSuccess(SuccessResponse result)
    {
        if (!result.Success)
        {
            throw new BackendOperationException("settings.operation_unconfirmed", "Server did not confirm the settings change.");
        }
    }

    private sealed record ShopSettingsRequest(string ShopName, string? Phone, string? Address, Guid ActorId, Guid CorrelationId);
    private sealed record ReceiptSettingsRequest(string? Header, string? Footer, bool ShowCustomer,
        bool ShowCashier, bool AutoPrintDefault, Guid ActorId, Guid CorrelationId);
    private sealed record SuccessResponse(bool Success);
    private sealed record CashierRequest(string DisplayName, string Pin, Guid ClientOperationId, Guid CorrelationId)
    {
        public override string ToString() => $"CashierRequest {{ ClientOperationId = {ClientOperationId} }}";
    }
    private sealed record ReadyDto(string Status, string? MaintenanceState, bool? CanConnect = null, bool? HasPendingMigrations = null, string? FailureReason = null);
}

internal static class BackupDiagnosticsDisplay
{
    public static string Format(BackupHistoryDiagnosticsResponse diagnostics)
    {
        if (diagnostics.VerifiedBackups is null || diagnostics.VerifiedBackupCount != diagnostics.VerifiedBackups.Count ||
            diagnostics.InvalidArtifactCount < 0)
        {
            throw new DesktopApiException("gateway.invalid_response", "Backup diagnostics could not be verified.");
        }
        var newest = diagnostics.VerifiedBackups.OrderByDescending(row => row.CreatedAtUtc).FirstOrDefault();
        if (newest is null)
        {
            return $"Never Run · No verified backups · {diagnostics.InvalidArtifactCount} invalid artifact(s)";
        }

        var age = DateTimeOffset.UtcNow - newest.CreatedAtUtc;
        var freshness = age <= TimeSpan.FromHours(24) ? "Fresh" : "Stale";
        return $"{freshness} · Last verified backup {newest.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC · {diagnostics.InvalidArtifactCount} invalid artifact(s)";
    }
}
