using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production;
using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;
using EdgeRetails.Server.Middleware;
using Microsoft.AspNetCore.Mvc;

namespace EdgeRetails.Server.Controllers;

[ApiController]
[Route("api/backups")]
public sealed class BackupsController(
    IConfiguration configuration,
    PostgresConnectionDescriptor runtimeConnection,
    IPostgresBackupEngine engine,
    IRestoreSessionStore restoreSessions,
    CreateBackupHandler createBackup,
    PrepareRestoreHandler prepareRestore,
    CutoverRestoreHandler cutoverRestore,
    DiscardPreparedRestoreHandler discardRestore,
    RecoverRestorePreparationHandler recoverRestorePreparation) : ControllerBase
{
    private const string CutoverConfirmationPrefix = "CUTOVER";
    private const string DiscardConfirmationPrefix = "DISCARD";
    private static readonly HashSet<string> SafeBackupIssueCodes = new(StringComparer.Ordinal)
    {
        "backup.manifest_reparse_point",
        "backup.manifest_size_invalid",
        "backup.manifest_invalid",
        "backup.manifest_fields_invalid",
        "backup.manifest_authentication_failed",
        "backup.manifest_companion_mismatch",
        "backup.artifact_missing",
        "backup.artifact_reparse_point",
        "backup.artifact_size_mismatch",
        "backup.artifact_checksum_mismatch",
        "backup.duplicate_artifact",
        "backup.manifest_invalid_json",
        "backup.manifest_path_invalid",
        "backup.manifest_access_denied",
        "backup.manifest_io_failed"
    };

    [HttpGet]
    public async Task<IActionResult> GetHistory(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        var backups = await engine.ReadHistoryAsync(ResolveBackupDirectory(), cancellationToken);
        return Ok(backups.Select(ToSafeBackupRecord).ToArray());
    }

    [HttpGet("diagnostics")]
    public async Task<IActionResult> GetHistoryDiagnostics(CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        var diagnostics = await engine.ReadDiagnosticsAsync(ResolveBackupDirectory(), cancellationToken);
        var issueCounts = diagnostics.Issues
            .GroupBy(issue => SafeBackupIssueCodes.Contains(issue.Code) ? issue.Code : "backup.validation_issue", StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new BackupHistoryIssueCount(group.Key, group.Count()))
            .ToArray();
        return Ok(new BackupHistoryDiagnosticsApiResponse(
            diagnostics.ValidBackups.Count,
            diagnostics.ValidBackups.Select(ToSafeBackupRecord).ToArray(),
            diagnostics.Issues.Count,
            issueCounts));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBackupApiRequest request, CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        if (request.CorrelationId == Guid.Empty)
        {
            return BadRequest(new { code = "backup.correlation_required", message = "A nonempty correlation identifier is required." });
        }

        try
        {
            var result = await createBackup.HandleAsync(
                new BackupCreateRequest(
                    ResolveRuntimeConnection(),
                    ResolveBackupDirectory(),
                    typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                    null,
                    new BackupRetentionPolicy(
                        ReadOptionalPositiveInt("Backup:MaximumBackupCount"),
                        ReadOptionalPositiveDays("Backup:MaximumBackupAgeDays")),
                    request.CorrelationId.ToString("D")),
                cancellationToken);

            return Ok(new BackupCreateApiResponse(ToSafeBackupRecord(result.Manifest), result.RetentionWarningCode));
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { code = "backup.operation_conflict", message = "The backup operation could not be started in the current server state." });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "backup.create_failed", message = "The server could not complete the backup operation." });
        }
    }

    [HttpPost("{backupId:guid}/restore/prepare")]
    public async Task<IActionResult> PrepareRestore(
        Guid backupId,
        [FromBody] PrepareRestoreApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        if (request.ClientOperationId == Guid.Empty || request.CorrelationId == Guid.Empty)
        {
            return BadRequest(new { code = "restore.operation_identity_required", message = "Nonempty operation and correlation identifiers are required." });
        }

        try
        {
            var backup = (await engine.ReadHistoryAsync(ResolveBackupDirectory(), cancellationToken))
                .SingleOrDefault(x => x.BackupId == backupId);
            if (backup is null)
            {
                return NotFound(new { code = "backup.not_found", message = "The verified backup was not found." });
            }

            var directory = ResolveBackupDirectory();
            var backupPath = BackupArtifactPathSafety.ResolveOwnedBackupPath(directory, backup.FileName);
            var manifestPath = backupPath + ".manifest.json";
            var runtimeConnection = ResolveRuntimeConnection();

            var existing = await restoreSessions.GetJournalByOperationIdAsync(request.ClientOperationId, cancellationToken);
            if (existing is not null)
            {
                if (existing.TargetDatabase != runtimeConnection.Database ||
                    !PathEquals(existing.BackupFilePath, backupPath) ||
                    !string.Equals(existing.VerifiedSha256, backup.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return Conflict(new { code = "restore.operation_identity_conflict", message = "This operation identifier is already bound to another restore request." });
                }

                if (existing.State == RestoreSessionState.Prepared)
                {
                    return Ok(ToRestoreStatusResponse(existing));
                }

                if (existing.State == RestoreSessionState.Preparing)
                {
                    var reconciled = await recoverRestorePreparation.HandleAsync(
                        request.ClientOperationId,
                        request.CorrelationId.ToString("D"),
                        cancellationToken);
                    var reconciledSession = await restoreSessions.GetAsync(reconciled.RestoreId, cancellationToken);
                    return reconciledSession is null
                        ? StatusCode(StatusCodes.Status500InternalServerError,
                            new { code = "restore.journal_unavailable", message = "The restore recovery journal could not be read." })
                        : Ok(ToRestoreStatusResponse(reconciledSession));
                }

                if (existing.State is RestoreSessionState.Discarded or RestoreSessionState.RolledBack or RestoreSessionState.Completed)
                {
                    return Ok(ToRestoreStatusResponse(existing));
                }

                return Conflict(new { code = "restore.reconciliation_required", message = "Look up this operation and reconcile its journal before starting another restore." });
            }

            var token = await prepareRestore.HandleAsync(
                new RestorePrepareRequest(
                    runtimeConnection,
                    backupPath,
                    manifestPath,
                    request.CorrelationId.ToString("D"),
                    request.ClientOperationId),
                cancellationToken);
            var summary = await restoreSessions.GetAsync(token.RestoreId, cancellationToken);
            if (summary is null)
            {
                return StatusCode(StatusCodes.Status500InternalServerError,
                    new { code = "restore.journal_unavailable", message = "The prepared restore journal could not be read." });
            }

            return Ok(ToRestoreStatusResponse(summary));
        }
        catch (RestoreRecoveryRequiredException)
        {
            return Conflict(new { code = "restore.reconciliation_required", message = "Restore preparation requires journal reconciliation before retry." });
        }
        catch (FileNotFoundException)
        {
            return NotFound(new { code = "backup.not_found", message = "The verified backup artifact is no longer available." });
        }
        catch (InvalidDataException)
        {
            return Conflict(new { code = "restore.validation_failed", message = "The restore artifact or journal failed server validation." });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "restore.prepare_failed", message = "The server could not prepare the restore." });
        }
    }

    [HttpGet("restore-sessions/{restoreId:guid}")]
    public async Task<IActionResult> GetRestoreStatus(Guid restoreId, CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        var session = await restoreSessions.GetAsync(restoreId, cancellationToken);
        if (session is null || !string.Equals(session.TargetDatabase, ResolveRuntimeConnection().Database, StringComparison.Ordinal))
        {
            return NotFound(new { code = "restore.not_found", message = "The restore session was not found." });
        }

        return Ok(ToRestoreStatusResponse(session));
    }

    [HttpGet("restore-operations/{clientOperationId:guid}")]
    public async Task<IActionResult> GetRestoreOperationStatus(Guid clientOperationId, CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        if (clientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "restore.operation_identity_required", message = "A nonempty operation identifier is required." });
        }

        var summary = await restoreSessions.GetSummaryByOperationIdAsync(clientOperationId, cancellationToken);
        if (summary is null)
        {
            return NotFound(new { code = "restore.operation_not_found", message = "The restore operation was not found." });
        }

        var session = await restoreSessions.GetAsync(summary.RestoreId, cancellationToken);
        if (session is null || !string.Equals(session.TargetDatabase, ResolveRuntimeConnection().Database, StringComparison.Ordinal))
        {
            return NotFound(new { code = "restore.operation_not_found", message = "The restore operation was not found." });
        }

        return Ok(ToRestoreStatusResponse(session));
    }

    [HttpPost("restore-operations/{clientOperationId:guid}/recover")]
    public async Task<IActionResult> RecoverRestorePreparation(
        Guid clientOperationId,
        [FromBody] RestoreRecoveryApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        if (clientOperationId == Guid.Empty)
        {
            return BadRequest(new { code = "restore.operation_identity_required", message = "A nonempty operation identifier is required." });
        }

        if (request.CorrelationId == Guid.Empty)
        {
            return BadRequest(new { code = "restore.correlation_required", message = "A nonempty correlation identifier is required." });
        }

        try
        {
            var journal = await restoreSessions.GetJournalByOperationIdAsync(clientOperationId, cancellationToken);
            if (journal is null || !string.Equals(journal.TargetDatabase, ResolveRuntimeConnection().Database, StringComparison.Ordinal))
            {
                return NotFound(new { code = "restore.operation_not_found", message = "The restore operation was not found." });
            }

            var summary = await recoverRestorePreparation.HandleAsync(
                clientOperationId,
                request.CorrelationId.ToString("D"),
                cancellationToken);
            var session = await restoreSessions.GetAsync(summary.RestoreId, cancellationToken);
            return session is null
                ? StatusCode(StatusCodes.Status500InternalServerError, new { code = "restore.journal_unavailable", message = "The reconciled restore journal could not be read." })
                : Ok(ToRestoreStatusResponse(session));
        }
        catch (RestoreRecoveryRequiredException)
        {
            return Conflict(new { code = "restore.manual_recovery_required", message = "Automatic preparation recovery was refused; operator recovery is required." });
        }
        catch (InvalidOperationException)
        {
            return Conflict(new { code = "restore.reconciliation_conflict", message = "The restore operation could not be reconciled in its current state." });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "restore.reconciliation_failed", message = "The server could not reconcile the restore operation." });
        }
    }

    [HttpPost("restore-sessions/{restoreId:guid}/cutover")]
    public async Task<IActionResult> CutoverRestore(
        Guid restoreId,
        [FromBody] RestoreConfirmationApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        var session = await GetPreparedSessionAsync(restoreId, cancellationToken);
        if (session is null)
        {
            return Conflict(new { code = "restore.session_not_prepared", message = "Only a prepared restore for the configured target can be cut over." });
        }

        if (!string.Equals(request.Confirmation, BuildCutoverConfirmation(session.TargetDatabase, restoreId), StringComparison.Ordinal))
        {
            return BadRequest(new { code = "restore.confirmation_required", message = "Enter the exact cutover confirmation for this restore and configured target." });
        }

        if (request.CorrelationId == Guid.Empty)
        {
            return BadRequest(new { code = "restore.correlation_required", message = "A nonempty correlation identifier is required." });
        }

        try
        {
            await cutoverRestore.HandleAsync(
                ResolveRuntimeConnection(),
                new RestoreSessionToken(restoreId),
                request.CorrelationId.ToString("D"),
                cancellationToken);
            var completed = await restoreSessions.GetAsync(restoreId, cancellationToken);
            return completed is null
                ? StatusCode(StatusCodes.Status500InternalServerError, new { code = "restore.journal_unavailable", message = "The restore result journal could not be read." })
                : Ok(ToRestoreStatusResponse(completed));
        }
        catch (RestoreRecoveryRequiredException)
        {
            return Conflict(new { code = "restore.manual_recovery_required", message = "Cutover requires operator recovery." });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "restore.cutover_failed", message = "The server could not complete restore cutover." });
        }
    }

    [HttpPost("restore-sessions/{restoreId:guid}/discard")]
    public async Task<IActionResult> DiscardRestore(
        Guid restoreId,
        [FromBody] RestoreConfirmationApiRequest request,
        CancellationToken cancellationToken)
    {
        var denied = this.RequirePermission(PermissionKeys.SettingsManage);
        if (denied is not null)
        {
            return denied;
        }

        var session = await GetPreparedSessionAsync(restoreId, cancellationToken);
        if (session is null)
        {
            return Conflict(new { code = "restore.session_not_prepared", message = "Only a prepared restore for the configured target can be discarded." });
        }

        if (!string.Equals(request.Confirmation, BuildDiscardConfirmation(session.TargetDatabase, restoreId), StringComparison.Ordinal))
        {
            return BadRequest(new { code = "restore.confirmation_required", message = "Enter the exact discard confirmation for this restore and configured target." });
        }

        if (request.CorrelationId == Guid.Empty)
        {
            return BadRequest(new { code = "restore.correlation_required", message = "A nonempty correlation identifier is required." });
        }

        try
        {
            await discardRestore.HandleAsync(
                ResolveRuntimeConnection(),
                new RestoreSessionToken(restoreId),
                request.CorrelationId.ToString("D"),
                cancellationToken);
            var discarded = await restoreSessions.GetAsync(restoreId, cancellationToken);
            return discarded is null
                ? StatusCode(StatusCodes.Status500InternalServerError, new { code = "restore.journal_unavailable", message = "The restore result journal could not be read." })
                : Ok(ToRestoreStatusResponse(discarded));
        }
        catch (RestoreRecoveryRequiredException)
        {
            return Conflict(new { code = "restore.manual_recovery_required", message = "Discard requires operator recovery." });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { code = "restore.discard_failed", message = "The server could not discard the prepared restore." });
        }
    }

    public static string BuildCutoverConfirmation(string targetDatabase, Guid restoreId)
        => $"{CutoverConfirmationPrefix} {targetDatabase} {restoreId:N}";

    public static string BuildDiscardConfirmation(string targetDatabase, Guid restoreId)
        => $"{DiscardConfirmationPrefix} {targetDatabase} {restoreId:N}";

    private async Task<RestoreSessionRecord?> GetPreparedSessionAsync(Guid restoreId, CancellationToken cancellationToken)
    {
        var session = await restoreSessions.GetAsync(restoreId, cancellationToken);
        if (session is null || session.State != RestoreSessionState.Prepared ||
            !string.Equals(session.TargetDatabase, ResolveRuntimeConnection().Database, StringComparison.Ordinal))
        {
            return null;
        }

        return session;
    }

    private PostgresConnectionDescriptor ResolveRuntimeConnection() => runtimeConnection;

    private string ResolveBackupDirectory()
    {
        var configured = configuration["EDGE_RETAILS_BACKUP_DIR"]
            ?? Environment.GetEnvironmentVariable("EDGE_RETAILS_BACKUP_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            throw new InvalidOperationException("The server local application-data directory is unavailable.");
        }

        return Path.Combine(local, "EdgeRetails", "Production", "backups");
    }

    private int? ReadOptionalPositiveInt(string key)
        => int.TryParse(configuration[key], out var value) && value > 0 ? value : null;

    private TimeSpan? ReadOptionalPositiveDays(string key)
        => double.TryParse(configuration[key], out var days) && days > 0 ? TimeSpan.FromDays(days) : null;

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static SafeBackupRecord ToSafeBackupRecord(BackupManifest manifest)
        => new(
            manifest.BackupId,
            manifest.CreatedAtUtc,
            manifest.SizeBytes,
            manifest.PostgreSqlServerVersion,
            manifest.PgDumpVersion,
            manifest.ApplicationVersion,
            manifest.SchemaVersion,
            manifest.Protection,
            manifest.FormatVersion);

    private static RestoreSessionSummary ToSafeSummary(RestoreSessionRecord session)
        => new(session.RestoreId, session.ClientOperationId, session.State, session.PreparedAtUtc, session.CompletedAtUtc);

    private static RestoreStatusApiResponse ToRestoreStatusResponse(RestoreSessionRecord session)
    {
        var prepared = session.State == RestoreSessionState.Prepared;
        return new RestoreStatusApiResponse(
            ToSafeSummary(session),
            prepared ? BuildCutoverConfirmation(session.TargetDatabase, session.RestoreId) : null,
            prepared ? BuildDiscardConfirmation(session.TargetDatabase, session.RestoreId) : null);
    }
}

public sealed record CreateBackupApiRequest(Guid CorrelationId);
public sealed record PrepareRestoreApiRequest(Guid ClientOperationId, Guid CorrelationId);
public sealed record RestoreRecoveryApiRequest(Guid CorrelationId);
public sealed record RestoreConfirmationApiRequest(string Confirmation, Guid CorrelationId);
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
public sealed record BackupCreateApiResponse(SafeBackupRecord Backup, string? RetentionWarningCode);
public sealed record BackupHistoryDiagnosticsApiResponse(
    int VerifiedBackupCount,
    IReadOnlyList<SafeBackupRecord> VerifiedBackups,
    int InvalidArtifactCount,
    IReadOnlyList<BackupHistoryIssueCount> Issues);
public sealed record RestoreStatusApiResponse(
    RestoreSessionSummary Session,
    string? CutoverConfirmation,
    string? DiscardConfirmation);
