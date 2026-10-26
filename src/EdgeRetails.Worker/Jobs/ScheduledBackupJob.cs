using EdgeRetails.Application.Production.Backup;
using EdgeRetails.Infrastructure.Production.Backup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdgeRetails.Worker.Jobs;

public sealed class ScheduledBackupJob : IWorkerJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ScheduledBackupJob> _logger;
    private DateTimeOffset _lastRunUtc = DateTimeOffset.MinValue;

    public string Name => "ScheduledBackup";
    public bool IsExclusive => true;
    public TimeSpan Interval => TimeSpan.FromMinutes(15); // Checks every 15 min if backup is due

    public ScheduledBackupJob(
        IServiceScopeFactory scopeFactory,
        ILogger<ScheduledBackupJob> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        // Example schedule: Run once per day after business hours or if 24 hours elapsed
        if (DateTimeOffset.UtcNow - _lastRunUtc < TimeSpan.FromHours(24))
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider.GetService<CreateBackupHandler>();
        var connection = scope.ServiceProvider.GetService<PostgresConnectionDescriptor>();
        if (handler is null || connection is null)
        {
            return;
        }

        var backupDir = Environment.GetEnvironmentVariable("EDGE_RETAILS_BACKUP_DIR");
        if (string.IsNullOrWhiteSpace(backupDir))
        {
            backupDir = ProductionBackupConfiguration.ResolveBackupDirectory();
        }

        try
        {
            _logger.LogInformation("Starting scheduled daily backup...");
            var result = await handler.HandleAsync(
                new BackupCreateRequest(
                    connection,
                    backupDir,
                    typeof(ScheduledBackupJob).Assembly.GetName().Version?.ToString() ?? "unknown",
                    null,
                    new BackupRetentionPolicy(null, null),
                    Guid.CreateVersion7().ToString("D")),
                cancellationToken);
            _lastRunUtc = DateTimeOffset.UtcNow;
            _logger.LogInformation(
                "Scheduled backup completed. BackupId={BackupId} SizeBytes={SizeBytes}",
                result.Manifest.BackupId,
                result.Manifest.SizeBytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Scheduled backup execution failed.");
        }
    }
}
