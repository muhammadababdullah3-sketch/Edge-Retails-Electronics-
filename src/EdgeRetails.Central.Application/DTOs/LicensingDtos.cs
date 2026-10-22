using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class CommercialTermDto
{
    [JsonPropertyName("termKind")]
    [Required]
    public string TermKind { get; set; } = string.Empty;

    [JsonPropertyName("validFromUtc")]
    public DateTimeOffset ValidFromUtc { get; set; }

    [JsonPropertyName("validUntilUtc")]
    public DateTimeOffset? ValidUntilUtc { get; set; }
}

public class PolicyDto
{
    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("licenseId")]
    public Guid LicenseId { get; set; }

    [JsonPropertyName("policyVersion")]
    public int PolicyVersion { get; set; }

    [JsonPropertyName("enforcementMode")]
    [Required]
    public string EnforcementMode { get; set; } = string.Empty;

    [JsonPropertyName("extensionEndUtc")]
    public DateTimeOffset? ExtensionEndUtc { get; set; }

    [JsonPropertyName("afterExtensionPolicyId")]
    public Guid AfterExtensionPolicyId { get; set; }

    [JsonPropertyName("leaseDurationSeconds")]
    public int LeaseDurationSeconds { get; set; }

    [JsonPropertyName("offlineGraceSeconds")]
    public int OfflineGraceSeconds { get; set; }

    [JsonPropertyName("rollbackToleranceSeconds")]
    public int RollbackToleranceSeconds { get; set; }

    [JsonPropertyName("capabilityPolicyId")]
    public Guid CapabilityPolicyId { get; set; }

    [JsonPropertyName("approvedByAdminId")]
    public Guid ApprovedByAdminId { get; set; }

    [JsonPropertyName("approvedAtUtc")]
    public DateTimeOffset ApprovedAtUtc { get; set; }
}

public class AddonEntitlementDto
{
    [JsonPropertyName("entitlementId")]
    public Guid EntitlementId { get; set; }

    [JsonPropertyName("kind")]
    [Required]
    public string Kind { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    [Required]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("validUntilUtc")]
    public DateTimeOffset? ValidUntilUtc { get; set; }
}

public class EntitlementPayloadDto
{
    [JsonPropertyName("certificateId")]
    public Guid CertificateId { get; set; }

    [JsonPropertyName("certificateVersion")]
    public int CertificateVersion { get; set; }

    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("licenseId")]
    public Guid LicenseId { get; set; }

    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("installationPublicKeyFingerprint")]
    public string InstallationPublicKeyFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("hardwareFingerprintVersion")]
    public int HardwareFingerprintVersion { get; set; }

    [JsonPropertyName("hardwareFingerprintHash")]
    public string HardwareFingerprintHash { get; set; } = string.Empty;

    [JsonPropertyName("authorityRevision")]
    public int AuthorityRevision { get; set; }

    [JsonPropertyName("policy")]
    public PolicyDto Policy { get; set; } = null!;

    [JsonPropertyName("issuedAtUtc")]
    public DateTimeOffset IssuedAtUtc { get; set; }

    [JsonPropertyName("validFromUtc")]
    public DateTimeOffset ValidFromUtc { get; set; }

    [JsonPropertyName("leaseExpiresAtUtc")]
    public DateTimeOffset LeaseExpiresAtUtc { get; set; }

    [JsonPropertyName("offlineGraceUntilUtc")]
    public DateTimeOffset OfflineGraceUntilUtc { get; set; }

    [JsonPropertyName("trustedVendorTimeUtc")]
    public DateTimeOffset TrustedVendorTimeUtc { get; set; }

    [JsonPropertyName("capabilityClaims")]
    public List<string> CapabilityClaims { get; set; } = new();

    [JsonPropertyName("signingKeyId")]
    public string SigningKeyId { get; set; } = string.Empty;

    [JsonPropertyName("signingKeyVersion")]
    public int SigningKeyVersion { get; set; }

    [JsonPropertyName("commercialTerm")]
    public CommercialTermDto CommercialTerm { get; set; } = null!;

    [JsonPropertyName("addonEntitlements")]
    public List<AddonEntitlementDto> AddonEntitlements { get; set; } = new();
}

public class SignedEntitlementDto
{
    [JsonPropertyName("envelopeVersion")]
    public int EnvelopeVersion { get; set; } = 1;

    [JsonPropertyName("canonicalizationProfileId")]
    public string CanonicalizationProfileId { get; set; } = "CANONICAL_JSON_V1";

    [JsonPropertyName("algorithmProfileId")]
    public string AlgorithmProfileId { get; set; } = "RS256";

    [JsonPropertyName("purpose")]
    public string Purpose => "LICENSE";

    [JsonPropertyName("payload")]
    public EntitlementPayloadDto Payload { get; set; } = null!;

    [JsonPropertyName("payloadSha256")]
    public string PayloadSha256 { get; set; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("trustSetVersion")]
    public int TrustSetVersion { get; set; }
}

public class ReconcileCommandDto
{
    [JsonPropertyName("installationId")]
    [Required]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("certificateId")]
    [Required]
    public Guid CertificateId { get; set; }

    [JsonPropertyName("authorityRevision")]
    public int AuthorityRevision { get; set; }

    [JsonPropertyName("hardwareFingerprintHash")]
    [Required]
    public string HardwareFingerprintHash { get; set; } = string.Empty;

    [JsonPropertyName("applicationVersion")]
    [Required]
    public string ApplicationVersion { get; set; } = string.Empty;

    [JsonPropertyName("lastVerifiedVendorTimeUtc")]
    public DateTimeOffset LastVerifiedVendorTimeUtc { get; set; }
}

