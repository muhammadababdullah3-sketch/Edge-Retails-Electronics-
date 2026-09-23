using EdgeRetails.Application.Production;

namespace EdgeRetails.Infrastructure.Production;

public sealed class DefaultProductionAuthorization : IProductionAuthorization
{
    public Task EnsureAuthenticatedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task EnsurePermissionAsync(string permission, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return Task.CompletedTask;
    }
}
