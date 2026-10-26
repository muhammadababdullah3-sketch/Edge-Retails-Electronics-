using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Api.Services;

public sealed record CentralOutboxOptions(bool Enabled, int BatchSize, int MaxAttempts, TimeSpan Poll, OutboxDispatchPolicy Policy, string? UnavailableReason)
{
    public static CentralOutboxOptions FromEnvironment()
    {
        var unavailable = new CentralOutboxOptions(false, 0, 0, TimeSpan.Zero, OutboxDispatchPolicy.ContractHarness, "WORKER_CONFIG_NOT_ACTIVE");
        if (!bool.TryParse(Environment.GetEnvironmentVariable("CENTRAL_OUTBOX_ENABLED"), out var enabled) || !enabled)
        {
            return unavailable;
        }

        if (!Read("CENTRAL_OUTBOX_BATCH_SIZE", 1, 1000, out var batch) ||
            !Read("CENTRAL_OUTBOX_MAX_ATTEMPTS", 1, 100, out var attempts) ||
            !Read("CENTRAL_OUTBOX_POLL_MS", 10, 3600000, out var poll) ||
            !Read("CENTRAL_OUTBOX_LEASE_SECONDS", 1, 3600, out var lease) ||
            !Read("CENTRAL_OUTBOX_RETRY_BASE_MS", 1, 3600000, out var retry) ||
            !Read("CENTRAL_OUTBOX_RETRY_MAX_MS", retry, 86400000, out var cap))
        {
            return unavailable with { UnavailableReason = "WORKER_CONFIG_INVALID" };
        }

        return new(true, batch, attempts, TimeSpan.FromMilliseconds(poll),
            new(TimeSpan.FromSeconds(lease), TimeSpan.FromMilliseconds(retry), TimeSpan.FromMilliseconds(cap)), null);
    }
    private static bool Read(string name, int min, int max, out int value) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out value) && value >= min && value <= max;
}

public sealed class CentralOutboxWorkerState(CentralOutboxOptions options)
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastFailure;
    private bool _deliveryFailures;
    public void Succeeded(bool deliveryFailures) { lock (_gate) { _lastSuccess = DateTimeOffset.UtcNow; _deliveryFailures = deliveryFailures; } }
    public void Failed() { lock (_gate)
        {
            _lastFailure = DateTimeOffset.UtcNow;
        }
    }
    public object Snapshot()
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            var fresh = _lastSuccess is not null && now - _lastSuccess <= options.Policy.Lease + options.Poll * 2;
            var status = !options.Enabled ? "unavailable" : _lastFailure > _lastSuccess || !fresh ? "unknown" : _deliveryFailures ? "degraded" : "healthy";
            return new { status, observedAtUtc = now, lastSucceededAtUtc = _lastSuccess, code = options.UnavailableReason };
        }
    }
}

public sealed class CentralOutboxHostedService(IServiceScopeFactory scopes, CentralOutboxOptions options,
    CentralOutboxWorkerState state, ILogger<CentralOutboxHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogWarning("Central outbox is unavailable: {Code}", options.UnavailableReason);
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                await dispatcher.DispatchPendingMessagesAsync(options.BatchSize, options.MaxAttempts, stoppingToken);
                var db = scope.ServiceProvider.GetRequiredService<ICentralDbContext>();
                state.Succeeded(await db.CentralOutboxMessages.AnyAsync(
                    m => m.Status == OutboxStatus.FAILED || m.Status == OutboxStatus.DEAD_LETTER, stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                state.Failed();
                logger.LogWarning("Central outbox poll failed ({FailureType}); processing will retry.", exception.GetType().Name);
            }
            try { await Task.Delay(options.Poll, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
