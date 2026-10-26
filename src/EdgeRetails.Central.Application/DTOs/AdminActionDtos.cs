using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public class AdminActionCommandDto
{
    [JsonPropertyName("adminUserId")]
    public Guid AdminUserId { get; set; }

    [JsonPropertyName("action")]
    [Required]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("roleId")]
    public Guid? RoleId { get; set; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }
}

public class OperationStatusDto
{
    [JsonPropertyName("clientOperationId")]
    public Guid ClientOperationId { get; set; }

    [JsonPropertyName("operationType")]
    public string OperationType { get; set; } = string.Empty;

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("outcomeId")]
    public Guid OutcomeId { get; set; }

    [JsonPropertyName("resultReference")]
    public string? ResultReference { get; set; }

    [JsonPropertyName("resourceVersion")]
    public int ResourceVersion { get; set; }

    [JsonPropertyName("authorityRevision")]
    public int AuthorityRevision { get; set; }

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [JsonPropertyName("completedAtUtc")]
    public DateTimeOffset? CompletedAtUtc { get; set; }

    [JsonPropertyName("safeError")]
    public ErrorDto? SafeError { get; set; }
}
