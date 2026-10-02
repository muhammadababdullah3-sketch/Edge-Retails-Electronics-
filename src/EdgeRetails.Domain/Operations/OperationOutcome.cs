namespace EdgeRetails.Domain.Operations;

public enum OperationOutcomeStatus
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    OutcomeUnknown = 4
}

public sealed class OperationOutcome
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid ClientOperationId { get; set; }
    public string OperationType { get; set; } = string.Empty;
    public string? PayloadFingerprint { get; set; }
    public OperationOutcomeStatus Status { get; set; } = OperationOutcomeStatus.Pending;
    public string? ResultEntityType { get; set; }
    public Guid? ResultEntityId { get; set; }
    public string? DocumentNumber { get; set; }
    public Guid? ActorUserId { get; set; }
    public Guid? TerminalId { get; set; }
    public Guid? SessionId { get; set; }
    public bool WasCommitted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
