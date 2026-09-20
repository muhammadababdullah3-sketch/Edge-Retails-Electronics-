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
    WithSupplier = 3,
    WithServiceCenter = 4
}

public enum WarrantyResolutionType
{
    Repaired = 1,
    Replaced = 2,
    Rejected = 3,
    Credited = 4,
    Refunded = 5,
    Other = 6
}

public sealed class WarrantyClaim : Entity
{
    public string ClaimNumber { get; set; } = string.Empty;
    public Guid CustomerId { get; set; }
    public Guid? OriginalSaleId { get; set; }
    public Guid? SupplierId { get; set; }
    public WarrantyClaimStatus Status { get; set; } = WarrantyClaimStatus.Received;
    public WarrantyCustody CurrentCustody { get; set; } = WarrantyCustody.WithShop;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public long Version { get; set; }

    public void SendToSupplier(DateTimeOffset now)
    {
        if (Status is WarrantyClaimStatus.Closed or WarrantyClaimStatus.Cancelled)
        {
            throw new BusinessRuleException(
                "warranty.closed_claim",
                "A closed or cancelled warranty claim cannot be sent.");
        }

        Status = WarrantyClaimStatus.SentToSupplier;
        CurrentCustody = WarrantyCustody.WithSupplier;
        Version++;
    }

    public void MarkReadyForCustomer(DateTimeOffset now)
    {
        if (Status is WarrantyClaimStatus.Closed or WarrantyClaimStatus.Cancelled)
        {
            throw new BusinessRuleException(
                "warranty.closed_claim",
                "A closed or cancelled warranty claim cannot be resolved.");
        }

        Status = WarrantyClaimStatus.ReadyForCustomer;
        CurrentCustody = WarrantyCustody.WithShop;
        ResolvedAt = now;
        Version++;
    }

    public void Handover(DateTimeOffset now)
    {
        if (Status != WarrantyClaimStatus.ReadyForCustomer)
        {
            throw new BusinessRuleException(
                "warranty.not_ready",
                "Warranty item must be ready for customer before handover.");
        }

        Status = WarrantyClaimStatus.Closed;
        CurrentCustody = WarrantyCustody.WithCustomer;
        ClosedAt = now;
        Version++;
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
    public Guid CreatedBy { get; set; }
    public long Version { get; set; }
}
