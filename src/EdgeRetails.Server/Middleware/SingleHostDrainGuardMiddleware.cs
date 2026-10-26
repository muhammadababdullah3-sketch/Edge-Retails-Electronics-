using EdgeRetails.Application.Abstractions;

namespace EdgeRetails.Server.Middleware;

public sealed class SingleHostDrainGuardMiddleware
{
    private readonly RequestDelegate _next;

    public SingleHostDrainGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, ISingleHostLifecycleState lifecycle)
    {
        if (lifecycle.IsDraining &&
            HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
            !IsAllowedDuringDrain(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                code = "system.server_draining",
                message = "Server is shutting down; new mutations are not accepted."
            });
            return;
        }

        await _next(context);
    }

    private static bool IsAllowedDuringDrain(PathString path)
    {
        if (path.StartsWithSegments("/api/system/health", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/system/ready", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/api/system/lifecycle", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var value = path.Value?.TrimEnd('/') ?? string.Empty;
        return string.Equals(value, "/api/auth/logout", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "/api/auth/unlock", StringComparison.OrdinalIgnoreCase);
    }
}
