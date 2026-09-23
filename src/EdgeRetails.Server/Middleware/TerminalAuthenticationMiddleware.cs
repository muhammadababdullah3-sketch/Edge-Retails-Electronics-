using EdgeRetails.Application.Features.Terminals;
using EdgeRetails.Domain.SystemConfiguration;

namespace EdgeRetails.Server.Middleware;

public sealed class TerminalAuthenticationMiddleware
{
    private readonly RequestDelegate _next;

    private static readonly HashSet<string> AnonymousEndpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/terminals/register",
        "/api/system/health",
        "/api/system/version"
    };

    public TerminalAuthenticationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ITerminalRepository terminalRepository)
    {
        var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;

        // Skip non-API paths or whitelisted anonymous endpoints or operation query status
        if (!path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            AnonymousEndpoints.Contains(path) ||
            path.StartsWith("/api/system/operations/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue("X-Terminal-Id", out var terminalIdValues) ||
            !Guid.TryParse(terminalIdValues.ToString(), out var terminalId))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.terminal_id_missing",
                message = "X-Terminal-Id header is required for authenticated terminal requests."
            });
            return;
        }

        var terminal = await terminalRepository.GetByIdAsync(terminalId, context.RequestAborted);
        if (terminal is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.terminal_unknown",
                message = $"Terminal '{terminalId}' is not registered on this server."
            });
            return;
        }

        if (terminal.Status == TerminalStatus.Revoked)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.terminal_revoked",
                message = "Terminal access has been revoked by administration."
            });
            return;
        }

        if (terminal.Status == TerminalStatus.Suspended)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.terminal_suspended",
                message = "Terminal access is currently suspended."
            });
            return;
        }

        // Terminal credentials are mandatory for every authenticated terminal endpoint.
        if (!context.Request.Headers.TryGetValue("X-Terminal-Secret", out var clientSecretValues) ||
            string.IsNullOrWhiteSpace(clientSecretValues.ToString()))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.terminal_secret_missing",
                message = "X-Terminal-Secret header is required for authenticated terminal requests."
            });
            return;
        }

        var clientSecret = clientSecretValues.ToString();
        if (!terminal.VerifySecret(clientSecret))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "auth.invalid_secret",
                message = "Provided terminal authentication secret is invalid."
            });
            return;
        }

        context.Items["CurrentTerminal"] = terminal;
        await _next(context);
    }
}
