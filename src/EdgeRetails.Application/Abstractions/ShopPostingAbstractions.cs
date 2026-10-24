using EdgeRetails.Application.Common;
using EdgeRetails.Domain.Posting;

namespace EdgeRetails.Application.Abstractions;

public interface IPeriodLockRepository
{
    Task<bool> IsPeriodLockedAsync(DateOnly postingDate, CancellationToken cancellationToken = default);
    Task<PeriodLock?> GetLockedPeriodForDateAsync(DateOnly postingDate, CancellationToken cancellationToken = default);
    void AddPeriodLock(PeriodLock periodLock);
}

public interface IPostingAuditRepository
{
    void Record(PostingAuditEntry entry);
    Task<IReadOnlyList<PostingAuditEntry>> GetEntriesByEntityAsync(Guid entityId, CancellationToken cancellationToken = default);
}

public sealed record PostingValidationCommand(
    string OperationType,
    Guid EntityId,
    string EntityNumber,
    Guid ActorId,
    DateOnly PostingDate,
    DateOnly? SupplierDocumentDate = null,
    SupervisorApprovalRequest? SupervisorApproval = null,
    bool? CallerSuppliedBooleanOverride = null);

public interface IShopPostingValidationService
{
    Task<Result<PostingAuditEntry>> ValidateAndAuditPostingAsync(
        PostingValidationCommand command,
        CancellationToken cancellationToken = default);
}

public interface IShopPeriodBoundaryService
{
    (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetDailyUtcBoundary(DateOnly day);
    (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetDateRangeUtcBoundary(DateOnly startDate, DateOnly endDate);
    (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetHourlyUtcBoundary(DateOnly day, int hour);
    (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetMonthlyUtcBoundary(int year, int month);
    (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetYearlyUtcBoundary(int year);
}
