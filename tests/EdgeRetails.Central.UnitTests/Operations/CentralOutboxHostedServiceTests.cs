using System.Collections.Concurrent;
using System.Text.Json;
using EdgeRetails.Central.Api.Services;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdgeRetails.Central.UnitTests.Operations;

public sealed class CentralOutboxHostedServiceTests
{
    [Fact]
    public async Task Hosted_worker_dispatches_persisted_event_and_reports_fresh_health()
    {
        var databaseName = $"central-outbox-hosted-{Guid.NewGuid():N}";
        var dispatchPolicy = new OutboxDispatchPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));
        var options = new CentralOutboxOptions(true, 10, 3, TimeSpan.FromMilliseconds(10), dispatchPolicy, null);
        var messageId = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CentralDbContext>(builder => builder
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ICentralDbContext>(provider => provider.GetRequiredService<CentralDbContext>());
        services.AddSingleton(options);
        services.AddSingleton<CentralOutboxWorkerState>();
        services.AddSingleton<RecordingSink>();
        services.AddScoped<IOutboxConsumerSink>(provider => provider.GetRequiredService<RecordingSink>());
        services.AddScoped<IOutboxDispatcher>(provider => new OutboxDispatcher(
            provider.GetRequiredService<ICentralDbContext>(), provider.GetRequiredService<IOutboxConsumerSink>(), dispatchPolicy));

        await using var provider = services.BuildServiceProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ICentralDbContext>();
            db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(messageId, "test.worker_probe", "TestAggregate",
                Guid.NewGuid().ToString("D"), "{\"fixture\":true}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var worker = new CentralOutboxHostedService(provider.GetRequiredService<IServiceScopeFactory>(), options,
            provider.GetRequiredService<CentralOutboxWorkerState>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<CentralOutboxHostedService>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(timeout.Token);
        try
        {
            await provider.GetRequiredService<RecordingSink>().WaitForDeliveryAsync(messageId, timeout.Token);
            await WaitForDispatchedAsync(provider, messageId, timeout.Token);
            var status = JsonSerializer.SerializeToElement(provider.GetRequiredService<CentralOutboxWorkerState>().Snapshot());
            Assert.Equal("healthy", status.GetProperty("status").GetString());
            Assert.Equal(1, provider.GetRequiredService<RecordingSink>().GetDeliveryCount(messageId));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task Hosted_worker_reports_degraded_health_after_delivery_is_dead_lettered()
    {
        var databaseName = $"central-outbox-degraded-{Guid.NewGuid():N}";
        var dispatchPolicy = new OutboxDispatchPolicy(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));
        var options = new CentralOutboxOptions(true, 10, 1, TimeSpan.FromMilliseconds(10), dispatchPolicy, null);
        var messageId = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<CentralDbContext>(builder => builder
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ICentralDbContext>(provider => provider.GetRequiredService<CentralDbContext>());
        services.AddSingleton(options);
        services.AddSingleton<CentralOutboxWorkerState>();
        services.AddSingleton<FailingSink>();
        services.AddScoped<IOutboxConsumerSink>(provider => provider.GetRequiredService<FailingSink>());
        services.AddScoped<IOutboxDispatcher>(provider => new OutboxDispatcher(
            provider.GetRequiredService<ICentralDbContext>(), provider.GetRequiredService<IOutboxConsumerSink>(), dispatchPolicy));

        await using var provider = services.BuildServiceProvider();
        await using (var seedScope = provider.CreateAsyncScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<ICentralDbContext>();
            db.CentralOutboxMessages.Add(CentralOutboxMessage.Create(messageId, "test.worker_failure", "TestAggregate",
                Guid.NewGuid().ToString("D"), "{\"fixture\":true}", DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }

        var worker = new CentralOutboxHostedService(provider.GetRequiredService<IServiceScopeFactory>(), options,
            provider.GetRequiredService<CentralOutboxWorkerState>(),
            provider.GetRequiredService<ILoggerFactory>().CreateLogger<CentralOutboxHostedService>());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StartAsync(timeout.Token);
        try
        {
            await provider.GetRequiredService<FailingSink>().WaitForFailureAsync(timeout.Token);
            await WaitForStatusAsync(provider, messageId, "degraded", timeout.Token);
            await using var verifyScope = provider.CreateAsyncScope();
            var db = verifyScope.ServiceProvider.GetRequiredService<ICentralDbContext>();
            var message = await db.CentralOutboxMessages.AsNoTracking().SingleAsync(x => x.Id == messageId, timeout.Token);
            Assert.Equal(OutboxStatus.DEAD_LETTER, message.Status);
            Assert.Equal(1, message.RetryCount);
            Assert.Contains("synthetic consumer failure", message.LastError);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    private static async Task WaitForDispatchedAsync(IServiceProvider provider, Guid messageId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ICentralDbContext>();
            if (await db.CentralOutboxMessages.AsNoTracking().AnyAsync(x => x.Id == messageId && x.Status == OutboxStatus.DISPATCHED, cancellationToken))
            {
                return;
            }
            await Task.Delay(10, cancellationToken);
        }
    }

    private static async Task WaitForStatusAsync(IServiceProvider provider, Guid messageId, string expectedStatus, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = JsonSerializer.SerializeToElement(provider.GetRequiredService<CentralOutboxWorkerState>().Snapshot());
            if (status.GetProperty("status").GetString() == expectedStatus)
            {
                await using var scope = provider.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<ICentralDbContext>();
                if (await db.CentralOutboxMessages.AsNoTracking().AnyAsync(x => x.Id == messageId && x.Status == OutboxStatus.DEAD_LETTER, cancellationToken))
                {
                    return;
                }
            }
            await Task.Delay(10, cancellationToken);
        }
    }

    private sealed class RecordingSink : IOutboxConsumerSink
    {
        private readonly ConcurrentDictionary<Guid, int> _delivered = new();
        private readonly TaskCompletionSource<Guid> _firstDelivery = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(Guid messageId, string eventType, string aggregateType, string aggregateId, string payloadJson,
            CancellationToken cancellationToken = default)
        {
            _delivered.AddOrUpdate(messageId, 1, (_, count) => count + 1);
            _firstDelivery.TrySetResult(messageId);
            return Task.CompletedTask;
        }

        public async Task WaitForDeliveryAsync(Guid messageId, CancellationToken cancellationToken)
        {
            var actual = await _firstDelivery.Task.WaitAsync(cancellationToken);
            Assert.Equal(messageId, actual);
        }

        public bool HasProcessed(Guid messageId) => _delivered.ContainsKey(messageId);
        public int GetDeliveryCount(Guid messageId) => _delivered.GetValueOrDefault(messageId);
        public IReadOnlyCollection<Guid> DeliveredMessageIds => _delivered.Keys.ToArray();
    }

    private sealed class FailingSink : IOutboxConsumerSink
    {
        private readonly TaskCompletionSource<Guid> _failure = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DeliverAsync(Guid messageId, string eventType, string aggregateType, string aggregateId, string payloadJson,
            CancellationToken cancellationToken = default)
        {
            _failure.TrySetResult(messageId);
            throw new InvalidOperationException("synthetic consumer failure");
        }

        public async Task WaitForFailureAsync(CancellationToken cancellationToken) => await _failure.Task.WaitAsync(cancellationToken);
        public bool HasProcessed(Guid messageId) => false;
        public int GetDeliveryCount(Guid messageId) => 0;
        public IReadOnlyCollection<Guid> DeliveredMessageIds => Array.Empty<Guid>();
    }
}
