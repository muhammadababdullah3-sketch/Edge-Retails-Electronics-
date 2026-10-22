using EdgeRetails.Central.Application.Abstractions;

namespace EdgeRetails.Central.Api.Services;

public sealed class ActivationDeliveryCleanupOptions(bool enabled, int batchSize, TimeSpan pollInterval)
{
    public bool Enabled { get; } = enabled;
    public int BatchSize { get; } = batchSize;
    public TimeSpan PollInterval { get; } = pollInterval;

    public static ActivationDeliveryCleanupOptions FromEnvironment()
    {
        if (!bool.TryParse(Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_CLEANUP_ENABLED"), out var enabled) || !enabled ||
            !int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_CLEANUP_BATCH_SIZE"), out var batch) || batch is < 1 or > 1000 ||
            !int.TryParse(Environment.GetEnvironmentVariable("CENTRAL_ACTIVATION_DELIVERY_CLEANUP_POLL_MS"), out var poll) || poll is < 100 or > 86_400_000)
        {
            return new(false, 0, TimeSpan.Zero);
        }

        return new(true, batch, TimeSpan.FromMilliseconds(poll));
    }
}

public sealed class ActivationDeliveryCleanupHostedService(
    IServiceScopeFactory scopes,
    ActivationDeliveryCleanupOptions options,
    ILogger<ActivationDeliveryCleanupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogWarning("Activation delivery-secret cleanup is unavailable without explicit valid configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var activation = scope.ServiceProvider.GetRequiredService<IActivationService>();
                var removed = await activation.ExpirePendingActivationDeliverySecretsAsync(options.BatchSize, DateTimeOffset.UtcNow, stoppingToken);
                if (removed > 0)
                {
                    logger.LogInformation("Expired {Count} pending activation delivery secrets.", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning("Activation delivery-secret cleanup failed ({FailureType}); processing will retry.", exception.GetType().Name);
            }

            try { await Task.Delay(options.PollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
