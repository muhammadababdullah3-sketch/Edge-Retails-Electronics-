using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class PrimaryTransfer : Entity
{
    public const string StatusPendingApproval = "PENDING_APPROVAL";
    public const string StatusApproved = "APPROVED";
    public const string StatusCompleted = "COMPLETED";
    public const string StatusRejected = "REJECTED";

    public Guid ShopId { get; private set; }
    public Guid SourceInstallationId { get; private set; }
    public Guid TargetInstallationId { get; private set; }
    public string TransferReason { get; private set; } = string.Empty;
    public string Status { get; private set; } = StatusPendingApproval;
    public Guid InitiatedByAdminId { get; private set; }
    public Guid? ApprovedByAdminId { get; private set; }
    public Guid? SensitiveApprovalId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private PrimaryTransfer() { }

    public static PrimaryTransfer Initiate(
        Guid shopId,
        Guid sourceInstallationId,
        Guid targetInstallationId,
        string transferReason,
        Guid initiatedByAdminId,
        DateTimeOffset createdAtUtc)
    {
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "transfer.invalid_shop_id", "Shop ID is required.");
        }
        if (sourceInstallationId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SOURCE_INSTALL", "transfer.invalid_source", "Source installation ID is required.");
        }
        if (targetInstallationId == Guid.Empty || targetInstallationId == sourceInstallationId)
        {
            throw new CentralDomainException("INVALID_TARGET_INSTALL", "transfer.invalid_target", "Target installation ID must be distinct from source.");
        }
        if (initiatedByAdminId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ADMIN_ID", "transfer.invalid_admin_id", "Initiating Admin ID is required.");
        }

        return new PrimaryTransfer
        {
            Id = Guid.NewGuid(),
            ShopId = shopId,
            SourceInstallationId = sourceInstallationId,
            TargetInstallationId = targetInstallationId,
            TransferReason = transferReason?.Trim() ?? string.Empty,
            Status = StatusPendingApproval,
            InitiatedByAdminId = initiatedByAdminId,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void Approve(Guid approverAdminId, Guid sensitiveApprovalId)
    {
        if (Status != StatusPendingApproval)
        {
            throw new CentralDomainException("NOT_PENDING", "transfer.not_pending", $"Cannot approve transfer in status: {Status}");
        }
        if (approverAdminId == InitiatedByAdminId)
        {
            throw new CentralDomainException("SELF_APPROVAL_PROHIBITED", "transfer.self_approval_prohibited", "Approver must be distinct from initiator.");
        }

        ApprovedByAdminId = approverAdminId;
        SensitiveApprovalId = sensitiveApprovalId;
        Status = StatusApproved;
        RowVersion++;
    }

    public void Complete(DateTimeOffset completedAtUtc)
    {
        if (Status != StatusApproved)
        {
            throw new CentralDomainException("NOT_APPROVED", "transfer.not_approved", $"Cannot complete transfer in status: {Status}");
        }

        Status = StatusCompleted;
        CompletedAtUtc = completedAtUtc;
        RowVersion++;
    }

    public void Reject(Guid adminId)
    {
        Status = StatusRejected;
        RowVersion++;
    }
}
