using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Audit;
using EdgeRetails.Infrastructure.Persistence;

namespace EdgeRetails.Infrastructure.Services;

public sealed class BusinessAuditWriter : IBusinessAuditWriter
{
    private readonly EdgeRetailsDbContext _db;
    private readonly IClock _clock;

    public BusinessAuditWriter(
        EdgeRetailsDbContext db,
        IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public void Record(
        string action,
        string entityType,
        Guid? entityId,
        Guid actorId,
        Guid correlationId,
        string? summary = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        _db.BusinessAuditEvents.Add(new BusinessAuditEvent
        {
            ActorId = actorId,
            Action = action.Trim().ToUpperInvariant(),
            EntityType = entityType.Trim().ToUpperInvariant(),
            EntityId = entityId,
            CorrelationId = correlationId,
            OccurredAt = _clock.UtcNow,
            Summary = Normalize(summary)
        });
    }

    private static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
