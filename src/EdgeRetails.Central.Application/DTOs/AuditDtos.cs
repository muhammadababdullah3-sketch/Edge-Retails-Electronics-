using System.Text.Json.Serialization;

namespace EdgeRetails.Central.Application.DTOs;

public class AuditQueryDto
{
    [JsonPropertyName("cursor")]
    public string? Cursor { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; } = 25;

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("asOfVersion")]
    public long? AsOfVersion { get; set; }
}

public class AuditRecordDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("actorId")]
    public string ActorId { get; set; } = string.Empty;

    [JsonPropertyName("actorUsername")]
    public string? ActorUsername { get; set; }

    [JsonPropertyName("sessionId")]
    public Guid? SessionId { get; set; }

    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    [JsonPropertyName("resource")]
    public string Resource { get; set; } = string.Empty;

    [JsonPropertyName("resourceId")]
    public string? ResourceId { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonPropertyName("correlationId")]
    public string CorrelationId { get; set; } = string.Empty;

    [JsonPropertyName("detailsJson")]
    public string DetailsJson { get; set; } = "{}";

    [JsonPropertyName("isSuccess")]
    public bool IsSuccess { get; set; }

    [JsonPropertyName("timestampUtc")]
    public DateTimeOffset TimestampUtc { get; set; }
}

public class AuditPageDto
{
    [JsonPropertyName("items")]
    public List<AuditRecordDto> Items { get; set; } = new();

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; set; }

    [JsonPropertyName("asOfVersion")]
    public long AsOfVersion { get; set; }

    [JsonPropertyName("authorizedCount")]
    public int AuthorizedCount { get; set; }

    [JsonPropertyName("projectionRevision")]
    public long ProjectionRevision { get; set; }
}
