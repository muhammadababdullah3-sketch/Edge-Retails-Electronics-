using EdgeRetails.Application.Common;

namespace EdgeRetails.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
    DateOnly ShopDate { get; }
}

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

