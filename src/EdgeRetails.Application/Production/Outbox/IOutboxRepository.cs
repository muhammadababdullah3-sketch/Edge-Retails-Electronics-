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

    Task<bool> TryClaimMessageAsync(
        Guid messageId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    Task<bool> TryClaimMessageAsync(
        Guid messageId,
        string workerId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default) =>
        TryClaimMessageAsync(messageId, workerId, leaseDuration, cancellationToken);

    Task MarkCompletedAsync(Guid messageId, DateTimeOffset completedAt, Guid leaseToken, CancellationToken cancellationToken = default) =>
        MarkCompletedAsync(messageId, completedAt, cancellationToken);

    Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? nextAttemptAt, Guid leaseToken, CancellationToken cancellationToken = default) =>
        MarkFailedAsync(messageId, error, nextAttemptAt, cancellationToken);

    Task MarkActionRequiredAsync(Guid messageId, string error, Guid leaseToken, CancellationToken cancellationToken = default) =>
        MarkActionRequiredAsync(messageId, error, cancellationToken);

    async Task<IReadOnlyList<OutboxMessage>> ClaimPendingMessagesAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetPendingMessagesAsync(batchSize, cancellationToken);
        var claimed = new List<OutboxMessage>();
        foreach (var message in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var leaseToken = Guid.NewGuid();
            if (await TryClaimMessageAsync(message.Id, workerId, leaseToken, leaseDuration, cancellationToken))
            {
                message.LeaseOwner = workerId;
                message.LeaseToken = leaseToken;
                claimed.Add(message);
            }
        }
        return claimed;
    }
}
