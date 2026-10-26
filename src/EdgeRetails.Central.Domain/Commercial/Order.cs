using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Commercial;

public class Order : Entity
{
    public const string StatusPendingPayment = "PENDING_PAYMENT";
    public const string StatusPaidApproved = "PAID_APPROVED";
    public const string StatusCancelled = "CANCELLED";
    public const string StatusRejected = "REJECTED";

    public Guid ShopId { get; private set; }
    public Guid CustomerId { get; private set; }
    public Guid SnapshotId { get; private set; }
    public Guid PlanId { get; private set; }
    public string OrderType { get; private set; } = "NEW_LICENSE";
    public int RequestedPrimaryCapacity { get; private set; } = 1;
    public Guid? BaseLicenseId { get; private set; }
    public Guid? ExistingEntitlementId { get; private set; }
    public string PurchaseTarget { get; private set; } = string.Empty;
    public string TermKind { get; private set; } = string.Empty;
    public string? BillingPeriodId { get; private set; }
    public decimal TotalAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public string Status { get; private set; } = StatusPendingPayment;
    public Guid? ApprovedEvidenceId { get; private set; }
    public Guid? ApprovedByAdminId { get; private set; }
    public DateTimeOffset? ApprovedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private Order() { }

    public static Order CreateFromSnapshot(
        PricingSnapshot snapshot,
        Guid customerId,
        DateTimeOffset createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var orderType = snapshot.PurchaseTarget switch
        {
            PlanCatalogItem.TargetSupport => "SUPPORT_RENEWAL",
            PlanCatalogItem.TargetPremiumUpgrade => "UPGRADE",
            _ => "NEW_LICENSE"
        };
        return CreateFromSnapshot(snapshot, customerId, createdAtUtc, orderType, 1, null, null);
    }

    public static Order CreateFromSnapshot(
        PricingSnapshot snapshot,
        Guid customerId,
        DateTimeOffset createdAtUtc,
        string orderType,
        int requestedPrimaryCapacity,
        Guid? baseLicenseId,
        Guid? existingEntitlementId)
    {
        if (snapshot == null)
        {
            throw new CentralDomainException("INVALID_SNAPSHOT", "order.null_snapshot", "Pricing snapshot is required.");
        }
        if (customerId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_CUSTOMER_ID", "order.invalid_customer_id", "Customer ID is required.");
        }
        if (string.IsNullOrWhiteSpace(orderType) || requestedPrimaryCapacity != 1 ||
            orderType is not ("NEW_LICENSE" or "RENEWAL" or "PLAN_CHANGE" or "SUPPORT_RENEWAL" or "UPGRADE"))
        {
            throw new CentralDomainException("INVALID_ORDER_CONTRACT", "order.invalid_contract", "The order type and single-primary capacity are invalid.");
        }
        var targetMatches = orderType switch
        {
            "NEW_LICENSE" or "RENEWAL" or "PLAN_CHANGE" => snapshot.PurchaseTarget == PlanCatalogItem.TargetBaseLicense,
            "SUPPORT_RENEWAL" => snapshot.PurchaseTarget == PlanCatalogItem.TargetSupport,
            "UPGRADE" => snapshot.PurchaseTarget == PlanCatalogItem.TargetPremiumUpgrade,
            _ => false
        };
        if (!targetMatches || snapshot.TermKind != PlanCatalogItem.TermFixed && snapshot.TermKind != PlanCatalogItem.TermLifetime ||
            snapshot.TermKind == PlanCatalogItem.TermLifetime && snapshot.PurchaseTarget != PlanCatalogItem.TargetBaseLicense)
        {
            throw new CentralDomainException("ORDER_SNAPSHOT_MISMATCH", "order.snapshot_mismatch", "The order does not match its persisted snapshot target and term.");
        }

        return new Order
        {
            Id = Guid.NewGuid(),
            ShopId = snapshot.ShopId,
            CustomerId = customerId,
            SnapshotId = snapshot.Id,
            PlanId = snapshot.CatalogItemId,
            OrderType = orderType,
            RequestedPrimaryCapacity = requestedPrimaryCapacity,
            BaseLicenseId = baseLicenseId,
            ExistingEntitlementId = existingEntitlementId,
            PurchaseTarget = snapshot.PurchaseTarget,
            TermKind = snapshot.TermKind,
            BillingPeriodId = snapshot.BillingPeriodId,
            TotalAmount = snapshot.UnitPrice,
            Currency = snapshot.Currency,
            Status = StatusPendingPayment,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void ApprovePayment(Guid evidenceId, Guid adminUserId, DateTimeOffset approvedAtUtc)
    {
        if (Status != StatusPendingPayment)
        {
            throw new CentralDomainException("ORDER_NOT_PENDING", "order.not_pending", $"Cannot approve order in status: {Status}");
        }
        if (evidenceId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_EVIDENCE_ID", "order.invalid_evidence_id", "Evidence ID is required.");
        }
        if (adminUserId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ADMIN_ID", "order.invalid_admin_id", "Approver Admin ID is required.");
        }

        Status = StatusPaidApproved;
        ApprovedEvidenceId = evidenceId;
        ApprovedByAdminId = adminUserId;
        ApprovedAtUtc = approvedAtUtc;
        RowVersion++;
    }

    public void Reject(string reason)
    {
        if (Status != StatusPendingPayment)
        {
            throw new CentralDomainException("ORDER_NOT_PENDING", "order.not_pending", $"Cannot reject order in status: {Status}");
        }
        Status = StatusRejected;
        RowVersion++;
    }
}
