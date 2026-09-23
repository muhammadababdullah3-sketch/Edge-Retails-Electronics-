using EdgeRetails.Application.Features.Terminals;

namespace EdgeRetails.Server.Middleware;

public sealed class ProtocolCompatibilityMiddleware
{
    private readonly RequestDelegate _next;

    public ProtocolCompatibilityMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (context.Request.Headers.TryGetValue("X-Protocol-Version", out var versionValues))
            {
                var version = versionValues.ToString();
                if (!TerminalProtocol.IsCompatible(version))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsJsonAsync(new
                    {
                        code = "protocol.incompatible",
                        message = $"Client protocol version '{version}' is incompatible. Server requires '{TerminalProtocol.CurrentProtocolVersion}'."
                    });
                    return;
                }
            }
        }

        await _next(context);
    }
}
