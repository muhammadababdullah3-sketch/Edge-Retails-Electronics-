using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class BootstrapChallengeRequestDto
{
    // Kept optional for request compatibility; the persisted installation is the authority.
    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("installationPublicKey")]
    public string InstallationPublicKey { get; set; } = string.Empty;

    [JsonPropertyName("activationTokenHash")]
    public string ActivationTokenHash { get; set; } = string.Empty;

    [JsonPropertyName("requestNonce")]
    public Guid RequestNonce { get; set; }
}

public class BootstrapChallengeDto
{
    [JsonPropertyName("challengeId")]
    public Guid ChallengeId { get; set; }

    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("challengeNonce")]
    public string ChallengeNonce { get; set; } = string.Empty;

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; set; }

    [JsonPropertyName("allowedOperations")]
    public string[] AllowedOperations { get; set; } = Array.Empty<string>();

    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("licenseId")]
    public Guid LicenseId { get; set; }
}

public class InstallRegistrationPayloadDto
{
    [JsonPropertyName("shopId")] public Guid ShopId { get; set; }
    [JsonPropertyName("licenseId")] public Guid LicenseId { get; set; }
    [JsonPropertyName("installationId")] public Guid InstallationId { get; set; }
    [JsonPropertyName("installationPublicKey")] public string InstallationPublicKey { get; set; } = string.Empty;
    [JsonPropertyName("installationPublicKeyFingerprint")] public string InstallationPublicKeyFingerprint { get; set; } = string.Empty;
    [JsonPropertyName("bootstrapChallengeId")] public Guid BootstrapChallengeId { get; set; }
    [JsonPropertyName("proofOfPossession")] public string ProofOfPossession { get; set; } = string.Empty;
    [JsonPropertyName("deviceIdentityVersion")] public int DeviceIdentityVersion { get; set; }
    [JsonPropertyName("deviceIdentityHash")] public string DeviceIdentityHash { get; set; } = string.Empty;
    [JsonPropertyName("hardwareFingerprintVersion")] public int HardwareFingerprintVersion { get; set; }
    [JsonPropertyName("hardwareFingerprintHash")] public string HardwareFingerprintHash { get; set; } = string.Empty;
    [JsonPropertyName("signalQuality")] public string SignalQuality { get; set; } = string.Empty;
    [JsonPropertyName("applicationVersion")] public string ApplicationVersion { get; set; } = string.Empty;
}

public class InstallRegistrationRequestDto
{
    [JsonPropertyName("meta")] public CommandMetaDto Meta { get; set; } = null!;
    [JsonPropertyName("payload")] public InstallRegistrationPayloadDto Payload { get; set; } = null!;
}

public class BootstrapActivationRedeemPayloadDto
{
    [JsonPropertyName("activationToken")] public string ActivationToken { get; set; } = string.Empty;
    [JsonPropertyName("installationId")] public Guid InstallationId { get; set; }
    [JsonPropertyName("hardwareFingerprintHash")] public string HardwareFingerprintHash { get; set; } = string.Empty;
    [JsonPropertyName("installationPublicKeyFingerprint")] public string InstallationPublicKeyFingerprint { get; set; } = string.Empty;
    [JsonPropertyName("validationNonce")] public Guid ValidationNonce { get; set; }
    [JsonPropertyName("bootstrapChallengeId")] public Guid BootstrapChallengeId { get; set; }
    [JsonPropertyName("proofOfPossession")] public string ProofOfPossession { get; set; } = string.Empty;
}

public class BootstrapActivationRedeemRequestDto
{
    [JsonPropertyName("meta")] public CommandMetaDto Meta { get; set; } = null!;
    [JsonPropertyName("payload")] public BootstrapActivationRedeemPayloadDto Payload { get; set; } = null!;
}

public class IssueActivationTokenCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("licenseId")]
    public Guid LicenseId { get; set; }

    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("firstRedemptionTtlSeconds")]
    public int FirstRedemptionTtlSeconds { get; set; } = 86400; // 24 hours
}

public class ActivationIssuedDto
{
    [JsonPropertyName("tokenId")]
    public Guid TokenId { get; set; }

    [JsonPropertyName("tokenSecret")]
    public string TokenSecret { get; set; } = string.Empty;

    [JsonPropertyName("firstRedemptionExpiresAtUtc")]
    public DateTimeOffset FirstRedemptionExpiresAtUtc { get; set; }
}

public sealed class ActivationIssuedContractDto
{
    [JsonPropertyName("clientOperationId")] public Guid ClientOperationId { get; set; }
    [JsonPropertyName("activationTokenId")] public Guid ActivationTokenId { get; set; }
    [JsonPropertyName("deliveryReference")] public string DeliveryReference { get; set; } = string.Empty;
    [JsonPropertyName("expiresAtUtc")] public DateTimeOffset ExpiresAtUtc { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "DELIVERY_PENDING";
}

public class RegisterInstallationCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("licenseId")]
    public Guid LicenseId { get; set; }

    [JsonPropertyName("machineFingerprint")]
    [Required]
    public string MachineFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("publicKeyPem")]
    [Required]
    public string PublicKeyPem { get; set; } = string.Empty;

    [JsonPropertyName("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [JsonPropertyName("osPlatform")]
    public string OsPlatform { get; set; } = string.Empty;
}

public class RedeemActivationTokenCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("tokenId")]
    public Guid TokenId { get; set; }

    [JsonPropertyName("installationId")]
    public Guid InstallationId { get; set; }

    [JsonPropertyName("tokenSecret")]
    [Required]
    public string TokenSecret { get; set; } = string.Empty;

    [JsonPropertyName("challengeNonce")]
    [Required]
    public string ChallengeNonce { get; set; } = string.Empty;

    [JsonPropertyName("clientSignatureHex")]
    [Required]
    public string ClientSignatureHex { get; set; } = string.Empty;
}

public class ReviewActivationCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("decision")]
    [Required]
    public string Decision { get; set; } = string.Empty; // APPROVE or REJECT
}

public class DecideHardwareReviewCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("approve")]
    public bool Approve { get; set; }
}

public class InitiatePrimaryTransferCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("shopId")]
    public Guid ShopId { get; set; }

    [JsonPropertyName("sourceInstallationId")]
    public Guid SourceInstallationId { get; set; }

    [JsonPropertyName("targetInstallationId")]
    public Guid TargetInstallationId { get; set; }

    [JsonPropertyName("transferReason")]
    public string TransferReason { get; set; } = string.Empty;
}

public class ApprovePrimaryTransferCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("sensitiveApprovalId")]
    public Guid SensitiveApprovalId { get; set; }

    [JsonPropertyName("stepUpGrantToken")]
    public string? StepUpGrantToken { get; set; }

    [JsonPropertyName("contractRecoveryType")]
    public string? ContractRecoveryType { get; set; }

    [JsonPropertyName("contractEvidenceId")]
    public Guid? ContractEvidenceId { get; set; }

    [JsonIgnore]
    public JsonElement? CanonicalPayload { get; set; }
}

public class CompletePrimaryTransferCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;
}

public class DecideSensitiveApprovalCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("decision")]
    [Required]
    public string Decision { get; set; } = string.Empty; // APPROVE or REJECT

    [JsonPropertyName("targetPayloadSha256")]
    [Required]
    public string TargetPayloadSha256 { get; set; } = string.Empty;
}

public class LicenseLifecycleCommand
{
    [JsonPropertyName("commandMeta")]
    [Required]
    public CommandMetaDto CommandMeta { get; set; } = null!;

    [JsonPropertyName("stepUpGrantToken")]
    public string? StepUpGrantToken { get; set; }

    [JsonPropertyName("sensitiveApprovalId")]
    public Guid? SensitiveApprovalId { get; set; }
}
