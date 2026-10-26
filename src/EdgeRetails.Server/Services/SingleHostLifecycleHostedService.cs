using EdgeRetails.Application.Abstractions;

namespace EdgeRetails.Server.Services;

/// <summary>Marks server drain on shutdown so in-flight mutations fail closed (Phase 8).</summary>
public sealed class SingleHostLifecycleHostedService : IHostedService
{
    private readonly ISingleHostLifecycleState _lifecycle;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SingleHostLifecycleHostedService> _logger;

    public SingleHostLifecycleHostedService(
        ISingleHostLifecycleState lifecycle,
        IHostApplicationLifetime lifetime,
        ILogger<SingleHostLifecycleHostedService> logger)
    {
        _lifecycle = lifecycle;
        _lifetime = lifetime;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _lifetime.ApplicationStopping.Register(() =>
        {
            _lifecycle.BeginDrain();
            _logger.LogInformation("Single-host server lifecycle entered drain state.");
        });

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
