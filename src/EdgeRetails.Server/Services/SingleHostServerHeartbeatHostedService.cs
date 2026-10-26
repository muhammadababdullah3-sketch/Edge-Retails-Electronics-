using EdgeRetails.Infrastructure.Services;

namespace EdgeRetails.Server.Services;

public sealed class SingleHostServerHeartbeatHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private readonly ILogger<SingleHostServerHeartbeatHostedService> _logger;
    private readonly string _heartbeatPath;

    public SingleHostServerHeartbeatHostedService(ILogger<SingleHostServerHeartbeatHostedService> logger)
    {
        _logger = logger;
        _heartbeatPath = SingleHostHeartbeatFileWriter.ResolveServerHeartbeatPath();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SingleHostHeartbeatFileWriter.WriteAsync(_heartbeatPath, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to write server heartbeat to '{Path}'.", _heartbeatPath);
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
