using EdgeRetails.Application.Features.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EdgeRetails.Desktop.Services;

public sealed record BackendLoginAccount(
    Guid UserId,
    string DisplayName,
    string RoleName,
    string Initials,
    bool IsPrimary);

public sealed record BackendAuthenticatedSession(
    Guid UserId,
    Guid SessionId,
    string DisplayName,
    string RoleName,
    string Initials,
    IReadOnlySet<string> PermissionKeys);

public interface IBackendIdentityService
{
    Task<IReadOnlyList<BackendLoginAccount>> GetAccountsAsync(
        CancellationToken cancellationToken = default);

    Task<BackendAuthenticatedSession> AuthenticateAsync(
        string accountId,
        string pin,
        CancellationToken cancellationToken = default);
    Task SignOutAsync(
        ISessionContext session,
        CancellationToken cancellationToken = default);
}

public sealed class BackendIdentityService : IBackendIdentityService
{
    private readonly IServiceScopeFactory _scopeFactory;

    public BackendIdentityService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<BackendLoginAccount>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<GetLoginAccountsHandler>();
        var rows = await handler.HandleAsync(cancellationToken);

        return rows.Select(x => new BackendLoginAccount(
            x.UserId,
            x.DisplayName,
            x.RoleName,
            x.Initials,
            x.IsPrimary)).ToArray();
    }
    public async Task<BackendAuthenticatedSession> AuthenticateAsync(
        string accountId,
        string pin,
        CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(accountId, out var userId))
        {
            throw new InvalidOperationException("Selected account is not a persistent backend user.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<AuthenticateUserHandler>();
        var result = await handler.HandleAsync(
            new AuthenticateUserCommand(
                userId,
                pin,
                Guid.CreateVersion7()),
            cancellationToken);

        if (!result.IsSuccess || result.Value is null)
        {
            throw new UnauthorizedAccessException(
                result.Error?.Message ?? "Invalid user or PIN.");
        }

        return new BackendAuthenticatedSession(
            result.Value.UserId,
            result.Value.SessionId,
            result.Value.DisplayName,
            result.Value.RoleName,
            result.Value.Initials,
            result.Value.PermissionKeys);
    }
    public async Task SignOutAsync(
        ISessionContext session,
        CancellationToken cancellationToken = default)
    {
        if (session.UserId is not Guid userId ||
            session.SessionId is not Guid sessionId)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var handler = scope.ServiceProvider
            .GetRequiredService<EndUserSessionHandler>();
        var result = await handler.HandleAsync(
            new EndUserSessionCommand(
                userId,
                sessionId,
                Guid.CreateVersion7()),
            cancellationToken);

        if (!result.IsSuccess)
        {
            throw new InvalidOperationException(
                result.Error?.Message ?? "User session could not be closed.");
        }
    }
}
