using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Domain.Outbox;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Central.Application.Services;

public class OutboxDispatcher : IOutboxDispatcher
{
    private readonly ICentralDbContext _dbContext;
    private readonly IOutboxConsumerSink _consumerSink;
    private readonly OutboxDispatchPolicy _policy;

    public OutboxDispatcher(
        ICentralDbContext dbContext,
        IOutboxConsumerSink? consumerSink = null,
        OutboxDispatchPolicy? policy = null)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _consumerSink = consumerSink ?? throw new InvalidOperationException("Explicit durable consumer sink required.");
        // Direct two-argument construction is the existing contract-test harness.
        // The host always supplies a separately validated explicit policy.
        _policy = policy ?? OutboxDispatchPolicy.ContractHarness;
    }

    public async Task EnqueueAsync(
        string messageType,
        string payloadJson,
        Guid? clientOperationId = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid();
        var message = CentralOutboxMessage.Create(
            messageId: messageId,
            eventType: messageType,
            aggregateType: "CentralAggregate",
            aggregateId: clientOperationId?.ToString() ?? messageId.ToString(),
            payloadJson: payloadJson,
            occurredOnUtc: now);

        _dbContext.CentralOutboxMessages.Add(message);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> DispatchPendingMessagesAsync(
        int batchSize = 50,
        int maxRetries = CentralOutboxMessage.DefaultMaxRetries,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1 || batchSize > 1000 || maxRetries < 1 || maxRetries > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var now = DateTimeOffset.UtcNow;
        var token = Guid.NewGuid();
        var leaseUntil = now + _policy.Lease;
        var relational = _dbContext.Database.IsRelational();
        List<CentralOutboxMessage> pendingMessages;
        if (relational)
        {
            // The atomic update is the claim transaction. Competing workers skip
            // locked rows; a dead process's persisted lease expires naturally.
            pendingMessages = await _dbContext.CentralOutboxMessages.FromSqlInterpolated($"""
                WITH candidates AS (
                  SELECT "Id" FROM central.outbox_messages
                  WHERE "Status" IN ('PENDING','FAILED')
                    AND ("NextAttemptAtUtc" IS NULL OR "NextAttemptAtUtc" <= {now})
                    AND ("ClaimExpiresAtUtc" IS NULL OR "ClaimExpiresAtUtc" <= {now})
                  ORDER BY "OccurredOnUtc", "Id"
                  FOR UPDATE SKIP LOCKED LIMIT {batchSize}
                )
                UPDATE central.outbox_messages m
                SET "ClaimToken"={token}, "ClaimExpiresAtUtc"={leaseUntil}
                FROM candidates c WHERE m."Id"=c."Id"
                RETURNING m.*
                """).AsNoTracking().ToListAsync(cancellationToken);
        }
        else
        {
            pendingMessages = await _dbContext.CentralOutboxMessages
                .Where(m => (m.Status == OutboxStatus.PENDING || m.Status == OutboxStatus.FAILED)
                    && (m.NextAttemptAtUtc == null || m.NextAttemptAtUtc <= now)
                    && (m.ClaimExpiresAtUtc == null || m.ClaimExpiresAtUtc <= now))
                .OrderBy(m => m.OccurredOnUtc).Take(batchSize).ToListAsync(cancellationToken);
            foreach (var m in pendingMessages)
            {
                m.Claim(token, leaseUntil);
            }

            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        var processedCount = 0;

        foreach (var message in pendingMessages)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _consumerSink.DeliverAsync(
                    message.Id,
                    message.EventType,
                    message.AggregateType,
                    message.AggregateId,
                    message.PayloadJson,
                    cancellationToken);

                var completed = DateTimeOffset.UtcNow;
                if (relational)
                {
                    var acknowledged = await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE central.outbox_messages SET "Status"='DISPATCHED',
                          "ProcessedAtUtc"={completed}, "ClaimToken"=NULL,
                          "ClaimExpiresAtUtc"=NULL, "NextAttemptAtUtc"=NULL
                        WHERE "Id"={message.Id} AND "ClaimToken"={token}
                        """, cancellationToken);
                    processedCount += acknowledged;
                }
                else
                {
                    message.MarkDispatched(completed);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    processedCount++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { throw; }
            catch (Exception ex)
            {
                var retryAt = DateTimeOffset.UtcNow + _policy.Delay(message.RetryCount + 1);
                var error = $"consumer.delivery_failed: {ex.Message}";
                if (relational)
                {
                    await _dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE central.outbox_messages SET "RetryCount"="RetryCount"+1,
                          "Status"=CASE WHEN "RetryCount"+1 >= {maxRetries} THEN 'DEAD_LETTER' ELSE 'FAILED' END,
                          "LastError"={error}, "NextAttemptAtUtc"={retryAt},
                          "ClaimToken"=NULL, "ClaimExpiresAtUtc"=NULL
                        WHERE "Id"={message.Id} AND "ClaimToken"={token}
                        """, cancellationToken);
                }
                else
                {
                    message.MarkFailed(error, maxRetries, retryAt);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        return processedCount;
    }
}

public sealed record OutboxDispatchPolicy(TimeSpan Lease, TimeSpan RetryBase, TimeSpan RetryMaximum)
{
    public static OutboxDispatchPolicy ContractHarness { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.Zero);
    public TimeSpan Delay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(
        RetryMaximum.TotalMilliseconds, RetryBase.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 30))));
}
