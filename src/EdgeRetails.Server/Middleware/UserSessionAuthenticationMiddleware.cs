using EdgeRetails.Application.Abstractions;
using EdgeRetails.Domain.Identity;

namespace EdgeRetails.Server.Middleware;

public sealed record ActorContext(
    Guid UserId,
    Guid SessionId,
    string DisplayName,
    Guid RoleId,
    string RoleName,
    IReadOnlySet<string> Permissions);

public static class HttpContextActorExtensions
{
    public static ActorContext? GetActorContext(this HttpContext context) =>
        context.Items.TryGetValue("ActorContext", out var item) ? item as ActorContext : null;

    public static Guid GetCurrentUserId(this HttpContext context) =>
        context.GetActorContext()?.UserId ?? Guid.Empty;
}

public sealed class UserSessionAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> WhitelistedExact = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/system/health",
        "/api/system/ready",
        "/api/system/version",
        "/api/terminals/register",
        "/api/terminals/heartbeat",
        "/api/terminals/status",
        "/api/terminals/revalidate",
        "/api/auth/login",
        "/api/auth/accounts",
        "/api/recovery/owner-pin",
        "/api/recovery/context",
        "/api/setup/state",
        "/api/setup/bootstrap"
    };

    public UserSessionAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        IIdentitySessionRepository sessionRepository,
        IIdentityReadRepository identityRepository)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;

        // Skip non-API paths or whitelisted endpoints
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            WhitelistedExact.Contains(path))
        {
            await _next(context);
            return;
        }

        // Legacy operation status route: supports terminal-only authority (minimal status)
        // or session authority (full canonical status). If session header is present, validate and inject ActorContext.
        if (path.StartsWith("/api/system/operations/", StringComparison.OrdinalIgnoreCase))
        {
            if (context.Request.Headers.TryGetValue("X-Session-Id", out var optSessionVal) &&
                Guid.TryParse(optSessionVal.ToString(), out var optSessionId))
            {
                var optSession = await sessionRepository.GetSessionAsync(optSessionId, context.RequestAborted);
                if (optSession is not null && !optSession.IsRevoked && optSession.EndedAt is null)
                {
                    var optUser = await identityRepository.GetUserAsync(optSession.UserId, context.RequestAborted);
                    if (optUser is not null && optUser.Status == UserStatus.Active)
                    {
                        var optRole = await identityRepository.GetRoleAsync(optUser.RoleId, context.RequestAborted);
                        if (optRole is not null && optRole.IsActive)
                        {
                            var optPerms = await identityRepository.GetEffectivePermissionKeysAsync(optUser.Id, context.RequestAborted);
                            context.Items["ActorContext"] = new ActorContext(
                                optUser.Id,
                                optSession.Id,
                                optUser.DisplayName,
                                optRole.Id,
                                optRole.Name,
                                optPerms);
                            context.Items["CurrentUserId"] = optUser.Id;
                        }
                    }
                }
            }

            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Session-Id", out var sessionIdValues) ||
            !Guid.TryParse(sessionIdValues.ToString(), out var sessionId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.session_missing",
                message = "X-Session-Id header is required for authenticated requests."
            });
            return;
        }

        var session = await sessionRepository.GetSessionAsync(sessionId, context.RequestAborted);
        if (session is null || session.IsRevoked || session.EndedAt is not null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.session_invalid",
                message = "Session is invalid, expired, or revoked."
            });
            return;
        }

        var user = await identityRepository.GetUserAsync(session.UserId, context.RequestAborted);
        if (user is null || user.Status != UserStatus.Active)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.user_disabled",
                message = "User account is disabled or not found."
            });
            return;
        }

        var role = await identityRepository.GetRoleAsync(user.RoleId, context.RequestAborted);
        if (role is null || !role.IsActive)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.role_disabled",
                message = "User role is inactive or not found."
            });
            return;
        }

        var permissions = await identityRepository.GetEffectivePermissionKeysAsync(user.Id, context.RequestAborted);

        var actorContext = new ActorContext(
            user.Id,
            session.Id,
            user.DisplayName,
            role.Id,
            role.Name,
            permissions);

        context.Items["ActorContext"] = actorContext;
        context.Items["CurrentUserId"] = user.Id;

        await _next(context);
    }
}
