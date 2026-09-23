using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Finance;

public enum SupplierAccountDirection
{
    IncreasePayable = 1,
    DecreasePayable = 2
}

public enum SupplierAccountEntryType
{
    OpeningBalance = 1,
    Purchase = 2,
    SupplierPayment = 3,
    SupplierPaymentReversal = 4,
    PurchaseReturnCredit = 5,
    PurchaseVoidReversal = 6,
    SupplierRefundReceived = 7,
    SupplierRefundReversal = 8,
    WarrantyCredit = 9,
    AdjustmentIncrease = 10,
    AdjustmentDecrease = 11
}

public enum SupplierPaymentPurpose
{
    Settlement = 1,
    Advance = 2
}

public enum SupplierSettlementMethod
{
    CashDrawer = 1,
    External = 2
}

public enum SupplierSettlementStatus
{
    Posted = 1,
    Reversed = 2
}

public sealed class SupplierAccountEntry : Entity
{
    public string EntryNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public SupplierAccountEntryType EntryType { get; set; }
    public SupplierAccountDirection Direction { get; set; }
    public decimal Amount { get; set; }
    public string ReferenceType { get; set; } = string.Empty;
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid ActorId { get; set; }
    public Guid? ClientOperationId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public decimal SignedAmount =>
        Direction == SupplierAccountDirection.IncreasePayable ? Amount : -Amount;

    public void ValidateDirection()
    {
        var valid = EntryType switch
        {
            SupplierAccountEntryType.OpeningBalance => true,
            SupplierAccountEntryType.Purchase => Direction == SupplierAccountDirection.IncreasePayable,
            SupplierAccountEntryType.SupplierPayment => Direction == SupplierAccountDirection.DecreasePayable,
            SupplierAccountEntryType.SupplierPaymentReversal => Direction == SupplierAccountDirection.IncreasePayable,
            SupplierAccountEntryType.PurchaseReturnCredit => Direction == SupplierAccountDirection.DecreasePayable,
            SupplierAccountEntryType.PurchaseVoidReversal => Direction == SupplierAccountDirection.DecreasePayable,
            SupplierAccountEntryType.SupplierRefundReceived => Direction == SupplierAccountDirection.IncreasePayable,
            SupplierAccountEntryType.SupplierRefundReversal => Direction == SupplierAccountDirection.DecreasePayable,
            SupplierAccountEntryType.WarrantyCredit => Direction == SupplierAccountDirection.DecreasePayable,
            SupplierAccountEntryType.AdjustmentIncrease => Direction == SupplierAccountDirection.IncreasePayable,
            SupplierAccountEntryType.AdjustmentDecrease => Direction == SupplierAccountDirection.DecreasePayable,
            _ => false
        };

        if (!valid)
        {
            throw new BusinessRuleException(
                "supplier.account_direction_invalid",
                $"Direction {Direction} is invalid for {EntryType}.");
        }

        if (Amount <= 0)
        {
            throw new BusinessRuleException(
                "supplier.account_amount_positive",
                "Supplier account amount must be greater than zero.");
        }
    }
}

public sealed class SupplierPayment : Entity
{
    public string PaymentNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public decimal Amount { get; set; }
    public SupplierPaymentPurpose Purpose { get; set; }
    public SupplierSettlementMethod Method { get; set; }
    public Guid? CashSessionId { get; set; }
    public string? ExternalReference { get; set; }
    public DateTimeOffset PaidAt { get; set; }
    public Guid ActorId { get; set; }
    public Guid ClientOperationId { get; set; }
    public string? Note { get; set; }
    public SupplierSettlementStatus Status { get; set; } = SupplierSettlementStatus.Posted;
}

public sealed class SupplierPaymentReversal : Entity
{
    public Guid SupplierPaymentId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid ReversedBy { get; set; }
    public DateTimeOffset ReversedAt { get; set; }
    public Guid ClientOperationId { get; set; }
}

public sealed class SupplierRefund : Entity
{
    public string RefundNumber { get; set; } = string.Empty;
    public Guid SupplierId { get; set; }
    public decimal Amount { get; set; }
    public SupplierSettlementMethod Method { get; set; }
    public Guid? CashSessionId { get; set; }
    public string? ExternalReference { get; set; }
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public Guid ActorId { get; set; }
    public Guid ClientOperationId { get; set; }
    public string? Note { get; set; }
    public SupplierSettlementStatus Status { get; set; } = SupplierSettlementStatus.Posted;
}

public sealed class SupplierRefundReversal : Entity
{
    public Guid SupplierRefundId { get; set; }
    public string Reason { get; set; } = string.Empty;
    public Guid ReversedBy { get; set; }
    public DateTimeOffset ReversedAt { get; set; }
    public Guid ClientOperationId { get; set; }
}
