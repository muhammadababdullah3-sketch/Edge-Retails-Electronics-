using EdgeRetails.Application.Abstractions;

namespace EdgeRetails.Application.Common;

public static class PersistenceGuard
{
    public static async Task<Result> SaveChangesAsync(
        IUnitOfWork unitOfWork,
        IExceptionToErrorMapper mapper,
        CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Success();
        }
        catch (Exception ex) when (mapper.TryMap(ex, out var error))
        {
            return Result.Failure(error.Code, error.Message);
        }
    }

    public static async Task<Result<T>> SaveChangesAsync<T>(
        IUnitOfWork unitOfWork,
        IExceptionToErrorMapper mapper,
        T successValue,
        CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result<T>.Success(successValue);
        }
        catch (Exception ex) when (mapper.TryMap(ex, out var error))
        {
            return Result<T>.Failure(error.Code, error.Message);
        }
    }
}
