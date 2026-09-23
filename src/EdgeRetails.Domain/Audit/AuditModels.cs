using EdgeRetails.Domain.Common;

namespace EdgeRetails.Domain.Audit;

public sealed class BusinessAuditEvent : Entity
{
    public Guid ActorId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public Guid? EntityId { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string? Summary { get; set; }
}
