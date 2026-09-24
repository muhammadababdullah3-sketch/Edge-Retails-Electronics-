using System.Diagnostics;
using System.Text.Json;
using EdgeRetails.Application.Production.Diagnostics;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Production.Diagnostics;

public sealed class HealthDiagnosticsService : IHealthDiagnosticsService
{
    private readonly EdgeRetailsDbContext _db;
    private readonly DiagnosticsPolicy _policy;
    private readonly string _stateRoot;

    public HealthDiagnosticsService(
        EdgeRetailsDbContext db,
        DiagnosticsPolicy policy,
        string stateRoot)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _stateRoot = Path.GetFullPath(stateRoot ?? throw new ArgumentNullException(nameof(stateRoot)));
    }

    public async Task<DiagnosticsSnapshot> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        var checks = new List<DiagnosticValue>();
        var connection = _db.Database.GetDbConnection();

        var dbLatency = await CaptureDatabaseLatencyAsync(connection, cancellationToken);
        checks.Add(dbLatency);

        var writeSafety = await CaptureWriteSafetyAsync(connection, cancellationToken);
        checks.Add(writeSafety);

        var schema = await CaptureSchemaCompatibilityAsync(cancellationToken);
        checks.Add(schema);

        checks.Add(CaptureDisk());
        checks.Add(CaptureWorkerHeartbeat());
        checks.Add(CaptureBackupAge());
        checks.Add(CaptureRemoteBackupVerification());

        var backlogs = await CaptureOutboxBacklogsAsync(connection, cancellationToken);
        checks.AddRange(backlogs);

        checks.Add(CaptureReconciliationStatus());

        var overall = DiagnosticsClassifier.Overall(checks);
        var metadata = new Dictionary<string,string>(StringComparer.Ordinal)
        {
            ["environment"] = "production-diagnostic-snapshot",
            ["state_root"] = _stateRoot
        };

        return new DiagnosticsSnapshot(
            DateTimeOffset.UtcNow,
            overall,
            checks,
            metadata);
    }

    private async Task<DiagnosticValue> CaptureDatabaseLatencyAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = 5;

            var started = Stopwatch.GetTimestamp();
            await command.ExecuteScalarAsync(cancellationToken);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            return elapsed <= _policy.DbLatencyWarningMilliseconds
                ? new(
                    DiagnosticCodes.DatabaseLatencyHealthy,
                    HealthClassification.HEALTHY,
                    $"Database probe completed in {elapsed:F1} ms.",
                    "No immediate database latency action is required.",
                    elapsed,
                    _policy.DbLatencyWarningMilliseconds)
                : new(
                    DiagnosticCodes.DatabaseLatencyDegraded,
                    HealthClassification.DEGRADED,
                    $"Database probe completed in {elapsed:F1} ms.",
                    "Investigate active database load and query-plan regressions.",
                    elapsed,
                    _policy.DbLatencyWarningMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(
                DiagnosticCodes.DatabaseUnavailable,
                HealthClassification.UNAVAILABLE,
                "Database probe could not be completed.",
                "Restore database connectivity before continuing operational work.");
        }
    }

    private async Task<DiagnosticValue> CaptureWriteSafetyAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT NOT pg_is_in_recovery()";
            command.CommandTimeout = 5;
            var result = await command.ExecuteScalarAsync(cancellationToken);

            if (result is bool writable && writable)
            {
                return new(
                    DiagnosticCodes.DatabaseWriteSafetyHealthy,
                    HealthClassification.HEALTHY,
                    "PostgreSQL reports the current node is writable.",
                    "No immediate write-safety action is required.");
            }

            return new(
                DiagnosticCodes.DatabaseWriteSafetyUnavailable,
                HealthClassification.UNAVAILABLE,
                "PostgreSQL is not currently writable.",
                "Route operational writes to the authoritative writable database.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(
                DiagnosticCodes.DatabaseWriteSafetyUnavailable,
                HealthClassification.UNAVAILABLE,
                "Database write-safety could not be verified.",
                "Do not assume writes are safe until the authoritative database is verified.");
        }
    }

    private async Task<DiagnosticValue> CaptureSchemaCompatibilityAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var readiness = await _db.Database.GetPendingMigrationsAsync(cancellationToken);
            if (!readiness.Any())
            {
                return new(
                    DiagnosticCodes.SchemaCompatible,
                    HealthClassification.HEALTHY,
                    "Database schema has no pending EF migrations.",
                    "Schema compatibility is current.");
            }

            return new(
                DiagnosticCodes.SchemaIncompatible,
                HealthClassification.ACTION_REQUIRED,
                $"{readiness.Count()} pending database migration(s) were detected.",
                "Apply the approved migration set through controlled deployment before normal operations.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new(
                DiagnosticCodes.SchemaIncompatible,
                HealthClassification.UNAVAILABLE,
                "Schema compatibility could not be verified.",
                "Treat schema compatibility as unverified until the database can be inspected.");
        }
    }

    private DiagnosticValue CaptureDisk()
    {
        try
        {
            var root = Path.GetPathRoot(_stateRoot);
            if (string.IsNullOrWhiteSpace(root))
            {
                return new(
                    DiagnosticCodes.DiskDegraded,
                    HealthClassification.UNAVAILABLE,
                    "Disk root could not be resolved.",
                    "Verify the production state path.");
            }

            var drive = new DriveInfo(root);
            var available = drive.AvailableFreeSpace;
            return available >= _policy.DiskFreeWarningBytes
                ? new(
                    DiagnosticCodes.DiskHealthy,
                    HealthClassification.HEALTHY,
                    $"Disk has {available / (1024d * 1024d * 1024d):F1} GiB free.",
                    "No immediate disk action is required.",
                    available,
                    _policy.DiskFreeWarningBytes)
                : new(
                    DiagnosticCodes.DiskDegraded,
                    HealthClassification.DEGRADED,
                    $"Disk has only {available / (1024d * 1024d * 1024d):F1} GiB free.",
                    "Free disk capacity before backup/log/database growth becomes unsafe.",
                    available,
                    _policy.DiskFreeWarningBytes);
        }
        catch
        {
            return new(
                DiagnosticCodes.DiskDegraded,
                HealthClassification.UNAVAILABLE,
                "Disk free space could not be measured.",
                "Verify the production state volume.");
        }
    }

    private DiagnosticValue CaptureWorkerHeartbeat()
    {
        var path = Environment.GetEnvironmentVariable("EDGE_RETAILS_WORKER_HEARTBEAT_PATH");
        path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(_stateRoot, "worker.heartbeat")
            : Path.GetFullPath(path.Trim());

        if (!File.Exists(path))
        {
            return new(
                DiagnosticCodes.WorkerHeartbeatActionRequired,
                HealthClassification.ACTION_REQUIRED,
                "Worker heartbeat has not been observed.",
                "Start or repair the worker process and verify its heartbeat path.");
        }

        try
        {
            var lastUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
            var age = DateTimeOffset.UtcNow - lastUtc;
            return age <= _policy.WorkerHeartbeatMaxAge
                ? new(
                    DiagnosticCodes.WorkerHeartbeatHealthy,
                    HealthClassification.HEALTHY,
                    $"Worker heartbeat age is {age.TotalSeconds:F0} seconds.",
                    "Worker heartbeat is current.",
                    age.TotalSeconds,
                    _policy.WorkerHeartbeatMaxAge.TotalSeconds)
                : new(
                    DiagnosticCodes.WorkerHeartbeatActionRequired,
                    HealthClassification.ACTION_REQUIRED,
                    $"Worker heartbeat is {age.TotalMinutes:F1} minutes old.",
                    "Inspect worker liveness and recover the background worker.");
        }
        catch
        {
            return new(
                DiagnosticCodes.WorkerHeartbeatActionRequired,
                HealthClassification.UNAVAILABLE,
                "Worker heartbeat could not be read.",
                "Verify heartbeat file accessibility.");
        }
    }

    private DiagnosticValue CaptureBackupAge()
    {
        var directory = Environment.GetEnvironmentVariable("EDGE_RETAILS_BACKUP_DIR");
        directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(_stateRoot, "backups")
            : Path.GetFullPath(directory.Trim());

        if (!Directory.Exists(directory))
        {
            return new(
                DiagnosticCodes.BackupUnavailable,
                HealthClassification.ACTION_REQUIRED,
                "Backup directory does not exist.",
                "Run the approved backup workflow and verify its authenticated manifest.");
        }

        var manifest = Directory.EnumerateFiles(directory, "*.manifest.json", SearchOption.TopDirectoryOnly)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

        if (manifest is null)
        {
            return new(
                DiagnosticCodes.BackupUnavailable,
                HealthClassification.ACTION_REQUIRED,
                "No backup manifest is available.",
                "Run the approved backup workflow before relying on disaster recovery.");
        }

        var age = DateTimeOffset.UtcNow - manifest.LastWriteTimeUtc;
        return age <= _policy.BackupMaxAge
            ? new(
                DiagnosticCodes.BackupHealthy,
                HealthClassification.HEALTHY,
                $"Latest local backup manifest is {age.TotalHours:F1} hours old.",
                "Local backup age is within policy.",
                age.TotalHours,
                _policy.BackupMaxAge.TotalHours)
            : new(
                DiagnosticCodes.BackupAgeActionRequired,
                HealthClassification.ACTION_REQUIRED,
                $"Latest local backup manifest is {age.TotalHours:F1} hours old.",
                "Run the approved backup workflow and verify the resulting manifest.");
    }

    private DiagnosticValue CaptureRemoteBackupVerification()
    {
        var configured = Environment.GetEnvironmentVariable("EDGE_RETAILS_REMOTE_BACKUP_VERIFICATION_LEVEL");
        if (Enum.TryParse<RemoteVerificationLevel>(configured, true, out var level) &&
            level >= RemoteVerificationLevel.REMOTE_HASH_VERIFIED)
        {
            return new(
                "backup.remote.verified",
                HealthClassification.HEALTHY,
                $"Configured remote backup verification level is {level}.",
                "Continue normal remote backup verification monitoring.");
        }

        return new(
            DiagnosticCodes.RemoteBackupUnverified,
            HealthClassification.UNAVAILABLE,
            "No concrete remote backup verification authority is configured in the current deployment.",
            "Configure and verify the approved cloud backup adapter before presenting remote recoverability as green.");
    }

    private async Task<IReadOnlyList<DiagnosticValue>> CaptureOutboxBacklogsAsync(
        System.Data.Common.DbConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            if (connection.State != System.Data.ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
SELECT
  count(*) FILTER (WHERE effect_type='PrintDocument' AND status IN (1,2)) AS print_pending,
  count(*) FILTER (WHERE lower(coalesce(last_error,''))='print.outcome_unknown') AS outcome_unknown,
  count(*) FILTER (WHERE status=5) AS action_required,
  count(*) FILTER (WHERE status=4) AS failed_jobs
FROM system.outbox_messages
""";
            command.CommandTimeout = 10;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);

            var printPending = reader.GetInt64(0);
            var outcomeUnknown = reader.GetInt64(1);
            var actionRequired = reader.GetInt64(2);
            var failed = reader.GetInt64(3);

            return
            [
                Backlog(
                    DiagnosticCodes.PrintBacklogHealthy,
                    DiagnosticCodes.PrintBacklogActionRequired,
                    printPending,
                    _policy.PrintBacklogWarningCount,
                    "print backlog",
                    "Drain or reconcile the print outbox backlog."),
                Backlog(
                    DiagnosticCodes.OutcomeUnknownHealthy,
                    DiagnosticCodes.OutcomeUnknownActionRequired,
                    outcomeUnknown,
                    _policy.OutcomeUnknownWarningCount,
                    "OUTCOME_UNKNOWN backlog",
                    "Run the canonical same-operation reconciliation workflow."),
                Backlog(
                    DiagnosticCodes.ActionRequiredBacklogHealthy,
                    DiagnosticCodes.ActionRequiredBacklogActionRequired,
                    actionRequired,
                    _policy.ActionRequiredWarningCount,
                    "ACTION_REQUIRED backlog",
                    "Review and resolve action-required outbox effects."),
                Backlog(
                    DiagnosticCodes.FailedJobsHealthy,
                    DiagnosticCodes.FailedJobsActionRequired,
                    failed,
                    _policy.FailedJobsWarningCount,
                    "failed-job backlog",
                    "Inspect failed background effects and their error codes.")
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return
            [
                new(
                    DiagnosticCodes.PrintBacklogActionRequired,
                    HealthClassification.UNAVAILABLE,
                    "Outbox backlog diagnostics could not be queried.",
                    "Verify the operational outbox before assuming print/background queues are healthy.")
            ];
        }
    }

    private static DiagnosticValue Backlog(
        string healthyCode,
        string actionCode,
        long count,
        int threshold,
        string subject,
        string guidance)
        => count <= threshold
            ? new(
                healthyCode,
                HealthClassification.HEALTHY,
                $"Current {subject}: {count}.",
                "Backlog is within configured warning policy.",
                count,
                threshold)
            : new(
                actionCode,
                HealthClassification.ACTION_REQUIRED,
                $"Current {subject}: {count}.",
                guidance,
                count,
                threshold);

    private DiagnosticValue CaptureReconciliationStatus()
    {
        return new(
            DiagnosticCodes.ReconciliationActionRequired,
            HealthClassification.UNAVAILABLE,
            "No concrete reconciliation-failure authority is configured in the current deployment.",
            "Wire the canonical reconciliation failure source before presenting reconciliation health as green.");
    }

    private enum RemoteVerificationLevel
    {
        UPLOADED = 1,
        REMOTE_HASH_VERIFIED = 2,
        REMOTE_MANIFEST_AUTH_VERIFIED = 3,
        RESTORE_REHEARSAL_VERIFIED = 4
    }
}
