using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class CommandMetaDto
{
    [JsonPropertyName("clientOperationId")]
    public Guid ClientOperationId { get; set; }

    [JsonPropertyName("operationType")]
    [Required]
    public string OperationType { get; set; } = string.Empty;

    [JsonPropertyName("fingerprintVersion")]
    public int FingerprintVersion { get; set; } = 1;

    [JsonPropertyName("payloadSha256")]
    [Required]
    [RegularExpression("^[a-f0-9]{64}$")]
    public string PayloadSha256 { get; set; } = string.Empty;

    [JsonPropertyName("expectedVersion")]
    public int ExpectedVersion { get; set; }

    [JsonPropertyName("policyVersion")]
    public int PolicyVersion { get; set; }

    [JsonPropertyName("reason")]
    [Required]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("sensitiveApprovalId")]
    public Guid? SensitiveApprovalId { get; set; }
}
