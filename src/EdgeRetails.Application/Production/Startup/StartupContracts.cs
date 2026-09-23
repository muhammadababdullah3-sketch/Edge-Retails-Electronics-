namespace EdgeRetails.Application.Production.Startup;

public enum StartupDisposition
{
    LicenseRequired,
    Blocked,
    SetupRequired,
    LoginReady
}

public enum DatabaseReadinessCode
{
    Ready,
    Unavailable,
    UnexpectedDatabase,
    ProbeFailed
}

public enum MigrationCompatibilityState
{
    Compatible,
    DatabaseBehind,
    DatabaseAhead,
    HistoryDiverged,
    ModelDrift,
    ProbeFailed
}

public enum SessionRecoveryState
{
    None,
    Recoverable,
    Expired,
    UserUnavailable,
    Corrupt,
    ProbeFailed
}

public sealed record StartupResult(
    StartupDisposition Disposition,
    string Stage,
    string Message,
    string? RecoveryHint = null,
    string? ErrorCode = null,
    string? RecommendedAction = null,
    string? CorrelationId = null);

public sealed record DatabaseReadinessResult(bool Ready, string Message)
{
    public DatabaseReadinessCode Code { get; init; } = Ready ? DatabaseReadinessCode.Ready : DatabaseReadinessCode.ProbeFailed;
    public string? PostgreSqlVersion { get; init; }
    public string? DatabaseName { get; init; }
    public TimeSpan? Latency { get; init; }
}

public sealed record MigrationCompatibilityResult(bool Compatible, string Message)
{
    public MigrationCompatibilityState State { get; init; } = Compatible ? MigrationCompatibilityState.Compatible : MigrationCompatibilityState.ProbeFailed;
    public IReadOnlyList<string> KnownMigrations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> AppliedMigrations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PendingMigrations { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UnknownAppliedMigrations { get; init; } = Array.Empty<string>();
}

public sealed record SetupStateResult(bool Complete, string Message);

public sealed record SessionRecoveryResult(bool HasRecoverableSession, string? Hint)
{
    public SessionRecoveryState State { get; init; } = HasRecoverableSession ? SessionRecoveryState.Recoverable : SessionRecoveryState.None;
}

public sealed record ProductionSessionSnapshot(
    Guid UserId,
    string? DisplayName,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset? ExpiresAtUtc);

public sealed record SessionUserEligibility(bool Eligible, string? DisplayName, string? ReasonCode = null);

public interface IDatabaseReadinessProbe
{
    Task<DatabaseReadinessResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IMigrationCompatibilityProbe
{
    Task<MigrationCompatibilityResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface ISetupStateProbe
{
    Task<SetupStateResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface ISessionRecoveryProbe
{
    Task<SessionRecoveryResult> CheckAsync(CancellationToken cancellationToken = default);
}

public interface IProductionSessionStore
{
    Task<ProductionSessionSnapshot?> ReadAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface IProductionSessionUserProbe
{
    Task<SessionUserEligibility> CheckAsync(Guid userId, CancellationToken cancellationToken = default);
}
