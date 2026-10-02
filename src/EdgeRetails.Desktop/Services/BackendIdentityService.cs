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
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly DesktopApiClient? _apiClient;

    public BackendIdentityService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public BackendIdentityService(DesktopApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<IReadOnlyList<BackendLoginAccount>> GetAccountsAsync(
        CancellationToken cancellationToken = default)
    {
        if (_apiClient is not null)
        {
            var accounts = await _apiClient.GetAsync<LoginAccountDto[]>(
                "/api/auth/accounts", cancellationToken);
            return accounts.Select(x => new BackendLoginAccount(
                x.UserId, x.DisplayName, x.RoleName, x.Initials, x.IsPrimary)).ToArray();
        }

        await using var scope = _scopeFactory!.CreateAsyncScope();
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

        if (_apiClient is not null)
        {
            ServerAuthenticatedUserDto authenticated;
            try
            {
                authenticated = await _apiClient.PostAsync<AuthenticateUserCommand, ServerAuthenticatedUserDto>(
                    "/api/auth/login",
                    new AuthenticateUserCommand(userId, pin, Guid.CreateVersion7()),
                    cancellationToken);
            }
            catch (DesktopApiException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new UnauthorizedAccessException(ex.Message, ex);
            }

            _apiClient.SetSession(authenticated.SessionId);
            try
            {
                var session = await _apiClient.GetAsync<ServerSessionDto>(
                    "/api/auth/session", cancellationToken);
                if (session.UserId != authenticated.UserId || session.SessionId != authenticated.SessionId)
                {
                    throw new UnauthorizedAccessException("Server session identity changed during sign in.");
                }

                return new BackendAuthenticatedSession(
                    session.UserId,
                    session.SessionId,
                    session.DisplayName,
                    session.RoleName,
                    authenticated.Initials,
                    new HashSet<string>(session.Permissions, StringComparer.OrdinalIgnoreCase));
            }
            catch
            {
                _apiClient.SetSession(null);
                throw;
            }
        }

        await using var scope = _scopeFactory!.CreateAsyncScope();
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

        if (_apiClient is not null)
        {
            try
            {
                await _apiClient.PostAsync<EndUserSessionCommand, LogoutResult>(
                    "/api/auth/logout",
                    new EndUserSessionCommand(userId, sessionId, Guid.CreateVersion7()),
                    cancellationToken);
            }
            finally
            {
                _apiClient.ClearSessionIfMatches(sessionId);
            }

            return;
        }

        await using var scope = _scopeFactory!.CreateAsyncScope();
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

    // HTTP permission arrays need a concrete JSON collection type. The
    // Application response keeps its IReadOnlySet contract inside the Server.
    private sealed record ServerAuthenticatedUserDto(
        Guid UserId,
        Guid SessionId,
        string DisplayName,
        string RoleName,
        string Initials);

    private sealed record ServerSessionDto(
        Guid UserId,
        Guid SessionId,
        string DisplayName,
        string RoleName,
        string[] Permissions);

    private sealed record LogoutResult(bool Success);
}
