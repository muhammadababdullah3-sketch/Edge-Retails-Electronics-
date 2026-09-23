using System.Collections.Concurrent;
using EdgeRetails.Worker.Jobs;

namespace EdgeRetails.Worker;

public sealed class Worker : BackgroundService
{
    private readonly IEnumerable<IWorkerJob> _jobs;
    private readonly IWorkerJobLock _jobLock;
    private readonly WorkerHeartbeatService _heartbeat;
    private readonly ILogger<Worker> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastExecution = new();
    private readonly ConcurrentDictionary<string, int> _consecutiveFailures = new();

    public Worker(
        IEnumerable<IWorkerJob> jobs,
        IWorkerJobLock jobLock,
        WorkerHeartbeatService heartbeat,
        ILogger<Worker> logger)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _jobLock = jobLock ?? throw new ArgumentNullException(nameof(jobLock));
        _heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("EdgeRetails Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _heartbeat.BeatAsync(stoppingToken);

                var now = DateTimeOffset.UtcNow;
                foreach (var job in _jobs)
                {
                    if (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var lastRun = _lastExecution.GetOrAdd(job.Name, DateTimeOffset.MinValue);
                    if (now - lastRun < job.Interval)
                    {
                        continue;
                    }

                    if (job.IsExclusive)
                    {
                        await using var lease = await _jobLock.TryAcquireJobLockAsync(job.Name, stoppingToken);
                        if (lease is null)
                        {
                            _logger.LogDebug("Exclusive job '{JobName}' is currently held by another worker instance. Skipping.", job.Name);
                            continue;
                        }

                        _lastExecution[job.Name] = DateTimeOffset.UtcNow;
                        await ExecuteJobSafelyAsync(job, stoppingToken);
                    }
                    else
                    {
                        _lastExecution[job.Name] = DateTimeOffset.UtcNow;
                        await ExecuteJobSafelyAsync(job, stoppingToken);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in worker loop.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("EdgeRetails Worker stopped gracefully.");
    }

    private async Task ExecuteJobSafelyAsync(IWorkerJob job, CancellationToken cancellationToken)
    {
        try
        {
            await job.ExecuteAsync(cancellationToken);
            _consecutiveFailures[job.Name] = 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Worker is shutting down
        }
        catch (Exception ex)
        {
            var failures = _consecutiveFailures.AddOrUpdate(job.Name, 1, (_, count) => count + 1);
            _logger.LogError(ex, "Job '{JobName}' threw an unhandled exception (consecutive failures: {Failures}).", job.Name, failures);
        }
    }
}
