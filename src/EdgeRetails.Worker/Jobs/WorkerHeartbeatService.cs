namespace EdgeRetails.Worker.Jobs;

public sealed class WorkerHeartbeatService
{
    private readonly string _heartbeatPath;
    private readonly ILogger<WorkerHeartbeatService> _logger;

    public WorkerHeartbeatService(string heartbeatPath, ILogger<WorkerHeartbeatService> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(heartbeatPath);
        _heartbeatPath = Path.GetFullPath(heartbeatPath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task BeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EdgeRetails.Infrastructure.Services.SingleHostHeartbeatFileWriter.WriteAsync(
                _heartbeatPath,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write worker heartbeat to '{HeartbeatPath}'.", _heartbeatPath);
        }
    }
}
