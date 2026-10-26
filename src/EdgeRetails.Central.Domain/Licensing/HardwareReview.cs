using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class HardwareReview : Entity
{
    public const string StatusPending = "PENDING";
    public const string StatusApproved = "APPROVED";
    public const string StatusRejected = "REJECTED";

    public Guid ShopId { get; private set; }
    public Guid InstallationId { get; private set; }
    public string ReportedMachineFingerprint { get; private set; } = string.Empty;
    public string StoredMachineFingerprint { get; private set; } = string.Empty;
    public Guid ReportedMachineFingerprintId { get; private set; }
    public Guid StoredMachineFingerprintId { get; private set; }
    public Guid? VerificationEvidenceId { get; private set; }
    public string? ContractDecision { get; private set; }
    public string DiscrepancyReason { get; private set; } = string.Empty;
    public string Status { get; private set; } = StatusPending;
    public Guid? ReviewedByAdminId { get; private set; }
    public DateTimeOffset? DecidedAtUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private HardwareReview() { }

    public static HardwareReview Create(
        Guid shopId,
        Guid installationId,
        string reportedFingerprint,
        string storedFingerprint,
        string discrepancyReason,
        DateTimeOffset createdAtUtc)
    {
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "hw.invalid_shop_id", "Shop ID is required.");
        }
        if (installationId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_INSTALL_ID", "hw.invalid_install_id", "Installation ID is required.");
        }
        if (string.IsNullOrWhiteSpace(reportedFingerprint))
        {
            throw new CentralDomainException("INVALID_FINGERPRINT", "hw.invalid_fingerprint", "Reported fingerprint is required.");
        }

        return new HardwareReview
        {
            Id = Guid.NewGuid(),
            ShopId = shopId,
            InstallationId = installationId,
            ReportedMachineFingerprint = reportedFingerprint.Trim(),
            StoredMachineFingerprint = storedFingerprint?.Trim() ?? string.Empty,
            ReportedMachineFingerprintId = Guid.NewGuid(),
            StoredMachineFingerprintId = Guid.NewGuid(),
            DiscrepancyReason = discrepancyReason?.Trim() ?? string.Empty,
            Status = StatusPending,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }

    public void Decide(bool approve, Guid adminUserId, DateTimeOffset decidedAtUtc)
        => Decide(approve ? "APPROVE_REBIND" : "REJECT", null, adminUserId, decidedAtUtc);

    public void Decide(string decision, Guid? verificationEvidenceId, Guid adminUserId, DateTimeOffset decidedAtUtc)
    {
        if (Status != StatusPending)
        {
            throw new CentralDomainException("NOT_PENDING", "hw.not_pending", $"Cannot decide review in status: {Status}");
        }
        if (adminUserId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_ADMIN_ID", "hw.invalid_admin_id", "Admin ID is required.");
        }

        if (decision is not ("APPROVE_REBIND" or "RECOMMEND_TRANSFER" or "REQUEST_INFORMATION" or "REJECT"))
        {
            throw new CentralDomainException("INVALID_DECISION", "hw.invalid_decision", "Hardware review decision is invalid.");
        }
        if (verificationEvidenceId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_EVIDENCE_ID", "hw.invalid_evidence", "Verification evidence ID is invalid.");
        }
        ContractDecision = decision;
        VerificationEvidenceId = verificationEvidenceId;
        Status = decision switch
        {
            "APPROVE_REBIND" => StatusApproved,
            "REJECT" => StatusRejected,
            _ => decision
        };
        ReviewedByAdminId = adminUserId;
        DecidedAtUtc = decidedAtUtc;
        RowVersion++;
    }
}
