using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class OutcomeDto
{
    [JsonPropertyName("clientOperationId")]
    public Guid ClientOperationId { get; set; }

    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = string.Empty;

    [JsonPropertyName("payloadSha256")]
    public string PayloadSha256 { get; set; } = string.Empty;

    [JsonPropertyName("outcomeId")]
    public Guid OutcomeId { get; set; }

    [JsonPropertyName("resultReference")]
    public string? ResultReference { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("resourceVersion")]
    public int ResourceVersion { get; set; }

    [JsonPropertyName("authorityRevision")]
    public int AuthorityRevision { get; set; }

    [JsonPropertyName("safeError")]
    public ErrorDto? SafeError { get; set; }
}
