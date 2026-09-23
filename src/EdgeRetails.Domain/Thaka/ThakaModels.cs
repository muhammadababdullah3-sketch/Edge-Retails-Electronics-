using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Thaka;

public enum ThakaProjectStatus
{
    Active = 1,
    Settled = 2,
    Closed = 3
}

public enum ThakaPaymentMethod
{
    Cash = 1,
    Bank = 2,
    Other = 3
}

public sealed class ThakaProject : Entity
{
    public string ProjectNumber { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public string? SiteAddress { get; set; }
    public string? Note { get; set; }
    public DateOnly StartedOn { get; set; }
    public ThakaProjectStatus Status { get; set; } = ThakaProjectStatus.Active;
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }
}
public sealed class ThakaMaterialIssue : Entity
{
    public Guid ProjectId { get; set; }
    public string ChallanNumber { get; set; } = string.Empty;
    public Guid ClientOperationId { get; set; }
    public decimal TotalCharge { get; set; }
    public decimal TotalCost { get; set; }
    public decimal GrossProfit { get; set; }
    public string? Note { get; set; }
    public Guid IssuedBy { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
}

public sealed class ThakaMaterialIssueItem : Entity
{
    public Guid MaterialIssueId { get; set; }
    public Guid InventoryMovementId { get; set; }
    public Guid ProductId { get; set; }
    public Guid ProductUnitId { get; set; }
    public string ProductNameSnapshot { get; set; } = string.Empty;
    public string? SkuSnapshot { get; set; }
    public decimal EnteredQuantity { get; set; }
    public decimal FactorToBaseSnapshot { get; set; }
    public decimal BaseQuantity { get; set; }
    public decimal UnitCharge { get; set; }
    public decimal LineCharge { get; set; }
    public decimal UnitCostSnapshot { get; set; }
    public decimal TotalCostSnapshot { get; set; }
    public decimal GrossProfitSnapshot { get; set; }
}
public sealed class ThakaMaterialIssueUnit : Entity
{
    public Guid MaterialIssueItemId { get; set; }
    public Guid InventoryUnitId { get; set; }
    public decimal UnitCostSnapshot { get; set; }
}

public sealed class ThakaPayment : Entity
{
    public Guid ProjectId { get; set; }
    public string ReceiptNumber { get; set; } = string.Empty;
    public Guid ClientOperationId { get; set; }
    public decimal Amount { get; set; }
    public ThakaPaymentMethod PaymentMethod { get; set; }
    public Guid? CashSessionId { get; set; }
    public string? Reference { get; set; }
    public string? Note { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class ThakaSettlement : Entity
{
    public Guid ProjectId { get; set; }
    public string SettlementNumber { get; set; } = string.Empty;
    public Guid ClientOperationId { get; set; }
    public decimal GrossMaterialChargesSnapshot { get; set; }
    public decimal PaymentsCollectedSnapshot { get; set; }
    public decimal SettlementDiscount { get; set; }
    public decimal FinalPaymentAmount { get; set; }
    public decimal BalanceBeforeSettlement { get; set; }
    public Guid? FinalPaymentId { get; set; }
    public Guid SettledBy { get; set; }
    public DateTimeOffset SettledAt { get; set; }
}

public sealed class ThakaReopening : Entity
{
    public Guid ProjectId { get; set; }
    public Guid SettlementId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid ReopenedBy { get; set; }
    public DateTimeOffset ReopenedAt { get; set; }
}

public sealed class ThakaMaterialReversal : Entity
{
    public Guid ProjectId { get; set; }
    public Guid MaterialIssueId { get; set; }
    public string ReversalNumber { get; set; } = string.Empty;
    public Guid ClientOperationId { get; set; }
    public decimal ReversedCharge { get; set; }
    public decimal RestoredCost { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid ReversedBy { get; set; }
    public DateTimeOffset ReversedAt { get; set; }
}
public sealed class ThakaPaymentReversal : Entity
{
    public Guid ProjectId { get; set; }
    public Guid PaymentId { get; set; }
    public string ReversalNumber { get; set; } = string.Empty;
    public Guid ClientOperationId { get; set; }
    public decimal Amount { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid ReversedBy { get; set; }
    public DateTimeOffset ReversedAt { get; set; }
}
