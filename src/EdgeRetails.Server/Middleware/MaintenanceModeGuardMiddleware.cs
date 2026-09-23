namespace EdgeRetails.Server.Middleware;

public sealed class MaintenanceModeGuardMiddleware
{
    private readonly RequestDelegate _next;

    public MaintenanceModeGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        bool isMaintenance = string.Equals(
            Environment.GetEnvironmentVariable("EDGE_RETAILS_MAINTENANCE_MODE"),
            "1",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
            Environment.GetEnvironmentVariable("EDGE_RETAILS_MAINTENANCE_MODE"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        if (isMaintenance && HttpMethods.IsPost(context.Request.Method))
        {
            // Allow health and status checks, block mutations
            if (!context.Request.Path.StartsWithSegments("/api/system/health"))
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "system.maintenance_mode",
                    message = "Server is currently in maintenance or restore mode. Authoritative mutations are blocked."
                });
                return;
            }
        }

        await _next(context);
    }
}
