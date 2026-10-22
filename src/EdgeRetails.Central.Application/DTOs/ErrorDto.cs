using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class ErrorDto
{
    [JsonPropertyName("code")]
    public string Code { get; set; } = string.Empty;

    [JsonPropertyName("requestId")]
    public Guid RequestId { get; set; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; set; }

    [JsonPropertyName("currentVersion")]
    public int? CurrentVersion { get; set; }

    [JsonPropertyName("outcomeReference")]
    public string? OutcomeReference { get; set; }

    [JsonPropertyName("safeMessageKey")]
    public string SafeMessageKey { get; set; } = string.Empty;
}
