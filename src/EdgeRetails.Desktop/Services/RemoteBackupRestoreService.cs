using System.Net;
using EdgeRetails.Application.Production.Backup;

namespace EdgeRetails.Desktop.Services;

public sealed record SafeBackupRecord(
    Guid BackupId,
    DateTimeOffset CreatedAtUtc,
    long SizeBytes,
    string? PostgreSqlServerVersion,
    string PgDumpVersion,
    string ApplicationVersion,
    string? SchemaVersion,
    string Protection,
    int FormatVersion);

public sealed record BackupHistoryDiagnosticsResponse(
    int VerifiedBackupCount,
    IReadOnlyList<SafeBackupRecord> VerifiedBackups,
    int InvalidArtifactCount,
    IReadOnlyList<BackupHistoryIssueCount> Issues);

public sealed record RestoreStatusApiResponse(
    RestoreSessionSummary Session,
    string? CutoverConfirmation,
    string? DiscardConfirmation);

public interface IBackendBackupRestoreService
{
    Task<IReadOnlyList<SafeBackupRecord>> LoadHistoryAsync(CancellationToken cancellationToken = default);
    Task<BackupHistoryDiagnosticsResponse> LoadHistoryDiagnosticsAsync(CancellationToken cancellationToken = default)
        => Task.FromException<BackupHistoryDiagnosticsResponse>(new NotSupportedException("Backup history diagnostics are not supported by this backend."));
    Task<SafeBackupRecord> CreateBackupAsync(CancellationToken cancellationToken = default);
    Task<RestoreStatusApiResponse> PrepareRestoreAsync(Guid backupId, CancellationToken cancellationToken = default);
    Task<RestoreStatusApiResponse?> GetRestoreByOperationIdAsync(Guid clientOperationId, CancellationToken cancellationToken = default);
    Task<RestoreStatusApiResponse?> ReconcilePendingRestorePreparationAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<RestoreStatusApiResponse?>(null);
    Task<RestoreStatusApiResponse?> GetRestoreAsync(Guid restoreId, CancellationToken cancellationToken = default);
    Task<RestoreStatusApiResponse> RecoverPreparationAsync(Guid clientOperationId, CancellationToken cancellationToken = default);
    Task<RestoreStatusApiResponse> CutoverAsync(Guid restoreId, string confirmation, CancellationToken cancellationToken = default);
    Task<RestoreStatusApiResponse> DiscardAsync(Guid restoreId, string confirmation, CancellationToken cancellationToken = default);
}

/// <summary>Backup and restore operations cross the loopback Server boundary; no filesystem authority is sent by Desktop.</summary>
public sealed class RemoteBackupRestoreService(
    DesktopApiClient apiClient,
    IClientOperationIntentStore operationIntents) : IBackendBackupRestoreService
{
    public Task<IReadOnlyList<SafeBackupRecord>> LoadHistoryAsync(CancellationToken cancellationToken = default)
        => apiClient.GetAsync<IReadOnlyList<SafeBackupRecord>>("/api/backups", cancellationToken);

    public Task<BackupHistoryDiagnosticsResponse> LoadHistoryDiagnosticsAsync(CancellationToken cancellationToken = default)
        => apiClient.GetAsync<BackupHistoryDiagnosticsResponse>("/api/backups/diagnostics", cancellationToken);

    public async Task<SafeBackupRecord> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await apiClient.PostAsync<CreateBackupRequest, BackupCreateApiResponse>(
                "/api/backups", new(Guid.CreateVersion7()), cancellationToken);
            return response.Backup;
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            throw new BackendOperationException(
                "backup.create_outcome_unknown",
                "The Server did not confirm whether the backup completed. Refresh backup history before retrying.");
        }
        catch (DesktopApiException ex)
        {
            throw new BackendOperationException(ex.Code, ex.Message);
        }
    }

    public async Task<RestoreStatusApiResponse> PrepareRestoreAsync(Guid backupId, CancellationToken cancellationToken = default)
    {
        if (backupId == Guid.Empty)
        {
            throw new ArgumentException("A backup identity is required.", nameof(backupId));
        }

        var key = $"backup-restore-prepare:{backupId:D}";
        var payload = backupId.ToString("N");
        var operationId = operationIntents.GetOrCreate(key, payload);
        try
        {
            var response = await apiClient.PostAsync<PrepareRestoreRequest, RestoreStatusApiResponse>(
                $"/api/backups/{backupId:D}/restore/prepare",
                new(operationId, Guid.CreateVersion7()), cancellationToken);
            CompleteResolvedPrepare(key, operationId, response);
            return response;
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            var recovered = await TryGetRestoreByOperationIdAsync(operationId, cancellationToken);
            if (recovered is not null)
            {
                CompleteResolvedPrepare(key, operationId, recovered);
                return recovered;
            }

            throw new BackendOperationException(
                "backup.restore_prepare_outcome_unknown",
                "Restore preparation may still be running. Recover this operation before starting another restore.");
        }
        catch (DesktopApiException ex) when (string.Equals(ex.Code, "restore.reconciliation_required", StringComparison.Ordinal))
        {
            var known = await TryGetRestoreByOperationIdAsync(operationId, cancellationToken);
            if (known?.Session.State == RestoreSessionState.Preparing)
            {
                known = await RecoverPreparationAsync(operationId, cancellationToken);
            }

            if (known is not null)
            {
                CompleteResolvedPrepare(key, operationId, known);
                return known;
            }

            throw new BackendOperationException(
                "backup.restore_prepare_outcome_unknown",
                "Restore preparation requires recovery. Retry with the same backup selected to reconcile its saved operation.");
        }
        catch (DesktopApiException ex)
        {
            operationIntents.Complete(key, operationId);
            throw new BackendOperationException(ex.Code, ex.Message);
        }
    }

    public async Task<RestoreStatusApiResponse?> GetRestoreByOperationIdAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
        => await TryGetRestoreByOperationIdAsync(clientOperationId, cancellationToken);

    public async Task<RestoreStatusApiResponse?> ReconcilePendingRestorePreparationAsync(
        CancellationToken cancellationToken = default)
    {
        foreach (var intent in operationIntents.FindPendingByPrefix("backup-restore-prepare:"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await TryGetRestoreByOperationIdAsync(intent.OperationId, cancellationToken);
            if (status is null)
            {
                continue;
            }

            CompleteResolvedPrepare(intent.OperationKey, intent.OperationId, status);
            return status;
        }

        return null;
    }

    public async Task<RestoreStatusApiResponse?> GetRestoreAsync(Guid restoreId, CancellationToken cancellationToken = default)
    {
        try
        {
            return await apiClient.GetAsync<RestoreStatusApiResponse>(
                $"/api/backups/restore-sessions/{restoreId:D}", cancellationToken);
        }
        catch (DesktopApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (DesktopApiException ex)
        {
            throw new BackendOperationException(ex.Code, ex.Message);
        }
    }

    public async Task<RestoreStatusApiResponse> RecoverPreparationAsync(
        Guid clientOperationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await apiClient.PostAsync<RecoverRestoreRequest, RestoreStatusApiResponse>(
                $"/api/backups/restore-operations/{clientOperationId:D}/recover",
                new(Guid.CreateVersion7()), cancellationToken);
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            var recovered = await TryGetRestoreByOperationIdAsync(clientOperationId, cancellationToken);
            if (recovered is not null)
            {
                return recovered;
            }

            throw new BackendOperationException(
                "backup.restore_recovery_outcome_unknown",
                "The Server did not confirm restore recovery. The recovery operation remains available by its operation ID.");
        }
        catch (DesktopApiException ex)
        {
            throw new BackendOperationException(ex.Code, ex.Message);
        }
    }

    public Task<RestoreStatusApiResponse> CutoverAsync(
        Guid restoreId,
        string confirmation,
        CancellationToken cancellationToken = default)
        => SendRestoreMutationAsync(
            restoreId,
            "cutover",
            confirmation,
            expectedState: RestoreSessionState.Completed,
            cancellationToken);

    public Task<RestoreStatusApiResponse> DiscardAsync(
        Guid restoreId,
        string confirmation,
        CancellationToken cancellationToken = default)
        => SendRestoreMutationAsync(
            restoreId,
            "discard",
            confirmation,
            expectedState: RestoreSessionState.Discarded,
            cancellationToken);

    private async Task<RestoreStatusApiResponse?> TryGetRestoreByOperationIdAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await apiClient.GetAsync<RestoreStatusApiResponse>(
                $"/api/backups/restore-operations/{operationId:D}", cancellationToken);
        }
        catch (DesktopApiException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            return null;
        }
        catch (DesktopApiException ex)
        {
            throw new BackendOperationException(ex.Code, ex.Message);
        }
    }

    private async Task<RestoreStatusApiResponse> SendRestoreMutationAsync(
        Guid restoreId,
        string action,
        string confirmation,
        RestoreSessionState expectedState,
        CancellationToken cancellationToken)
    {
        if (restoreId == Guid.Empty || string.IsNullOrWhiteSpace(confirmation))
        {
            throw new ArgumentException("Restore identity and typed confirmation are required.");
        }

        var path = $"/api/backups/restore-sessions/{restoreId:D}/{action}";
        try
        {
            var status = await apiClient.PostAsync<RestoreConfirmationRequest, RestoreStatusApiResponse>(
                path, new(confirmation, Guid.CreateVersion7()), cancellationToken);
            CompleteTerminalRestoreIntent(status);
            return status;
        }
        catch (DesktopApiException ex) when (IsAmbiguous(ex))
        {
            var status = await GetRestoreAsync(restoreId, cancellationToken);
            if (status?.Session.State == expectedState)
            {
                CompleteTerminalRestoreIntent(status);
                return status;
            }

            throw new BackendOperationException(
                $"backup.restore_{action}_outcome_unknown",
                "The Server did not confirm the restore operation. Refresh its status before trying again.");
        }
        catch (DesktopApiException ex)
        {
            throw new BackendOperationException(ex.Code, ex.Message);
        }
    }

    private void CompleteResolvedPrepare(string key, Guid operationId, RestoreStatusApiResponse response)
    {
        if (response.Session.State is RestoreSessionState.Completed or RestoreSessionState.Discarded)
        {
            operationIntents.Complete(key, operationId);
        }
    }

    private void CompleteTerminalRestoreIntent(RestoreStatusApiResponse response)
    {
        if (response.Session.State is not (RestoreSessionState.Completed or RestoreSessionState.Discarded) ||
            response.Session.ClientOperationId is not Guid operationId)
        {
            return;
        }

        foreach (var intent in operationIntents.FindPendingByPrefix("backup-restore-prepare:"))
        {
            if (intent.OperationId == operationId)
            {
                operationIntents.Complete(intent.OperationKey, operationId);
                return;
            }
        }
    }

    private static bool IsAmbiguous(DesktopApiException exception)
        => exception.StatusCode is null || (int)exception.StatusCode >= 500;

    private sealed record CreateBackupRequest(Guid CorrelationId);
    private sealed record BackupCreateApiResponse(SafeBackupRecord Backup, string? RetentionWarningCode);
    private sealed record PrepareRestoreRequest(Guid ClientOperationId, Guid CorrelationId);
    private sealed record RecoverRestoreRequest(Guid CorrelationId);
    private sealed record RestoreConfirmationRequest(string Confirmation, Guid CorrelationId);
}
