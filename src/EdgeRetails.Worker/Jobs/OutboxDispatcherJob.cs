using EdgeRetails.Application.Production.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdgeRetails.Worker.Jobs;

public sealed class OutboxDispatcherJob : IWorkerJob
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherJob> _logger;

    public string Name => "OutboxDispatcher";
    public bool IsExclusive => true;
    public TimeSpan Interval => TimeSpan.FromSeconds(5);

    public OutboxDispatcherJob(
        IServiceScopeFactory scopeFactory,
        ILogger<OutboxDispatcherJob> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetService<OutboxProcessor>();
        if (processor is null)
        {
            return;
        }

        try
        {
            var processed = await processor.ProcessPendingAsync(batchSize: 20, cancellationToken);
            if (processed > 0)
            {
                _logger.LogInformation("OutboxDispatcher processed {Count} external effect jobs.", processed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error processing outbox messages.");
        }
    }
}
