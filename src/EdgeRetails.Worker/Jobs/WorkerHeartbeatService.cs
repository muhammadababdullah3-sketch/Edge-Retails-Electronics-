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
            var dir = Path.GetDirectoryName(_heartbeatPath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = _heartbeatPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temp, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
            File.Move(temp, _heartbeatPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to write worker heartbeat to '{HeartbeatPath}'.", _heartbeatPath);
        }
    }
}
