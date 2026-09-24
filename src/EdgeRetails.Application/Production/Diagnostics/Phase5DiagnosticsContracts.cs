namespace EdgeRetails.Application.Production.Diagnostics;

public enum HealthClassification
{
    HEALTHY = 1,
    DEGRADED = 2,
    ACTION_REQUIRED = 3,
    UNAVAILABLE = 4
}

public static class DiagnosticCodes
{
    public const string DatabaseLatencyHealthy = "db.latency.healthy";
    public const string DatabaseLatencyDegraded = "db.latency.degraded";
    public const string DatabaseUnavailable = "db.unavailable";
    public const string DatabaseWriteSafetyHealthy = "db.write_safety.healthy";
    public const string DatabaseWriteSafetyUnavailable = "db.write_safety.unavailable";
    public const string SchemaCompatible = "schema.compatible";
    public const string SchemaIncompatible = "schema.incompatible";
    public const string BackupHealthy = "backup.healthy";
    public const string BackupDegraded = "backup.degraded";
    public const string BackupUnavailable = "backup.unavailable";
    public const string BackupAgeActionRequired = "backup.age.action_required";
    public const string RemoteBackupUnverified = "backup.remote.unverified";
    public const string DiskHealthy = "disk.healthy";
    public const string DiskDegraded = "disk.degraded";
    public const string WorkerHeartbeatHealthy = "worker.heartbeat.healthy";
    public const string WorkerHeartbeatActionRequired = "worker.heartbeat.action_required";
    public const string PrintBacklogHealthy = "print.backlog.healthy";
    public const string PrintBacklogActionRequired = "print.backlog.action_required";
    public const string OutcomeUnknownHealthy = "outcome_unknown.healthy";
    public const string OutcomeUnknownActionRequired = "outcome_unknown.action_required";
    public const string ActionRequiredBacklogHealthy = "action_required_backlog.healthy";
    public const string ActionRequiredBacklogActionRequired = "action_required_backlog.action_required";
    public const string FailedJobsHealthy = "failed_jobs.healthy";
    public const string FailedJobsActionRequired = "failed_jobs.action_required";
    public const string ReconciliationHealthy = "reconciliation.healthy";
    public const string ReconciliationActionRequired = "reconciliation.action_required";
}

public sealed record DiagnosticsPolicy(
    TimeSpan BackupMaxAge,
    TimeSpan WorkerHeartbeatMaxAge,
    long DiskFreeWarningBytes,
    int PrintBacklogWarningCount,
    int OutcomeUnknownWarningCount,
    int ActionRequiredWarningCount,
    int FailedJobsWarningCount,
    int ReconciliationFailureWarningCount,
    double DbLatencyWarningMilliseconds)
{
    public static DiagnosticsPolicy Default => new(
        TimeSpan.FromHours(24),
        TimeSpan.FromMinutes(2),
        10L * 1024 * 1024 * 1024,
        50,
        1,
        1,
        1,
        1,
        500);

    public static DiagnosticsPolicy FromEnvironment()
    {
        static TimeSpan ReadTimeSpanHours(string name, TimeSpan fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var hours) &&
            hours > 0 && hours <= 720
                ? TimeSpan.FromHours(hours)
                : fallback;

        static TimeSpan ReadTimeSpanMinutes(string name, TimeSpan fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var minutes) &&
            minutes > 0 && minutes <= 1440
                ? TimeSpan.FromMinutes(minutes)
                : fallback;

        static long ReadBytes(string name, long fallback) =>
            long.TryParse(Environment.GetEnvironmentVariable(name), out var bytes) &&
            bytes >= 0 && bytes <= long.MaxValue / 2
                ? bytes
                : fallback;

        static int ReadInt(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var value) &&
            value >= 0 && value <= 1_000_000
                ? value
                : fallback;

        static double ReadMilliseconds(string name, double fallback) =>
            double.TryParse(Environment.GetEnvironmentVariable(name), out var value) &&
            value > 0 && value <= 86_400_000
                ? value
                : fallback;

        var d = Default;
        return new DiagnosticsPolicy(
            ReadTimeSpanHours("EDGE_RETAILS_DIAG_BACKUP_MAX_AGE_HOURS", d.BackupMaxAge),
            ReadTimeSpanMinutes("EDGE_RETAILS_DIAG_WORKER_MAX_AGE_MINUTES", d.WorkerHeartbeatMaxAge),
            ReadBytes("EDGE_RETAILS_DIAG_DISK_FREE_WARNING_BYTES", d.DiskFreeWarningBytes),
            ReadInt("EDGE_RETAILS_DIAG_PRINT_BACKLOG_WARNING_COUNT", d.PrintBacklogWarningCount),
            ReadInt("EDGE_RETAILS_DIAG_OUTCOME_UNKNOWN_WARNING_COUNT", d.OutcomeUnknownWarningCount),
            ReadInt("EDGE_RETAILS_DIAG_ACTION_REQUIRED_WARNING_COUNT", d.ActionRequiredWarningCount),
            ReadInt("EDGE_RETAILS_DIAG_FAILED_JOBS_WARNING_COUNT", d.FailedJobsWarningCount),
            ReadInt("EDGE_RETAILS_DIAG_RECONCILIATION_FAILURE_WARNING_COUNT", d.ReconciliationFailureWarningCount),
            ReadMilliseconds("EDGE_RETAILS_DIAG_DB_LATENCY_WARNING_MS", d.DbLatencyWarningMilliseconds));
    }
}

public sealed record DiagnosticValue(
    string Code,
    HealthClassification Classification,
    string HumanStatus,
    string Guidance,
    double? MeasuredValue = null,
    double? Threshold = null);

public sealed record DiagnosticsSnapshot(
    DateTimeOffset CapturedAtUtc,
    HealthClassification OverallClassification,
    IReadOnlyList<DiagnosticValue> Checks,
    IReadOnlyDictionary<string, string> SafeMetadata);

public interface IHealthDiagnosticsService
{
    Task<DiagnosticsSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default);
}

public static class DiagnosticsClassifier
{
    public static HealthClassification Overall(
        IReadOnlyList<DiagnosticValue> checks)
    {
        if (checks.Count == 0)
        {
            return HealthClassification.UNAVAILABLE;
        }

        if (checks.Any(x => x.Classification == HealthClassification.UNAVAILABLE))
        {
            return HealthClassification.UNAVAILABLE;
        }

        if (checks.Any(x => x.Classification == HealthClassification.ACTION_REQUIRED))
        {
            return HealthClassification.ACTION_REQUIRED;
        }

        if (checks.Any(x => x.Classification == HealthClassification.DEGRADED))
        {
            return HealthClassification.DEGRADED;
        }

        return HealthClassification.HEALTHY;
    }
}
