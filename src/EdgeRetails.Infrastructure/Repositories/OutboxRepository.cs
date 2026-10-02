using EdgeRetails.Application.Production.Outbox;
using EdgeRetails.Domain.SystemConfiguration;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class OutboxRepository : IOutboxRepository
{
    private readonly EdgeRetailsDbContext _db;

    public OutboxRepository(EdgeRetailsDbContext db)
    {
        _db = db;
    }

    public void Enqueue(OutboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        _db.OutboxMessages.Add(message);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingMessagesAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        return await _db.OutboxMessages
            .Where(x => (x.Status == OutboxMessageStatus.Pending && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                     || (x.Status == OutboxMessageStatus.Processing && x.NextAttemptAt != null && x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> TryClaimMessageAsync(
        Guid messageId,
        string workerId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
        => await TryClaimMessageAsync(messageId, workerId, Guid.NewGuid(), leaseDuration, cancellationToken);

    public async Task<bool> TryClaimMessageAsync(
        Guid messageId,
        string workerId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerId);
        ArgumentOutOfRangeException.ThrowIfEqual(leaseToken, Guid.Empty);
        var now = DateTimeOffset.UtcNow;
        var leaseExpiry = now.Add(leaseDuration);

        try
        {
            var affected = await _db.OutboxMessages
                .Where(x => x.Id == messageId &&
                           ((x.Status == OutboxMessageStatus.Pending && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
                            || (x.Status == OutboxMessageStatus.Processing && x.NextAttemptAt != null && x.NextAttemptAt <= now)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(m => m.Status, OutboxMessageStatus.Processing)
                    .SetProperty(m => m.LeaseOwner, workerId)
                    .SetProperty(m => m.LeaseToken, leaseToken)
                    .SetProperty(m => m.NextAttemptAt, leaseExpiry),
                    cancellationToken);

            if (affected > 0)
            {
                var tracked = _db.ChangeTracker.Entries<OutboxMessage>()
                    .FirstOrDefault(e => e.Entity.Id == messageId)?.Entity;
                if (tracked is not null)
                {
                    tracked.Status = OutboxMessageStatus.Processing;
                    tracked.NextAttemptAt = leaseExpiry;
                    tracked.LeaseOwner = workerId;
                    tracked.LeaseToken = leaseToken;
                    tracked.LeaseExpiresAt = leaseExpiry;
                }
                return true;
            }

            return false;
        }
        catch (InvalidOperationException)
        {
            var message = await _db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
            if (message is null)
            {
                return false;
            }

            var canClaim = (message.Status == OutboxMessageStatus.Pending && (message.NextAttemptAt == null || message.NextAttemptAt <= now))
                        || (message.Status == OutboxMessageStatus.Processing && message.NextAttemptAt != null && message.NextAttemptAt <= now);

            if (!canClaim)
            {
                return false;
            }

            message.Status = OutboxMessageStatus.Processing;
            message.NextAttemptAt = leaseExpiry;
            message.LeaseOwner = workerId;
            message.LeaseToken = leaseToken;
            message.LeaseExpiresAt = leaseExpiry;
            await _db.SaveChangesAsync(cancellationToken);
            return true;
        }
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingMessagesAsync(
        string workerId,
        int batchSize,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetPendingMessagesAsync(batchSize, cancellationToken);
        var claimed = new List<OutboxMessage>();

        foreach (var candidate in candidates)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var leaseToken = Guid.NewGuid();
            if (await TryClaimMessageAsync(candidate.Id, workerId, leaseToken, leaseDuration, cancellationToken))
            {
                candidate.Status = OutboxMessageStatus.Processing;
                candidate.LeaseOwner = workerId;
                candidate.LeaseToken = leaseToken;
                candidate.LeaseExpiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
                candidate.NextAttemptAt = candidate.LeaseExpiresAt;
                claimed.Add(candidate);
            }
        }

        return claimed;
    }

    public async Task MarkCompletedAsync(Guid messageId, DateTimeOffset completedAt, CancellationToken cancellationToken = default)
    {
        var message = await _db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is not null)
        {
            message.AttemptCount++;
            message.Status = OutboxMessageStatus.Completed;
            message.CompletedAt = completedAt;
            message.LastError = null;
            message.LeaseOwner = null;
            message.LeaseExpiresAt = null;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken = default)
    {
        var message = await _db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is not null)
        {
            message.AttemptCount++;
            message.LastError = error;
            message.NextAttemptAt = nextAttemptAt;
            message.Status = nextAttemptAt.HasValue ? OutboxMessageStatus.Pending : OutboxMessageStatus.Failed;
            message.LeaseOwner = null;
            message.LeaseExpiresAt = null;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task MarkActionRequiredAsync(Guid messageId, string error, CancellationToken cancellationToken = default)
    {
        var message = await _db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is not null)
        {
            message.AttemptCount++;
            message.LastError = error;
            message.Status = OutboxMessageStatus.ActionRequired;
            message.NextAttemptAt = null;
            message.LeaseOwner = null;
            message.LeaseExpiresAt = null;
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task MarkCompletedAsync(Guid messageId, DateTimeOffset completedAt, Guid leaseToken, CancellationToken cancellationToken = default) =>
        SettleClaimAsync(messageId, leaseToken, cancellationToken, message =>
        {
            message.AttemptCount++;
            message.Status = OutboxMessageStatus.Completed;
            message.CompletedAt = completedAt;
            message.LastError = null;
            message.NextAttemptAt = null;
            message.LeaseOwner = null;
            message.LeaseToken = null;
        }, (query, ct) => query.ExecuteUpdateAsync(update => update
            .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
            .SetProperty(x => x.Status, OutboxMessageStatus.Completed)
            .SetProperty(x => x.CompletedAt, completedAt)
            .SetProperty(x => x.LastError, (string?)null)
            .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
            .SetProperty(x => x.LeaseOwner, (string?)null)
            .SetProperty(x => x.LeaseToken, (Guid?)null), ct));

    public Task MarkFailedAsync(Guid messageId, string error, DateTimeOffset? nextAttemptAt, Guid leaseToken, CancellationToken cancellationToken = default) =>
        SettleClaimAsync(messageId, leaseToken, cancellationToken, message =>
        {
            message.AttemptCount++;
            message.LastError = error;
            message.NextAttemptAt = nextAttemptAt;
            message.Status = nextAttemptAt.HasValue ? OutboxMessageStatus.Pending : OutboxMessageStatus.Failed;
            message.LeaseOwner = null;
            message.LeaseToken = null;
        }, (query, ct) => query.ExecuteUpdateAsync(update => update
            .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
            .SetProperty(x => x.LastError, error)
            .SetProperty(x => x.NextAttemptAt, nextAttemptAt)
            .SetProperty(x => x.Status, nextAttemptAt.HasValue ? OutboxMessageStatus.Pending : OutboxMessageStatus.Failed)
            .SetProperty(x => x.LeaseOwner, (string?)null)
            .SetProperty(x => x.LeaseToken, (Guid?)null), ct));

    public Task MarkActionRequiredAsync(Guid messageId, string error, Guid leaseToken, CancellationToken cancellationToken = default) =>
        SettleClaimAsync(messageId, leaseToken, cancellationToken, message =>
        {
            message.AttemptCount++;
            message.LastError = error;
            message.Status = OutboxMessageStatus.ActionRequired;
            message.NextAttemptAt = null;
            message.LeaseOwner = null;
            message.LeaseToken = null;
        }, (query, ct) => query.ExecuteUpdateAsync(update => update
            .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
            .SetProperty(x => x.LastError, error)
            .SetProperty(x => x.Status, OutboxMessageStatus.ActionRequired)
            .SetProperty(x => x.NextAttemptAt, (DateTimeOffset?)null)
            .SetProperty(x => x.LeaseOwner, (string?)null)
            .SetProperty(x => x.LeaseToken, (Guid?)null), ct));

    private async Task SettleClaimAsync(
        Guid messageId,
        Guid leaseToken,
        CancellationToken cancellationToken,
        Action<OutboxMessage> apply,
        Func<IQueryable<OutboxMessage>, CancellationToken, Task<int>> update)
    {
        if (_db.Database.IsRelational())
        {
            var query = _db.OutboxMessages
                .Where(x => x.Id == messageId &&
                            x.Status == OutboxMessageStatus.Processing &&
                            x.LeaseToken == leaseToken);
            var affected = await update(query, cancellationToken);

            var tracked = _db.ChangeTracker.Entries<OutboxMessage>()
                .FirstOrDefault(x => x.Entity.Id == messageId)?.Entity;
            if (tracked is not null)
            {
                if (affected > 0)
                {
                    apply(tracked);
                }
                else
                {
                    await _db.Entry(tracked).ReloadAsync(cancellationToken);
                }
            }
            return;
        }

        var message = await _db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
        if (message is null || message.Status != OutboxMessageStatus.Processing || message.LeaseToken != leaseToken)
        {
            return;
        }

        apply(message);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public Task<OutboxMessage?> GetByIdAsync(Guid messageId, CancellationToken cancellationToken = default)
        => _db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
}
