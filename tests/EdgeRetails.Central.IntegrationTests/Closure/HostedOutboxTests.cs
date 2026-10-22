using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdgeRetails.Central.Api.Services;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Commercial;
using EdgeRetails.Central.Domain.Identity;
using EdgeRetails.Central.Domain.Licensing;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EdgeRetails.Central.IntegrationTests.Closure;

public sealed class HostedOutboxTests
{
    private static CentralDbContext Open() => new CentralDbContextFactory().CreateDbContext([]);
    private static readonly OutboxDispatchPolicy FixturePolicy = new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(100));

    private sealed class Host(Guid? poison = null) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<CentralOutboxOptions>();
                services.AddSingleton(new CentralOutboxOptions(true, 500, 2, TimeSpan.FromMilliseconds(50), FixturePolicy, null));
                if (poison is not null)
                {
                    services.RemoveAll<IOutboxConsumerSink>();
                    services.AddScoped<IOutboxConsumerSink>(sp => new PoisonSink(sp.GetRequiredService<ICentralDbContext>(), poison.Value));
                }
            });
        }
    }

    private sealed class PoisonSink(ICentralDbContext db, Guid poison) : IOutboxConsumerSink
    {
        private readonly DurableOutboxConsumerSink _inner = new(db);
        public Task DeliverAsync(Guid id, string type, string aggregate, string target, string json, CancellationToken ct = default) =>
            id == poison ? Task.FromException(new InvalidOperationException("synthetic poison")) : _inner.DeliverAsync(id, type, aggregate, target, json, ct);
        public bool HasProcessed(Guid id) => _inner.HasProcessed(id);
        public int GetDeliveryCount(Guid id) => _inner.GetDeliveryCount(id);
        public IReadOnlyCollection<Guid> DeliveredMessageIds => _inner.DeliveredMessageIds;
    }

    private sealed class LoseAckSink(ICentralDbContext db, Guid target, CancellationTokenSource stop) : IOutboxConsumerSink
    {
        private readonly DurableOutboxConsumerSink _inner = new(db);
        public async Task DeliverAsync(Guid id, string type, string aggregate, string entity, string json, CancellationToken ct = default)
        {
            await _inner.DeliverAsync(id, type, aggregate, entity, json, ct);
            if (id == target) { stop.Cancel(); ct.ThrowIfCancellationRequested(); }
        }
        public bool HasProcessed(Guid id) => _inner.HasProcessed(id);
        public int GetDeliveryCount(Guid id) => _inner.GetDeliveryCount(id);
        public IReadOnlyCollection<Guid> DeliveredMessageIds => _inner.DeliveredMessageIds;
    }

    private static async Task<Guid> Seed()
    {
        await using var db = Open();
        var now = DateTimeOffset.UtcNow;
        var customer = Customer.Create("Synthetic worker fixture", $"worker-{Guid.NewGuid():N}@example.invalid", "", now);
        var shop = Shop.Create(customer.Id, Guid.NewGuid().ToString("N"), "Synthetic worker shop", now);
        var review = HardwareReview.Create(shop.Id, Guid.NewGuid(), "synthetic-new", "synthetic-old", "Fixture review", now);
        var message = CentralOutboxMessage.Create(Guid.NewGuid(), "activation.hardware_review_requested", nameof(HardwareReview), review.Id.ToString("D"), "{}", now);
        db.Customers.Add(customer); db.Shops.Add(shop); db.HardwareReviews.Add(review); db.CentralOutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }

    private static async Task<Guid> SeedAdminSession(params string[] capabilities)
    {
        await using var db = Open();
        var now = DateTimeOffset.UtcNow;
        var admin = AdminUser.Create($"worker-status-{Guid.NewGuid():N}", "synthetic", "synthetic", null, false, now);
        var sessionId = Guid.NewGuid();
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId.ToString()))).ToLowerInvariant();
        var session = AdminSession.Create(admin.Id, token, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, admin.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty("Id")!.SetValue(session, sessionId);
        db.AdminUsers.Add(admin); db.AdminSessions.Add(session); await db.SaveChangesAsync();
        return sessionId;
    }

    private static async Task WaitFor(Func<CentralDbContext, Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!timeout.IsCancellationRequested)
        {
            await using var db = Open();
            if (await predicate(db))
            {
                return;
            }

            await Task.Delay(50, timeout.Token);
        }
        throw new TimeoutException("Expected durable worker effect was not observed.");
    }

    [Fact]
    public async Task WorkerStatus_RequiresFreshAdminSessionAndNotificationsCapability()
    {
        await using var host = new Host();
        using var anonymous = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        using var wrong = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        wrong.DefaultRequestHeaders.Add("X-Session-Id", (await SeedAdminSession("dashboard.view")).ToString());
        using var allowed = host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        allowed.DefaultRequestHeaders.Add("X-Session-Id", (await SeedAdminSession("notifications.view")).ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/v1/notifications/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await wrong.GetAsync("/api/admin/v1/notifications/status")).StatusCode);
        using var response = await allowed.GetAsync("/api/admin/v1/notifications/status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(json.RootElement.GetProperty("status").GetString(), new[] { "unavailable", "unknown", "degraded", "healthy" });
        Assert.True(response.Headers.CacheControl?.NoStore ?? false);
    }

    private static async Task<Guid> Session(params string[] capabilities)
    {
        await using var db = Open();
        var now = DateTimeOffset.UtcNow;
        var user = AdminUser.Create("worker_" + Guid.NewGuid().ToString("N"), "fixture", "fixture", null, false, now);
        var role = AdminRole.Create("worker_" + Guid.NewGuid().ToString("N"), "Synthetic worker authorization", capabilities);
        var id = Guid.NewGuid();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString()))).ToLowerInvariant();
        var session = AdminSession.Create(user.Id, hash, now, TimeSpan.FromMinutes(15), TimeSpan.FromHours(8), true, user.SecurityRevision, capabilities);
        typeof(AdminSession).GetProperty(nameof(AdminSession.Id))!.SetValue(session, id);
        db.AdminUsers.Add(user); db.AdminRoles.Add(role); db.AdminUserRoles.Add(new(user.Id, role.Id, now)); db.AdminSessions.Add(session);
        await db.SaveChangesAsync();
        return id;
    }

    [Fact]
    public async Task ActualHost_AutonomouslyProjectsNotification_AndFiltersByDomainCapability()
    {
        var id = await Seed();
        await using var host = new Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        await WaitFor(db => db.AdminNotifications.AnyAsync(n => n.Id == id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/v1/notifications")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Session-Id", (await Session("notifications.view")).ToString());
        var hidden = await client.GetFromJsonAsync<JsonElement>("/api/admin/v1/notifications");
        Assert.DoesNotContain(hidden.GetProperty("items").EnumerateArray(), n => n.GetProperty("id").GetGuid() == id);
        client.DefaultRequestHeaders.Remove("X-Session-Id");
        client.DefaultRequestHeaders.Add("X-Session-Id", (await Session("notifications.view", "hardware.review")).ToString());
        var visible = await client.GetFromJsonAsync<JsonElement>("/api/admin/v1/notifications?pageSize=100");
        Assert.Contains(visible.GetProperty("items").EnumerateArray(), n => n.GetProperty("id").GetGuid() == id);
        await using var verify = Open();
        var notice = await verify.AdminNotifications.SingleAsync(n => n.Id == id);
        Assert.Equal(HardwareReview.StatusPending, (await verify.HardwareReviews.SingleAsync(r => r.Id == notice.TargetId)).Status);
        Assert.Equal(1, await verify.CentralConsumerEffects.CountAsync(e => e.MessageId == id));
    }

    [Fact]
    public async Task ConcurrentClaims_AndLostAckExpiredLease_ProduceOneDurableEffect()
    {
        var race = await Seed();
        await using (var a = Open())
        await using (var b = Open())
        {
            await Task.WhenAll(
                new OutboxDispatcher(a, new DurableOutboxConsumerSink(a), FixturePolicy).DispatchPendingMessagesAsync(1000),
                new OutboxDispatcher(b, new DurableOutboxConsumerSink(b), FixturePolicy).DispatchPendingMessagesAsync(1000));
        }

        await using (var verify = Open())
        {
            Assert.Equal(1, await verify.AdminNotifications.CountAsync(n => n.Id == race));
            Assert.Equal(1, await verify.CentralConsumerEffects.CountAsync(n => n.MessageId == race));
        }
        var lostAck = await Seed();
        using var stop = new CancellationTokenSource();
        await using (var beforeRestart = Open())
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                new OutboxDispatcher(beforeRestart, new LoseAckSink(beforeRestart, lostAck, stop), FixturePolicy)
                    .DispatchPendingMessagesAsync(1000, cancellationToken: stop.Token));
        }

        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        await using (var afterRestart = Open())
        {
            await new OutboxDispatcher(afterRestart, new DurableOutboxConsumerSink(afterRestart), FixturePolicy).DispatchPendingMessagesAsync(1000);
        }

        await using var final = Open();
        Assert.Equal(OutboxStatus.DISPATCHED, (await final.CentralOutboxMessages.SingleAsync(m => m.Id == lostAck)).Status);
        Assert.Equal(1, await final.AdminNotifications.CountAsync(n => n.Id == lostAck));
        Assert.Equal(1, await final.CentralConsumerEffects.CountAsync(n => n.MessageId == lostAck));
    }

    [Fact]
    public async Task Consumer_RejectsAlteredOrMissingEvent_WithoutCreatingReceiptOrNotification()
    {
        var id = await Seed();
        await using var db = Open();
        var message = await db.CentralOutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id);
        var sink = new DurableOutboxConsumerSink(db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.DeliverAsync(id, message.EventType,
            message.AggregateType, message.AggregateId, "{\"altered\":true}"));
        Assert.False(await db.CentralConsumerEffects.AnyAsync(e => e.MessageId == id));
        Assert.False(await db.AdminNotifications.AnyAsync(e => e.Id == id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.DeliverAsync(Guid.NewGuid(), message.EventType,
            message.AggregateType, message.AggregateId, message.PayloadJson));
        await sink.DeliverAsync(id, message.EventType, message.AggregateType, message.AggregateId, message.PayloadJson);
        await sink.DeliverAsync(id, message.EventType, message.AggregateType, message.AggregateId, message.PayloadJson);
        Assert.Equal(1, await db.CentralConsumerEffects.CountAsync(e => e.MessageId == id));
        Assert.Equal(1, await db.AdminNotifications.CountAsync(e => e.Id == id));
    }

    [Fact]
    public async Task ScheduledPoison_BackoffAndDeadLetter_DoNotBlockLaterNotification()
    {
        var poison = await Seed();
        var good = await Seed();
        await using var host = new Host(poison);
        using var client = host.CreateClient();
        await WaitFor(db => db.CentralOutboxMessages.AnyAsync(m => m.Id == poison && m.Status == OutboxStatus.DEAD_LETTER));
        await WaitFor(db => db.AdminNotifications.AnyAsync(n => n.Id == good));
        await using var verify = Open();
        var failed = await verify.CentralOutboxMessages.SingleAsync(m => m.Id == poison);
        Assert.Equal(2, failed.RetryCount);
        Assert.NotNull(failed.NextAttemptAtUtc);
        Assert.False(await verify.AdminNotifications.AnyAsync(n => n.Id == poison));
        Assert.Equal(1, await verify.AdminNotifications.CountAsync(n => n.Id == good));
    }
}
