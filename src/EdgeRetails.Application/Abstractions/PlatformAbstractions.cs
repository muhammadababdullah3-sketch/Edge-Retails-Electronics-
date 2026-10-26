using EdgeRetails.Application.Common;

namespace EdgeRetails.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly ShopDate { get; }
}

public interface IShopClock : IClock
{
    DateTimeOffset ShopNow { get; }
    TimeZoneInfo ShopTimeZone { get; }
    DateOnly ToShopDate(DateTimeOffset utcTimestamp);
    DateTimeOffset ToUtc(DateOnly shopDate, TimeOnly time);
    (DateTimeOffset StartUtc, DateTimeOffset EndUtc) GetShopDayUtcRange(DateOnly shopDate);
}

/// <summary>Phase 8 configured shop time and business-date authority.</summary>
public interface IShopTimeAuthority : IShopClock
{
    DateOnly GetCurrentShopDate();

    ShopClockIntegrity Integrity { get; }
}

public readonly record struct ShopClockIntegrity(bool IsTrusted, string? AnomalyCode);

public interface IIdGenerator
{
    Guid NewId();
}

public interface IDocumentNumberService
{
    Task<string> NextAsync(string series, CancellationToken cancellationToken);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Maps infrastructure persistence exceptions to application errors (Phase 8 concurrency).</summary>
public interface IExceptionToErrorMapper
{
    bool TryMap(Exception exception, out Error error);
}

/// <summary>Single-machine host lifecycle (Phase 8 P8-LIFE-01).</summary>
public interface ISingleHostLifecycleState
{
    DateTimeOffset ServerStartedAtUtc { get; }
    bool IsDraining { get; }
    void BeginDrain();
}

public sealed record SingleHostWorkerHeartbeatStatus(
    bool HeartbeatFileFound,
    DateTimeOffset? LastBeatUtc,
    bool IsStale,
    string? HeartbeatPath);

public interface IReceiptSnapshotProvider
{
    Task<string> CaptureAsync(CancellationToken cancellationToken);
}

public interface IBusinessAuditWriter
{
    void Record(
        string action,
        string entityType,
        Guid? entityId,
        Guid actorId,
        Guid correlationId,
        string? summary = null);
}

public interface IOperationLock
{
    Task AcquireAsync(Guid clientOperationId, CancellationToken cancellationToken);
}

public interface IResourceLock
{
    Task AcquireAsync(
        string resourceType,
        Guid resourceId,
        CancellationToken cancellationToken);

    Task AcquireAsync(
        string resourceType,
        string resourceKey,
        CancellationToken cancellationToken);
}

public interface ITransactionRunner
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

