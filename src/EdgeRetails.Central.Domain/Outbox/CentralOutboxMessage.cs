using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Outbox;

public class CentralOutboxMessage : Entity
{
    public DateTimeOffset OccurredOnUtc { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string AggregateType { get; private set; } = string.Empty;
    public string AggregateId { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = string.Empty;
    public OutboxStatus Status { get; private set; }
    public DateTimeOffset? ProcessedAtUtc { get; private set; }
    public int RetryCount { get; private set; }
    public string? LastError { get; private set; }
    public Guid? ClaimToken { get; private set; }
    public DateTimeOffset? ClaimExpiresAtUtc { get; private set; }
    public DateTimeOffset? NextAttemptAtUtc { get; private set; }

    private CentralOutboxMessage() { }

    public static CentralOutboxMessage Create(
        Guid messageId,
        string eventType,
        string aggregateType,
        string aggregateId,
        string payloadJson,
        DateTimeOffset occurredOnUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateType);
        ArgumentException.ThrowIfNullOrWhiteSpace(aggregateId);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadJson);

        return new CentralOutboxMessage
        {
            Id = messageId,
            OccurredOnUtc = occurredOnUtc,
            EventType = eventType,
            AggregateType = aggregateType,
            AggregateId = aggregateId,
            PayloadJson = payloadJson,
            Status = OutboxStatus.PENDING,
            RetryCount = 0
        };
    }

    public const int DefaultMaxRetries = 5;

    public void MarkDispatched(DateTimeOffset processedAtUtc)
    {
        Status = OutboxStatus.DISPATCHED;
        ProcessedAtUtc = processedAtUtc;
        ClaimToken = null;
        ClaimExpiresAtUtc = null;
        NextAttemptAtUtc = null;
    }

    public void MarkFailed(string error, int maxRetries = DefaultMaxRetries, DateTimeOffset? nextAttemptAtUtc = null)
    {
        RetryCount++;
        LastError = error;
        ClaimToken = null;
        ClaimExpiresAtUtc = null;
        NextAttemptAtUtc = nextAttemptAtUtc;
        if (RetryCount >= maxRetries)
        {
            Status = OutboxStatus.DEAD_LETTER;
        }
        else
        {
            Status = OutboxStatus.FAILED;
        }
    }

    public void MarkDeadLetter(string error)
    {
        Status = OutboxStatus.DEAD_LETTER;
        LastError = error;
        ClaimToken = null;
        ClaimExpiresAtUtc = null;
    }

    public void Claim(Guid token, DateTimeOffset expiresAtUtc)
    {
        ClaimToken = token;
        ClaimExpiresAtUtc = expiresAtUtc;
    }
}
