using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class SigningRequestDto
{
    [JsonPropertyName("signingRequestId")]
    public Guid SigningRequestId { get; set; }

    [JsonPropertyName("issuanceRequestId")]
    public Guid IssuanceRequestId { get; set; }

    [JsonPropertyName("payloadSha256")]
    [Required]
    public string PayloadSha256 { get; set; } = string.Empty;

    [JsonPropertyName("authorityRevision")]
    public int AuthorityRevision { get; set; }

    [JsonPropertyName("keyId")]
    [Required]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("keyVersion")]
    public int KeyVersion { get; set; }

    [JsonPropertyName("purpose")]
    [Required]
    public string Purpose { get; set; } = string.Empty;

    [JsonPropertyName("canonicalPayload")]
    [Required]
    public string CanonicalPayload { get; set; } = string.Empty;
}

public class SigningResponseDto
{
    [JsonPropertyName("signingRequestId")]
    public Guid SigningRequestId { get; set; }

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;

    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("keyVersion")]
    public int KeyVersion { get; set; }

    [JsonPropertyName("algorithmProfileId")]
    public string AlgorithmProfileId { get; set; } = "RS256";

    [JsonPropertyName("signedAtUtc")]
    public DateTimeOffset SignedAtUtc { get; set; }
}

public class KeyDescriptorDto
{
    [JsonPropertyName("keyId")]
    public string KeyId { get; set; } = string.Empty;

    [JsonPropertyName("keyVersion")]
    public int KeyVersion { get; set; }

    [JsonPropertyName("algorithmProfileId")]
    public string AlgorithmProfileId { get; set; } = "RS256";

    [JsonPropertyName("purpose")]
    public string Purpose { get; set; } = string.Empty;

    [JsonPropertyName("publicKey")]
    public string PublicKey { get; set; } = string.Empty;

    [JsonPropertyName("validFromUtc")]
    public DateTimeOffset ValidFromUtc { get; set; }

    [JsonPropertyName("validUntilUtc")]
    public DateTimeOffset ValidUntilUtc { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}

public class TrustSetDto
{
    [JsonPropertyName("trustSetVersion")]
    public int TrustSetVersion { get; set; }

    [JsonPropertyName("keys")]
    public List<KeyDescriptorDto> Keys { get; set; } = new();

    [JsonPropertyName("revokedKeyIds")]
    public List<string> RevokedKeyIds { get; set; } = new();

    [JsonPropertyName("emergencyRolloverRevision")]
    public int EmergencyRolloverRevision { get; set; }

    [JsonPropertyName("signedByTrustedRootKeyId")]
    public string SignedByTrustedRootKeyId { get; set; } = string.Empty;

    [JsonPropertyName("signature")]
    public string Signature { get; set; } = string.Empty;
}
