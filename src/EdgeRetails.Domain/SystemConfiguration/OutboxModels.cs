using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.SystemConfiguration;

public enum OutboxMessageStatus
{
    Pending = 1,
    Processing = 2,
    Completed = 3,
    Failed = 4,
    ActionRequired = 5
}

public sealed class OutboxMessage : Entity
{
    public string EffectType { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;
    public string? LastError { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
