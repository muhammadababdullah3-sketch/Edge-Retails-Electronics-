using System.Collections.Concurrent;
using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Posting;
using EdgeRetails.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Infrastructure.Repositories;

public sealed class EfPeriodLockRepository : IPeriodLockRepository
{
    private readonly EdgeRetailsDbContext _db;
    private readonly IResourceLock? _resourceLock;

    public EfPeriodLockRepository(EdgeRetailsDbContext db, IResourceLock? resourceLock = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _resourceLock = resourceLock;
    }

    public async Task<bool> IsPeriodLockedAsync(DateOnly postingDate, CancellationToken cancellationToken = default)
    {
        if (_resourceLock is not null && _db.Database.CurrentTransaction is not null)
        {
            await _resourceLock.AcquireAsync("posting_period_date", postingDate.ToString("yyyy-MM-dd"), cancellationToken);
        }

        return await _db.PeriodLocks
            .AsNoTracking()
            .AnyAsync(p => p.IsLocked && postingDate >= p.PeriodStartDate && postingDate <= p.PeriodEndDate, cancellationToken);
    }

    public async Task<PeriodLock?> GetLockedPeriodForDateAsync(DateOnly postingDate, CancellationToken cancellationToken = default)
    {
        if (_resourceLock is not null && _db.Database.CurrentTransaction is not null)
        {
            await _resourceLock.AcquireAsync("posting_period_date", postingDate.ToString("yyyy-MM-dd"), cancellationToken);
        }

        return await _db.PeriodLocks
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.IsLocked && postingDate >= p.PeriodStartDate && postingDate <= p.PeriodEndDate, cancellationToken);
    }

    public void AddPeriodLock(PeriodLock periodLock)
    {
        ArgumentNullException.ThrowIfNull(periodLock);
        _db.PeriodLocks.Add(periodLock);
    }

    public async Task AddPeriodLockTransactionalAsync(PeriodLock periodLock, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(periodLock);
        if (_resourceLock is not null && _db.Database.CurrentTransaction is not null)
        {
            for (var d = periodLock.PeriodStartDate; d <= periodLock.PeriodEndDate; d = d.AddDays(1))
            {
                await _resourceLock.AcquireAsync("posting_period_date", d.ToString("yyyy-MM-dd"), cancellationToken);
            }
        }
        _db.PeriodLocks.Add(periodLock);
    }
}

public sealed class EfPostingAuditRepository : IPostingAuditRepository
{
    private readonly EdgeRetailsDbContext _db;

    public EfPostingAuditRepository(EdgeRetailsDbContext db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    public void Record(PostingAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.ValidateInvariants();
        _db.PostingAuditEntries.Add(entry);
    }

    public async Task<IReadOnlyList<PostingAuditEntry>> GetEntriesByEntityAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var matched = await _db.PostingAuditEntries
            .AsNoTracking()
            .Where(e => e.EntityId == entityId)
            .OrderBy(e => e.EventTimestampUtc)
            .ToListAsync(cancellationToken);
        return matched;
    }
}

public sealed class InMemoryPeriodLockRepository : IPeriodLockRepository
{
    private readonly ConcurrentBag<PeriodLock> _periodLocks = new();

    public Task<bool> IsPeriodLockedAsync(DateOnly postingDate, CancellationToken cancellationToken = default)
    {
        var isLocked = _periodLocks.Any(p => p.IsDateInLockedPeriod(postingDate));
        return Task.FromResult(isLocked);
    }

    public Task<PeriodLock?> GetLockedPeriodForDateAsync(DateOnly postingDate, CancellationToken cancellationToken = default)
    {
        var period = _periodLocks.FirstOrDefault(p => p.IsDateInLockedPeriod(postingDate));
        return Task.FromResult(period);
    }

    public void AddPeriodLock(PeriodLock periodLock)
    {
        ArgumentNullException.ThrowIfNull(periodLock);
        _periodLocks.Add(periodLock);
    }
}

public sealed class PostingAuditRepository : IPostingAuditRepository
{
    private readonly ConcurrentBag<PostingAuditEntry> _entries = new();

    public void Record(PostingAuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries.Add(entry);
    }

    public Task<IReadOnlyList<PostingAuditEntry>> GetEntriesByEntityAsync(Guid entityId, CancellationToken cancellationToken = default)
    {
        var matched = _entries.Where(e => e.EntityId == entityId).ToList();
        return Task.FromResult<IReadOnlyList<PostingAuditEntry>>(matched);
    }
}

public sealed class FinancialPostingAdapter
{
    private readonly IShopPostingValidationService _postingValidation;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ITransactionRunner _transactions;

    public FinancialPostingAdapter(
        IShopPostingValidationService postingValidation,
        IUnitOfWork unitOfWork,
        ITransactionRunner transactions)
    {
        _postingValidation = postingValidation ?? throw new ArgumentNullException(nameof(postingValidation));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
    }

    public async Task<PostingAuditEntry> ExecuteTransactionalPostingAsync(
        PostingValidationCommand command,
        Func<Task> domainAction,
        CancellationToken cancellationToken = default)
    {
        return await _transactions.ExecuteAsync(async ct =>
        {
            // Step 1: Execute posting validation (enforces 7-day rule, locks, supervisor PIN)
            var validationResult = await _postingValidation.ValidateAndAuditPostingAsync(command, ct);
            if (!validationResult.IsSuccess)
            {
                throw new Domain.Common.BusinessRuleException(
                    validationResult.Error?.Code ?? "posting.validation_failed",
                    validationResult.Error?.Message ?? "Posting validation failed.");
            }

            // Step 2: Execute domain mutation (e.g. Purchase, Sale, Expense)
            await domainAction();

            // Step 3: Atomic unit-of-work save (audit and domain entities commit together)
            await _unitOfWork.SaveChangesAsync(ct);

            return validationResult.Value!;
        }, cancellationToken);
    }
}
