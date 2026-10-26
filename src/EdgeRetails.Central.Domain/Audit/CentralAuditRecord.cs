using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Domain.Audit;

public class CentralAuditRecord : Entity
{
    public DateTimeOffset TimestampUtc { get; private set; }
    public string ActorId { get; private set; } = string.Empty;
    public string? ActorUsername { get; private set; }
    public Guid? SessionId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Resource { get; private set; } = string.Empty;
    public string? ResourceId { get; private set; }
    public string? Reason { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? ClientIp { get; private set; }
    public string? UserAgent { get; private set; }
    public string? PayloadSha256 { get; private set; }
    public string DetailsJson { get; private set; } = "{}";
    public bool IsSuccess { get; private set; }

    private CentralAuditRecord() { }

    public static CentralAuditRecord Create(
        string actorId,
        string? actorUsername,
        Guid? sessionId,
        string action,
        string resource,
        string? resourceId,
        string? reason,
        string correlationId,
        string? clientIp,
        string? userAgent,
        string? payloadSha256,
        string detailsJson,
        bool isSuccess,
        DateTimeOffset timestampUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(resource);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        return new CentralAuditRecord
        {
            Id = Guid.NewGuid(),
            TimestampUtc = timestampUtc,
            ActorId = actorId,
            ActorUsername = actorUsername,
            SessionId = sessionId,
            Action = action,
            Resource = resource,
            ResourceId = resourceId,
            Reason = reason,
            CorrelationId = correlationId,
            ClientIp = clientIp,
            UserAgent = userAgent,
            PayloadSha256 = payloadSha256,
            DetailsJson = detailsJson,
            IsSuccess = isSuccess
        };
    }
}
