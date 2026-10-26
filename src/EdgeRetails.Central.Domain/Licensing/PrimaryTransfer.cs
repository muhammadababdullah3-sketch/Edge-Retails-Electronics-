using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class PrimaryTransfer : Entity
{
    public const string StatusPendingApproval = "PENDING_APPROVAL";
    public const string StatusApproved = "APPROVED";
    public const string StatusCompleted = "COMPLETED";
    public const string StatusRejected = "REJECTED";
    public const string StatusCancelled = "CANCELLED";

    public Guid ShopId { get; private set; }
    public Guid SourceInstallationId { get; private set; }
    public Guid TargetInstallationId { get; private set; }
    public string TransferReason { get; private set; } = string.Empty;
    public string Status { get; private set; } = StatusPendingApproval;
    public Guid InitiatedByAdminId { get; private set; }
    public Guid? ApprovedByAdminId { get; private set; }
    public Guid? SensitiveApprovalId { get; private set; }
    public string? ContractDecision { get; private set; }
    public string? RecoveryType { get; private set; }
    public Guid? VerificationEvidenceId { get; private set; }
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
        => Initiate(Guid.NewGuid(), shopId, sourceInstallationId, targetInstallationId, transferReason, initiatedByAdminId, createdAtUtc);

    public static PrimaryTransfer Initiate(
        Guid transferId,
        Guid shopId,
        Guid sourceInstallationId,
        Guid targetInstallationId,
        string transferReason,
        Guid initiatedByAdminId,
        DateTimeOffset createdAtUtc)
    {
        if (transferId == Guid.Empty || shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_TRANSFER_ID", "transfer.invalid_id", "Transfer and shop IDs are required.");
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
            Id = transferId,
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
        if (approverAdminId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ADMIN_ID", "transfer.invalid_admin_id", "Approving Admin ID is required.");
        }
        if (sensitiveApprovalId == Guid.Empty)
        {
            throw new CentralDomainException("APPROVAL_REQUIRED", "transfer.approval_required", "A consumed sensitive approval is required.");
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
        if (Status != StatusPendingApproval)
        {
            throw new CentralDomainException("NOT_PENDING", "transfer.not_pending", $"Cannot reject transfer in status: {Status}");
        }
        if (adminId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ADMIN_ID", "transfer.invalid_admin_id", "Rejecting Admin ID is required.");
        }

        Status = StatusRejected;
        RowVersion++;
    }

    public void ApplyContractDecision(string decision, string recoveryType, Guid evidenceId, Guid adminId)
    {
        if (decision is not ("REQUEST" or "APPROVE" or "REJECT" or "CANCEL") ||
            recoveryType is not ("STANDARD_TRANSFER" or "LOST_HARDWARE_RECOVERY") || evidenceId == Guid.Empty || adminId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_CONTRACT_DECISION", "transfer.invalid_decision", "Transfer decision metadata is invalid.");
        }
        if (decision == "CANCEL")
        {
            if (Status != StatusPendingApproval)
            {
                throw new CentralDomainException("CANCEL_NOT_ALLOWED", "transfer.cancel_not_allowed", "Only a pending transfer can be cancelled.");
            }
            Status = StatusCancelled;
            RowVersion++;
        }
        else if (decision == "REQUEST")
        {
            if (Status != StatusPendingApproval || ContractDecision is not null)
            {
                throw new CentralDomainException("NOT_PENDING", "transfer.not_pending", "A request can only be recorded once for a pending transfer.");
            }
            RowVersion++;
        }
        else if (decision == "REJECT")
        {
            Reject(adminId);
        }
        else if (Status != StatusApproved)
        {
            throw new CentralDomainException("NOT_APPROVED", "transfer.not_approved", "Approval must be completed through dual-control approval.");
        }
        ContractDecision = decision;
        RecoveryType = recoveryType;
        VerificationEvidenceId = evidenceId;
    }
}
