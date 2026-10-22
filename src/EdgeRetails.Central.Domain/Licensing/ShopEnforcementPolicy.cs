using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public class ShopEnforcementPolicy : Entity
{
    public const string FullStop = "FULL_STOP";
    public const string ConfigurableExtension = "CONFIGURABLE_EXTENSION";

    public Guid ShopId { get; private set; }
    public Guid LicenseId { get; private set; }
    public int PolicyVersion { get; private set; } = 1;
    public string EnforcementMode { get; private set; } = FullStop;
    public DateTimeOffset? ExtensionEndUtc { get; private set; }
    public Guid AfterExtensionPolicyId { get; private set; }
    public int LeaseDurationSeconds { get; private set; } = 86400; // 24 hours
    public int OfflineGraceSeconds { get; private set; } = 86400; // 24 hours minimum continuity
    public int RollbackToleranceSeconds { get; private set; } = 300; // 5 min
    public Guid CapabilityPolicyId { get; private set; }
    public string SignedPolicyEnvelopeJson { get; private set; } = string.Empty;
    public string RS256Signature { get; private set; } = string.Empty;
    public string KeyId { get; private set; } = string.Empty;
    public Guid ApprovedByAdminId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public int RowVersion { get; private set; } = 1;

    private ShopEnforcementPolicy() { }

    public static ShopEnforcementPolicy Create(
        Guid shopId,
        Guid licenseId,
        int policyVersion,
        string enforcementMode,
        DateTimeOffset? extensionEndUtc,
        Guid afterExtensionPolicyId,
        int leaseDurationSeconds,
        int offlineGraceSeconds,
        int rollbackToleranceSeconds,
        Guid capabilityPolicyId,
        string signedPolicyEnvelopeJson,
        string rs256Signature,
        string keyId,
        Guid approvedByAdminId,
        DateTimeOffset createdAtUtc)
    {
        if (shopId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_SHOP_ID", "policy.invalid_shop_id", "Shop ID is required.");
        }
        if (licenseId == Guid.Empty)
        {
            throw new CentralDomainException("INVALID_LICENSE_ID", "policy.invalid_license_id", "License ID is required.");
        }
        if (enforcementMode != FullStop && enforcementMode != ConfigurableExtension)
        {
            throw new CentralDomainException("INVALID_MODE", "policy.invalid_mode", $"Invalid enforcement mode: {enforcementMode}");
        }
        if (enforcementMode == ConfigurableExtension && !extensionEndUtc.HasValue)
        {
            throw new CentralDomainException("EXTENSION_REQUIRES_END_DATE", "policy.extension_end_required", "Configurable extension requires extensionEndUtc.");
        }
        if (offlineGraceSeconds < 86400)
        {
            throw new CentralDomainException("MINIMUM_GRACE_REQUIRED", "policy.minimum_grace", "Minimum offline grace period is 24 hours (86400 seconds).");
        }
        if (string.IsNullOrWhiteSpace(signedPolicyEnvelopeJson))
        {
            throw new CentralDomainException("INVALID_ENVELOPE", "policy.invalid_envelope", "Signed envelope JSON is required.");
        }
        if (string.IsNullOrWhiteSpace(rs256Signature))
        {
            throw new CentralDomainException("INVALID_SIGNATURE", "policy.invalid_signature", "RS256 signature is required.");
        }

        return new ShopEnforcementPolicy
        {
            Id = Guid.NewGuid(),
            ShopId = shopId,
            LicenseId = licenseId,
            PolicyVersion = policyVersion,
            EnforcementMode = enforcementMode,
            ExtensionEndUtc = extensionEndUtc,
            AfterExtensionPolicyId = afterExtensionPolicyId,
            LeaseDurationSeconds = leaseDurationSeconds,
            OfflineGraceSeconds = offlineGraceSeconds,
            RollbackToleranceSeconds = rollbackToleranceSeconds,
            CapabilityPolicyId = capabilityPolicyId,
            SignedPolicyEnvelopeJson = signedPolicyEnvelopeJson,
            RS256Signature = rs256Signature.Trim(),
            KeyId = keyId?.Trim() ?? string.Empty,
            ApprovedByAdminId = approvedByAdminId,
            CreatedAtUtc = createdAtUtc,
            RowVersion = 1
        };
    }
}
