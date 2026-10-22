using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Domain.Common;
namespace EdgeRetails.Central.Api.Middleware;

public sealed class W1DenialAuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var isCustomerRestoreShim = HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Equals("/api/licensing/v1/backups/rehearse", StringComparison.OrdinalIgnoreCase);

        try
        {
            await next(context);
        }
        catch (CentralDomainException ex)
        {
            if (!isCustomerRestoreShim)
            {
                var services = context.RequestServices;
                await RecordAsync(context,
                    services.GetRequiredService<ICentralAuditService>(),
                    services.GetRequiredService<IAdminAuthService>(),
                    ex.Code);
            }
            throw;
        }

        // This legacy restore shim is deliberately a no-op with no database dependency.
        if (!isCustomerRestoreShim && context.Response.StatusCode >= 400)
        {
            var services = context.RequestServices;
            await RecordAsync(context,
                services.GetRequiredService<ICentralAuditService>(),
                services.GetRequiredService<IAdminAuthService>(),
                $"HTTP_{context.Response.StatusCode}");
        }
    }

    private static async Task RecordAsync(HttpContext context, ICentralAuditService audit, IAdminAuthService auth, string code)
    {
        var credentials = CentralSessionCredentials.Parse(context.Request);
        var session = credentials.IsValid
            ? await auth.ValidateSessionAsync(credentials.SessionId, context.RequestAborted)
            : null;
        Guid? operation = Guid.TryParse(context.Request.Headers["X-Client-Operation-Id"], out var op) ? op : null;
        await audit.RecordAuditAsync("security.request.denied", session is null ? "anonymous" : $"admin:{session.SessionId}",
            session?.AdminUserId, session?.SessionId, code, operation, operation,
            "ApiRoute", context.Request.Path.ToString(), "{}", isSuccess: false, cancellationToken: context.RequestAborted);
    }
}
