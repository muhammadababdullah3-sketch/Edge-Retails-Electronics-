using System.Text.Json;
using EdgeRetails.Central.Api.Security;

namespace EdgeRetails.Central.Api.Middleware;

/// <summary>
/// Portal login performs a session probe before any admin cookie exists. That probe must not
/// force Central database resolution just to return SESSION_REQUIRED.
/// </summary>
public sealed class AnonymousPortalSessionGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (HttpMethods.IsGet(context.Request.Method) &&
            path.Equals("/api/admin/v1/portal/session", StringComparison.OrdinalIgnoreCase))
        {
            var cred = CentralSessionCredentials.Parse(context.Request);
            if (!cred.IsValid)
            {
                CsrfProtectionMiddleware.SetNoStoreHeaders(context.Response);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(JsonSerializer.Serialize(new
                {
                    code = "SESSION_REQUIRED",
                    safeMessageKey = "auth.session_required",
                    message = "An authenticated admin session is required."
                }), context.RequestAborted);
                return;
            }
        }

        await next(context);
    }
}
