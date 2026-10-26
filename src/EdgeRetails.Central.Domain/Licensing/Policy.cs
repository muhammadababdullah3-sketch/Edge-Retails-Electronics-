using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Licensing;

public record Policy
{
    public const string FullStop = "FULL_STOP";
    public const string ConfigurableExtension = "CONFIGURABLE_EXTENSION";

    public Guid ShopId { get; init; }
    public Guid LicenseId { get; init; }
    public int PolicyVersion { get; init; }
    public string EnforcementMode { get; init; }
    public DateTimeOffset? ExtensionEndUtc { get; init; }
    public Guid AfterExtensionPolicyId { get; init; }
    public int LeaseDurationSeconds { get; init; }
    public int OfflineGraceSeconds { get; init; }
    public int RollbackToleranceSeconds { get; init; }
    public Guid CapabilityPolicyId { get; init; }
    public Guid ApprovedByAdminId { get; init; }
    public DateTimeOffset ApprovedAtUtc { get; init; }

    public Policy(
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
        Guid approvedByAdminId,
        DateTimeOffset approvedAtUtc)
    {
        if (enforcementMode != FullStop && enforcementMode != ConfigurableExtension)
        {
            throw new CentralDomainException("INVALID_ENFORCEMENT_MODE", "policy.invalid_mode", $"Invalid enforcement mode: {enforcementMode}");
        }

        if (enforcementMode == ConfigurableExtension && !extensionEndUtc.HasValue)
        {
            throw new CentralDomainException("EXTENSION_REQUIRES_END_DATE", "policy.extension_end_required", "Configurable extension requires extensionEndUtc.");
        }

        ShopId = shopId;
        LicenseId = licenseId;
        PolicyVersion = policyVersion;
        EnforcementMode = enforcementMode;
        ExtensionEndUtc = extensionEndUtc;
        AfterExtensionPolicyId = afterExtensionPolicyId;
        LeaseDurationSeconds = leaseDurationSeconds;
        OfflineGraceSeconds = offlineGraceSeconds;
        RollbackToleranceSeconds = rollbackToleranceSeconds;
        CapabilityPolicyId = capabilityPolicyId;
        ApprovedByAdminId = approvedByAdminId;
        ApprovedAtUtc = approvedAtUtc;
    }
}
