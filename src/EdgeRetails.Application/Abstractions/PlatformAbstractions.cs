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

public interface ITransactionRunner
{
    Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);
}

public interface IQuotationSaleConverter
{
    Task<Result<Guid>> ConvertAsync(
        Guid quotationId,
        Guid actorId,
        CancellationToken cancellationToken);
}
