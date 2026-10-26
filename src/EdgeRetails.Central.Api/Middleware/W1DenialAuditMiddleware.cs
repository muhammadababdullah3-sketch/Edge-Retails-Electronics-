using System.Text.Json;
using EdgeRetails.Central.Application.Abstractions;
using EdgeRetails.Central.Api.Security;
using EdgeRetails.Central.Domain.Common;
namespace EdgeRetails.Central.Api.Middleware;

public sealed class W1DenialAuditMiddleware(RequestDelegate next)
{
    public const string DenialCodeItemKey = "CentralDenialCode";

    public static void SetDenialCode(HttpContext context, string code) => context.Items[DenialCodeItemKey] = code;

    public async Task InvokeAsync(HttpContext context)
    {
        var isCustomerRestoreShim = HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Equals("/api/licensing/v1/backups/rehearse", StringComparison.OrdinalIgnoreCase);
        var isAuditableApiRequest = context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/internal");
        var shouldAudit = !isCustomerRestoreShim && isAuditableApiRequest;

        try
        {
            await next(context);
        }
        catch (CentralDomainException ex)
        {
            if (shouldAudit)
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
        if (shouldAudit && context.Response.StatusCode >= 400)
        {
            var services = context.RequestServices;
            var code = context.Items.TryGetValue(DenialCodeItemKey, out var value) && value is string denialCode
                ? denialCode
                : $"HTTP_{context.Response.StatusCode}";
            await RecordAsync(context,
                services.GetRequiredService<ICentralAuditService>(),
                services.GetRequiredService<IAdminAuthService>(),
                code);
        }
    }

    private static async Task RecordAsync(HttpContext context, ICentralAuditService audit, IAdminAuthService auth, string code)
    {
        var credentials = CentralSessionCredentials.Parse(context.Request);
        var session = credentials.IsValid
            ? await auth.ValidateSessionAsync(credentials.SessionId, context.RequestAborted)
            : null;
        Guid? operation = Guid.TryParse(context.Request.Headers["X-Client-Operation-Id"], out var op) ? op : null;
        var details = JsonSerializer.Serialize(new { code });
        await audit.RecordAuditAsync("security.request.denied", session is null ? "anonymous" : $"admin:{session.SessionId}",
            session?.AdminUserId, session?.SessionId, null, operation, operation,
            "ApiRoute", context.Request.Path.ToString(), details, isSuccess: false, cancellationToken: context.RequestAborted);
    }
}
