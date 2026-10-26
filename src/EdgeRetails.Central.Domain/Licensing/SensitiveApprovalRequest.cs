using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class SensitiveApprovalRequest : Entity
{
    public const string StatusPending = "PENDING";
    public const string StatusApproved = "APPROVED";
    public const string StatusRejected = "REJECTED";
    public const string StatusExpired = "EXPIRED";
    public const string StatusConsumed = "CONSUMED";

    public bool IsConsumed => Status == StatusConsumed;

    public string OperationType { get; private set; } = string.Empty;
    public Guid TargetEntityId { get; private set; }
    public string TargetPayloadSha256 { get; private set; } = string.Empty;
    public Guid RequesterAdminId { get; private set; }
    public Guid? ApproverAdminId { get; private set; }
    public string Status { get; private set; } = StatusPending;
    public string ActionScope { get; private set; } = string.Empty;
    public string RequestReason { get; private set; } = string.Empty;
    public Guid StepUpSessionId { get; private set; }
    public DateTimeOffset RequestedAtUtc { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public DateTimeOffset ExpiresAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private SensitiveApprovalRequest() { }

    public static SensitiveApprovalRequest Request(
        string operationType,
        Guid targetEntityId,
        string targetPayloadSha256,
        Guid requesterAdminId,
        string actionScope,
        Guid stepUpSessionId,
        DateTimeOffset requestedAtUtc,
        DateTimeOffset expiresAtUtc,
        string requestReason = "")
    {
        if (string.IsNullOrWhiteSpace(operationType))
        {
            throw new CentralDomainException("INVALID_OP_TYPE", "approval.invalid_op_type", "Operation type is required.");
        }
        if (targetEntityId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_TARGET_ID", "approval.invalid_target_id", "Target entity ID is required.");
        }
        if (string.IsNullOrWhiteSpace(targetPayloadSha256) || targetPayloadSha256.Length != 64)
        {
            throw new CentralDomainException("INVALID_PAYLOAD_SHA", "approval.invalid_payload_sha", "Valid 64-character payload SHA-256 is required.");
        }
        if (requesterAdminId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_REQUESTER", "approval.invalid_requester", "Requester Admin ID is required.");
        }
        if (requestReason is null || requestReason.Length > 500)
        {
            throw new CentralDomainException("INVALID_APPROVAL_REASON", "approval.invalid_reason", "Approval request reason must be 500 characters or fewer.");
        }
        if (expiresAtUtc <= requestedAtUtc)
        {
            throw new CentralDomainException("INVALID_EXPIRATION", "approval.invalid_expiration", "Expiration must be after request time.");
        }

        return new SensitiveApprovalRequest
        {
            Id = Guid.NewGuid(),
            OperationType = operationType.Trim(),
            TargetEntityId = targetEntityId,
            TargetPayloadSha256 = targetPayloadSha256.Trim().ToLowerInvariant(),
            RequesterAdminId = requesterAdminId,
            Status = StatusPending,
            ActionScope = actionScope.Trim(),
            RequestReason = requestReason.Trim(),
            StepUpSessionId = stepUpSessionId,
            RequestedAtUtc = requestedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            RowVersion = 1
        };
    }

    public void Approve(Guid approverAdminId, string providedPayloadSha256, DateTimeOffset decidedAtUtc)
    {
        if (Status != StatusPending)
        {
            throw new CentralDomainException("NOT_PENDING", "approval.not_pending", $"Cannot approve request in status: {Status}");
        }
        if (decidedAtUtc > ExpiresAtUtc)
        {
            Status = StatusExpired;
            RowVersion++;
            throw new CentralDomainException("APPROVAL_EXPIRED", "approval.expired", "Approval request has expired.");
        }
        if (approverAdminId == RequesterAdminId)
        {
            throw new CentralDomainException("SELF_APPROVAL_PROHIBITED", "approval.self_approval_prohibited", "Approver must be distinct from requester.");
        }
        if (!string.Equals(TargetPayloadSha256, providedPayloadSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new CentralDomainException("PAYLOAD_MISMATCH", "approval.payload_mismatch", "Provided payload SHA-256 does not match original request.");
        }

        ApproverAdminId = approverAdminId;
        Status = StatusApproved;
        DecidedAtUtc = decidedAtUtc;
        RowVersion++;
    }

    public void Reject(Guid adminId, DateTimeOffset decidedAtUtc)
    {
        if (Status != StatusPending)
        {
            throw new CentralDomainException("NOT_PENDING", "approval.not_pending", $"Cannot reject request in status: {Status}");
        }

        ApproverAdminId = adminId;
        Status = StatusRejected;
        DecidedAtUtc = decidedAtUtc;
        RowVersion++;
    }

    public void Consume(DateTimeOffset consumedAtUtc)
    {
        if (Status != StatusApproved)
        {
            throw new CentralDomainException("APPROVAL_NOT_APPROVED", "approval.not_approved", $"Cannot consume approval in status: {Status}");
        }

        Status = StatusConsumed;
        DecidedAtUtc = consumedAtUtc;
        RowVersion++;
    }
}
