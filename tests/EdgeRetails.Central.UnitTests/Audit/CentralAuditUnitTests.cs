using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Application.Services;
using EdgeRetails.Central.Domain.Audit;
using EdgeRetails.Central.Domain.Outbox;
using EdgeRetails.Central.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.UnitTests.Audit;

public class CentralAuditUnitTests : IDisposable
{
    private readonly CentralDbContext _dbContext;
    private readonly CentralAuditService _auditService;
    private readonly OutboxDispatcher _outboxDispatcher;

    public CentralAuditUnitTests()
    {
        var options = new DbContextOptionsBuilder<CentralDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new CentralDbContext(options);
        _auditService = new CentralAuditService(_dbContext);
        _outboxDispatcher = new OutboxDispatcher(_dbContext, new InMemoryDeduplicatingOutboxSink());
    }

    [Fact]
    public void MaskPii_RedactsSensitiveFields()
    {
        const string rawJson = """{"username":"admin","password":"SecretPassword123","token":"jwt.xyz.123","secret":"totpseed"}""";
        var masked = _auditService.MaskPii(rawJson);

        Assert.Equal("[REDACTED]", System.Text.Json.Nodes.JsonNode.Parse(masked)!["password"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", System.Text.Json.Nodes.JsonNode.Parse(masked)!["token"]!.GetValue<string>());
        Assert.Equal("[REDACTED]", System.Text.Json.Nodes.JsonNode.Parse(masked)!["secret"]!.GetValue<string>());
        Assert.DoesNotContain("SecretPassword123", masked);
    }

    [Fact]
    public async Task AppendOnlyAuditInterceptor_ThrowsOnUpdate()
    {
        var now = DateTimeOffset.UtcNow;
        var record = CentralAuditRecord.Create(
            "admin1", "admin", Guid.NewGuid(), "LOGIN", "AUTH", null, null,
            Guid.NewGuid().ToString(), null, null, null, "{}", true, now);

        _dbContext.CentralAuditRecords.Add(record);
        await _dbContext.SaveChangesAsync();

        // Attempt to modify record through EF entry
        var entry = _dbContext.Entry(record);
        entry.State = EntityState.Modified;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _dbContext.SaveChangesAsync());
        Assert.Contains("strictly append-only", ex.Message);
    }

    [Fact]
    public async Task AppendOnlyAuditInterceptor_ThrowsOnDelete()
    {
        var now = DateTimeOffset.UtcNow;
        var record = CentralAuditRecord.Create(
            "admin1", "admin", Guid.NewGuid(), "LOGIN", "AUTH", null, null,
            Guid.NewGuid().ToString(), null, null, null, "{}", true, now);

        _dbContext.CentralAuditRecords.Add(record);
        await _dbContext.SaveChangesAsync();

        // Attempt to delete record
        _dbContext.CentralAuditRecords.Remove(record);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _dbContext.SaveChangesAsync());
        Assert.Contains("strictly append-only", ex.Message);
    }

    [Fact]
    public async Task OutboxDispatcher_DispatchesPendingMessages_WithStableMessageId()
    {
        await _outboxDispatcher.EnqueueAsync("LicenseIssued", """{"licenseId":"123"}""");
        var dispatchedCount = await _outboxDispatcher.DispatchPendingMessagesAsync();

        Assert.Equal(1, dispatchedCount);

        var messages = await _dbContext.CentralOutboxMessages.ToListAsync();
        Assert.Single(messages);
        Assert.Equal(OutboxStatus.DISPATCHED, messages[0].Status);
        Assert.NotEqual(Guid.Empty, messages[0].Id); // Stable MessageId
    }

    [Fact]
    public async Task OutboxDispatcher_DeadLetterThreshold_TransitionsToDeadLetter_AfterMaxRetries()
    {
        var failingSink = new FailingOutboxSink();
        var dispatcher = new OutboxDispatcher(_dbContext, failingSink);

        await dispatcher.EnqueueAsync("OrderPaid", """{"orderId":"abc"}""");

        // Dispatch 2 times with maxRetries = 2
        await dispatcher.DispatchPendingMessagesAsync(batchSize: 50, maxRetries: 2);
        var messages = await _dbContext.CentralOutboxMessages.ToListAsync();
        Assert.Single(messages);
        Assert.Equal(OutboxStatus.FAILED, messages[0].Status);
        Assert.Equal(1, messages[0].RetryCount);

        // Second failure reaches maxRetries -> DEAD_LETTER
        await dispatcher.DispatchPendingMessagesAsync(batchSize: 50, maxRetries: 2);
        messages = await _dbContext.CentralOutboxMessages.ToListAsync();
        Assert.Equal(OutboxStatus.DEAD_LETTER, messages[0].Status);
        Assert.Equal(2, messages[0].RetryCount);
        Assert.Contains("Simulated consumer failure", messages[0].LastError);

        // Third attempt does not process DEAD_LETTER
        var count = await dispatcher.DispatchPendingMessagesAsync(batchSize: 50, maxRetries: 2);
        Assert.Equal(0, count);
    }

    private sealed class FailingOutboxSink : IOutboxConsumerSink
    {
        public Task DeliverAsync(Guid messageId, string eventType, string aggregateType, string aggregateId, string payloadJson, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Simulated consumer failure");
        }

        public bool HasProcessed(Guid messageId) => false;
        public int GetDeliveryCount(Guid messageId) => 0;
        public IReadOnlyCollection<Guid> DeliveredMessageIds => Array.Empty<Guid>();
    }

    [Fact]
    public void StructuredRedaction_NestedNumbersArraysAndUnknownPii_AreNotLeaked()
    {
        var masked = _auditService.MaskPii("""{"email":"canary@example.invalid","nested":{"pin":1234,"token":{"raw":"secretcanary"}},"items":["privatecanary",{"phone":"555canary"}],"action":"ASSIGN_ROLE"}""");
        foreach (var secret in new[] { "canary@example.invalid", "1234", "secretcanary", "privatecanary", "555canary" })
        {
            Assert.DoesNotContain(secret, masked);
        }
        Assert.Contains("ASSIGN_ROLE", masked);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }
}
