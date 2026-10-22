using EdgeRetails.Central.Application.Services;

namespace EdgeRetails.Central.Api.Services;

public sealed record CentralStorageOptions(bool Enabled, int BatchSize, TimeSpan Poll, TimeSpan PendingGrace)
{
    public static CentralStorageOptions FromEnvironment()
    {
        if (!bool.TryParse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_RECONCILIATION_ENABLED"), out var enabled) || !enabled ||
            !int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_RECONCILIATION_BATCH_SIZE"), out var batch) || batch is < 1 or > 1000 ||
            !int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_RECONCILIATION_POLL_MS"), out var poll) || poll is < 10 or > 3600000 ||
            !int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_STORAGE_PENDING_GRACE_SECONDS"), out var grace) || grace is < 60 or > 86400)
        {
            return new(false, 0, TimeSpan.Zero, TimeSpan.Zero);
        }
        return new(true, batch, TimeSpan.FromMilliseconds(poll), TimeSpan.FromSeconds(grace));
    }
}

public sealed class CentralStorageHostedService(IServiceScopeFactory scopes, CentralStorageOptions options,
    ILogger<CentralStorageHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogWarning("Central storage reconciliation is unavailable without explicit valid configuration.");
            return;
        }
        var sessionOffset = 0;
        var tombstoneOffset = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<StorageReconciliationService>();
                var now = DateTimeOffset.UtcNow;
                var result = await service.ReconcileAsync(sessionOffset, tombstoneOffset, options.BatchSize,
                    now, now - options.PendingGrace, stoppingToken);
                sessionOffset = result.SessionsScanned < options.BatchSize ? 0 : sessionOffset + result.SessionsScanned;
                tombstoneOffset = result.TombstonesScanned < options.BatchSize ? 0 : tombstoneOffset + result.TombstonesScanned;
                if (result.ExpiredDownloadGrantsRemoved > 0)
                {
                    logger.LogInformation("Central storage reconciliation removed {ExpiredDownloadGrants} expired backup download grants.", result.ExpiredDownloadGrantsRemoved);
                }
                if (result.Inconsistent > 0 || result.PendingDeferred > 0)
                {
                    logger.LogWarning("Central storage reconciliation found {Inconsistent} inconsistent records and {Deferred} deferred publications.", result.Inconsistent, result.PendingDeferred);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogWarning("Central storage reconciliation failed ({FailureType}); processing will retry.", exception.GetType().Name);
            }
            try { await Task.Delay(options.Poll, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
