using EdgeRetails.Application.Production.Diagnostics;
using EdgeRetails.Infrastructure.Production.Backup;

namespace EdgeRetails.Infrastructure.Production.Diagnostics;

public sealed class BackupHealthProbe : IBackupHealthProbe
{
    private readonly BackupHistoryService _history;
    private readonly string _backupDirectory;

    public BackupHealthProbe(BackupHistoryService history, string backupDirectory)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _backupDirectory = backupDirectory ?? throw new ArgumentNullException(nameof(backupDirectory));
    }

    public async Task<BackupHealthResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var diagnostics = await _history.ReadDiagnosticsAsync(_backupDirectory, cancellationToken);
        var latest = diagnostics.ValidBackups.OrderByDescending(x => x.CreatedAtUtc).FirstOrDefault();
        if (diagnostics.Issues.Count > 0)
        {
            return new(BackupHealthState.Degraded, latest, diagnostics.ValidBackups.Count, diagnostics.Issues.Count,
                "Backup history contains one or more invalid, tampered, or inaccessible manifest entries.");
        }

        if (latest is null)
        {
            return new(BackupHealthState.NoBackups, null, 0, 0, "No verified production backup is available yet.");
        }

        return new(BackupHealthState.Healthy, latest, diagnostics.ValidBackups.Count, 0, "Authenticated backup metadata/history is healthy; restore performs full artifact checksum verification.");
    }
}

public sealed class FileWorkerHeartbeatProbe : IWorkerHeartbeatProbe
{
    private readonly string _heartbeatPath;
    private readonly TimeSpan _maximumAge;

    public FileWorkerHeartbeatProbe(string heartbeatPath, TimeSpan maximumAge)
    {
        _heartbeatPath = Path.GetFullPath(heartbeatPath ?? throw new ArgumentNullException(nameof(heartbeatPath)));
        if (maximumAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge));
        }

        _maximumAge = maximumAge;
    }

    public Task<WorkerHeartbeatResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_heartbeatPath))
        {
            return Task.FromResult(new WorkerHeartbeatResult(false, null, "Worker heartbeat has not been observed."));
        }

        var last = File.GetLastWriteTimeUtc(_heartbeatPath);
        var lastUtc = new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc));
        var healthy = DateTimeOffset.UtcNow - lastUtc <= _maximumAge;
        return Task.FromResult(new WorkerHeartbeatResult(
            healthy,
            lastUtc,
            healthy ? "Worker heartbeat is current." : "Worker heartbeat is stale."));
    }
}

public sealed class DriveDiskSpaceProbe : IDiskSpaceProbe
{
    private readonly string _path;
    private readonly long _minimumFreeBytes;

    public DriveDiskSpaceProbe(string path, long minimumFreeBytes)
    {
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
        if (minimumFreeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumFreeBytes));
        }

        _minimumFreeBytes = minimumFreeBytes;
    }

    public Task<DiskSpaceResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.GetPathRoot(_path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Task.FromResult(new DiskSpaceResult(false, null, "Disk root could not be resolved."));
        }

        var drive = new DriveInfo(root);
        var available = drive.AvailableFreeSpace;
        return Task.FromResult(new DiskSpaceResult(
            available >= _minimumFreeBytes,
            available,
            available >= _minimumFreeBytes ? "Disk free space is healthy." : "Disk free space is below the configured production threshold."));
    }
}

public sealed class PostgresToolchainReadinessProbe : IPostgresToolchainReadinessProbe
{
    private readonly IReadOnlyDictionary<string, string> _tools;
    private readonly IPostgresProcessRunner _runner;

    public PostgresToolchainReadinessProbe(
        string pgDumpPath,
        string pgRestorePath,
        string psqlPath,
        string createdbPath,
        IPostgresProcessRunner? runner = null)
    {
        _tools = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["pg_dump"] = pgDumpPath,
            ["pg_restore"] = pgRestorePath,
            ["psql"] = psqlPath,
            ["createdb"] = createdbPath
        };
        _runner = runner ?? new ProcessRunner();
    }

    public async Task<PostgresToolchainResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var versions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, path) in _tools)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return new(false, $"Required PostgreSQL client tool '{name}' is not available.", versions);
            }

            var result = await _runner.RunAsync(path, new[] { "--version" }, null, cancellationToken);
            if (result.ExitCode != 0)
            {
                return new(false, $"Required PostgreSQL client tool '{name}' failed its readiness probe.", versions);
            }

            versions[name] = result.StandardOutput.Trim();
        }
        return new(true, "PostgreSQL backup/restore client toolchain is ready.", versions);
    }
}
