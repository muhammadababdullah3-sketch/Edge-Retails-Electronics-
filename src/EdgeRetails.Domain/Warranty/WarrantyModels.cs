using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Warranty;

public enum WarrantyClaimStatus
{
    Received = 1,
    UnderReview = 2,
    SentToSupplier = 3,
    SupplierProcessing = 4,
    ReadyForCustomer = 5,
    Closed = 6,
    Cancelled = 7
}

public enum WarrantyCustody
{
    WithCustomer = 1,
    WithShop = 2,
    WithSupplier = 3
}

public enum WarrantyResolutionType
{
    Repaired = 1,
    Replaced = 2,
    Rejected = 3,
    Credited = 4,
    Refunded = 5,
    Other = 6,
    Scrapped = 7
}

public sealed class WarrantyClaim : Entity
{
    public string ClaimNumber { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public Guid? OriginalSaleId { get; set; }
    public Guid? SupplierId { get; set; }
    public Guid ClientOperationId { get; set; }
    public WarrantyClaimStatus Status { get; set; } = WarrantyClaimStatus.Received;
    public WarrantyCustody CurrentCustody { get; set; } = WarrantyCustody.WithShop;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }

    public void BeginReview()
    {
        RequireStatus(WarrantyClaimStatus.Received);
        Status = WarrantyClaimStatus.UnderReview;
        CurrentCustody = WarrantyCustody.WithShop;
        Version++;
    }

    public void SendToSupplier(DateTimeOffset now)
    {
        RequireStatus(WarrantyClaimStatus.UnderReview);
        Status = WarrantyClaimStatus.SentToSupplier;
        CurrentCustody = WarrantyCustody.WithSupplier;
        Version++;
    }

    public void MarkSupplierProcessing()
    {
        RequireStatus(WarrantyClaimStatus.SentToSupplier);
        Status = WarrantyClaimStatus.SupplierProcessing;
        CurrentCustody = WarrantyCustody.WithSupplier;
        Version++;
    }

    public void MarkReadyForCustomer(DateTimeOffset now)
    {
        if (Status is not WarrantyClaimStatus.UnderReview and
            not WarrantyClaimStatus.SentToSupplier and
            not WarrantyClaimStatus.SupplierProcessing)
        {
            throw new BusinessRuleException(
                "warranty.invalid_transition",
                $"Warranty claim cannot move from {Status} to ReadyForCustomer.");
        }

        Status = WarrantyClaimStatus.ReadyForCustomer;
        CurrentCustody = WarrantyCustody.WithShop;
        ResolvedAt = now;
        Version++;
    }

    public void Handover(DateTimeOffset now)
    {
        RequireStatus(WarrantyClaimStatus.ReadyForCustomer);
        Status = WarrantyClaimStatus.Closed;
        CurrentCustody = WarrantyCustody.WithCustomer;
        ClosedAt = now;
        Version++;
    }

    public void Cancel()
    {
        if (Status is WarrantyClaimStatus.Closed or WarrantyClaimStatus.Cancelled or WarrantyClaimStatus.SentToSupplier or WarrantyClaimStatus.SupplierProcessing)
        {
            throw new BusinessRuleException(
                "warranty.claim_not_cancellable",
                "This warranty claim can no longer be cancelled.");
        }

        Status = WarrantyClaimStatus.Cancelled;
        CurrentCustody = WarrantyCustody.WithCustomer;
        Version++;
    }

    private void RequireStatus(WarrantyClaimStatus required)
    {
        if (Status != required)
        {
            throw new BusinessRuleException(
                "warranty.invalid_transition",
                $"Warranty claim must be {required} for this transition; current status is {Status}.");
        }
    }
}

public sealed class WarrantyClaimItem : Entity
{
    public Guid ClaimId { get; set; }
    public Guid? OriginalSaleItemId { get; set; }
    public Guid ProductId { get; set; }
    public decimal Quantity { get; set; }
    public string FaultDescription { get; set; } = string.Empty;
    public DateOnly? WarrantyValidUntil { get; set; }
    public WarrantyResolutionType? ResolutionType { get; set; }
    public string? ResolutionNote { get; set; }
    public Guid? ReplacementProductId { get; set; }
    public string? ReplacementReference { get; set; }
}

public sealed class WarrantyClaimItemUnit : Entity
{
    public Guid ClaimItemId { get; set; }
    public Guid? OriginalInventoryUnitId { get; set; }
    public Guid? ActiveOriginalInventoryUnitId { get; set; }
    public string? OriginalIdentitySnapshot { get; set; }
    public Guid? ReplacementInventoryUnitId { get; set; }
    public string? ReplacementIdentitySnapshot { get; set; }
}

public sealed class WarrantyClaimEvent : Entity
{
    public Guid ClaimId { get; set; }
    public WarrantyClaimStatus Status { get; set; }
    public WarrantyCustody Custody { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string? Note { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public enum WarrantyOperationType
{
    BeginReview = 1,
    SendToSupplier = 2,
    SupplierProcessing = 3,
    Resolution = 4,
    CustomerReplacement = 5,
    Handover = 6,
    Cancellation = 7,
    ShopSend = 8,
    ShopReceiveRepaired = 9,
    ShopReceiveRejected = 10,
    ShopReceiveScrapped = 11,
    ShopReceiveReplacement = 12,
    ShopSupplierCredit = 13
}

public sealed class WarrantyOperation : Entity
{
    public Guid ClientOperationId { get; set; }
    public string TargetType { get; set; } = string.Empty;
    public Guid? TargetId { get; set; }
    public WarrantyOperationType OperationType { get; set; }
    public Guid ActorId { get; set; }
    public string PayloadHash { get; set; } = string.Empty;
    public Guid? ResultId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public enum ShopWarrantyCaseStatus
{
    Open = 1,
    WithSupplier = 2,
    ReadyForStock = 3,
    Closed = 4,
    WrittenOff = 5
}

public sealed class ShopStockWarrantyCase : Entity
{
    public string CaseNumber { get; set; } = string.Empty;
    public Guid ProductId { get; set; }
    public decimal BaseQuantity { get; set; }
    public Guid SupplierId { get; set; }
    public Guid? SourcePurchaseItemId { get; set; }
    public string FaultDescription { get; set; } = string.Empty;
    public string? SupplierReference { get; set; }
    public ShopWarrantyCaseStatus Status { get; set; } = ShopWarrantyCaseStatus.Open;
    public WarrantyResolutionType? ResolutionType { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? ReceivedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal? InventoryCarryingCostResolved { get; set; }
    public decimal? SupplierCreditAmount { get; set; }
    public decimal? RecoveryDifference { get; set; }
    public Guid? ResolutionClientOperationId { get; set; }
    public Guid CreatedBy { get; set; }
    public long Version { get; set; }
}
