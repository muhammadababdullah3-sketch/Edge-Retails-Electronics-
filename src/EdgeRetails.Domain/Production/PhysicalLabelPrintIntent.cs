using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Production;

public enum PhysicalLabelPrintIntentKind
{
    Original = 0,
    Reprint = 1
}

public enum PhysicalLabelPrintIntentStatus
{
    PendingDispatch = 0,
    Dispatched = 1,
    Succeeded = 2,
    OutcomeUnknown = 3,
    Failed = 4,
    Cancelled = 5
}

/// <summary>
/// Durable ORIGINAL (or reprint) label delivery intent, committed with the business receipt.
/// Physical dispatch happens after commit on the authenticated workstation.
/// </summary>
public sealed class PhysicalLabelPrintIntent : Entity
{
    public Guid InventoryUnitId { get; set; }
    public Guid? InventoryMovementId { get; set; }
    public Guid SourceClientOperationId { get; set; }
    public PhysicalLabelPrintIntentKind Kind { get; set; }
    public PhysicalLabelPrintIntentStatus Status { get; set; }
    public string? TargetWorkstationId { get; set; }
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
}
