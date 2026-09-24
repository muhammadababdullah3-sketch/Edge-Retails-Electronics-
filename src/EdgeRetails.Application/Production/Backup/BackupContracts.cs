namespace EdgeRetails.Application.Production.Backup;

public sealed class RestoreRecoveryRequiredException : InvalidOperationException
{
    public RestoreRecoveryRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class SensitiveString
{
    private readonly string _value;
    public SensitiveString(string value) => _value = value ?? throw new ArgumentNullException(nameof(value));
    public string Reveal() => _value;
    public override string ToString() => "[REDACTED]";
}

/// <summary>Least-privilege identity used by normal Edge Retails runtime operations.</summary>
public sealed record PostgresConnectionDescriptor(
    string Host,
    int Port,
    string Database,
    string Username,
    SensitiveString Password,
    string MaintenanceDatabase = "postgres");

/// <summary>
/// Recovery-only identity. This must be supplied by the deployment/recovery trust boundary and
/// must never be persisted as a plaintext Desktop setting.
/// </summary>
public sealed record PostgresMaintenanceDescriptor(
    string Host,
    int Port,
    string Username,
    SensitiveString Password,
    string MaintenanceDatabase = "postgres");

public sealed record BackupManifest(
    Guid BackupId,
    string DatabaseName,
    string FileName,
    string Sha256,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc,
    string? PostgreSqlServerVersion,
    string PgDumpVersion,
    string ApplicationVersion,
    string? SchemaVersion,
    string Protection)
{
    public int FormatVersion { get; init; } = 2;
}

public sealed record BackupManifestEnvelope(BackupManifest Manifest, string Authentication);

public sealed record BackupHistoryIssue(string ManifestFileName, string Code);

public sealed record BackupHistoryDiagnostics(
    IReadOnlyList<BackupManifest> ValidBackups,
    IReadOnlyList<BackupHistoryIssue> Issues);

public sealed record BackupRetentionPolicy(int? MaximumBackupCount, TimeSpan? MaximumBackupAge);

public sealed record BackupCreateRequest(
    PostgresConnectionDescriptor Connection,
    string BackupDirectory,
    string ApplicationVersion,
    string? SchemaVersion,
    BackupRetentionPolicy RetentionPolicy,
    string? CorrelationId);

public sealed record BackupCreateResult(BackupManifest Manifest, string FullPath)
{
    public string? RetentionWarningCode { get; init; }
}

public sealed record RestorePrepareRequest(
    PostgresConnectionDescriptor RuntimeConnection,
    string BackupFilePath,
    string ManifestFilePath,
    string? CorrelationId);

/// <summary>
/// Opaque capability returned to callers. It intentionally contains no database names or paths.
/// Destructive restore authority is reloaded from the protected restore-session journal.
/// </summary>
public sealed record RestoreSessionToken(Guid RestoreId);

public enum RestoreSessionState
{
    Prepared,
    CutoverInProgress,
    Completed,
    Discarded,
    RolledBack,
    RecoveryRequired
}

public sealed record RestoreSessionRecord(
    Guid RestoreId,
    string TargetDatabase,
    string StagingDatabase,
    uint OriginalDatabaseOid,
    uint StagingDatabaseOid,
    string BackupFilePath,
    string VerifiedSha256,
    DateTimeOffset PreparedAtUtc,
    RestoreSessionState State,
    string? PreservedDatabase = null,
    string? FailedRestoredDatabase = null,
    DateTimeOffset? CompletedAtUtc = null);

public sealed record RestoreCutoverResult(
    Guid RestoreId,
    string ActiveDatabase,
    string PreservedPreRestoreDatabase,
    DateTimeOffset CompletedAtUtc);

public interface IPostgresMaintenanceConnectionProvider
{
    Task<PostgresMaintenanceDescriptor> GetAsync(CancellationToken cancellationToken = default);
}

public interface IRestoreSessionStore
{
    Task CreateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default);
    Task<RestoreSessionRecord?> GetAsync(Guid restoreId, CancellationToken cancellationToken = default);
    Task UpdateAsync(RestoreSessionRecord session, CancellationToken cancellationToken = default);
}

public interface IPostgresBackupEngine
{
    Task<BackupCreateResult> CreateAsync(BackupCreateRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BackupManifest>> ReadHistoryAsync(string backupDirectory, CancellationToken cancellationToken = default);
    Task<RestoreSessionToken> PrepareRestoreAsync(RestorePrepareRequest request, CancellationToken cancellationToken = default);
    Task<RestoreCutoverResult> CutoverAsync(
        PostgresConnectionDescriptor runtimeConnection,
        RestoreSessionToken token,
        CancellationToken cancellationToken = default);
    Task DiscardPreparedRestoreAsync(
        PostgresConnectionDescriptor runtimeConnection,
        RestoreSessionToken token,
        CancellationToken cancellationToken = default);
}
