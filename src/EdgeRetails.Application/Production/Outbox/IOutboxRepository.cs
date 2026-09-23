using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Application.Production.Outbox;

public interface IOutboxWriter
{
    void Enqueue(OutboxMessage message);
}

public interface IOutboxRepository : IOutboxWriter
{
    Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, CancellationToken cancellationToken = default);
    Task MarkCompletedAsync(Guid messageId, DateTimeOffset completedAt, CancellationToken cancellationToken = default);
    Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default);
    Task MarkActionRequiredAsync(Guid messageId, string error, CancellationToken cancellationToken = default);
    Task<OutboxMessage?> GetByIdAsync(Guid messageId, CancellationToken cancellationToken = default);
}
