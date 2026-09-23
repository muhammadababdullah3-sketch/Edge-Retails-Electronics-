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
            .Where(x => (x.Status == OutboxMessageStatus.Pending || x.Status == OutboxMessageStatus.Processing)
                     && (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
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
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    public Task<OutboxMessage?> GetByIdAsync(Guid messageId, CancellationToken cancellationToken = default)
        => _db.OutboxMessages.SingleOrDefaultAsync(x => x.Id == messageId, cancellationToken);
}
