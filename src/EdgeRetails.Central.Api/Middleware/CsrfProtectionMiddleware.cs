using System.Net;
using EdgeRetails.Central.Api.Security;

namespace EdgeRetails.Central.Api.Middleware;

public class CsrfProtectionMiddleware
{
    private readonly RequestDelegate _next;

    public CsrfProtectionMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Enforce HTTPS
        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"code\":\"HTTPS_REQUIRED\",\"safeMessageKey\":\"security.https_required\",\"message\":\"HTTPS is strictly required for Central authority endpoints.\"}");
            return;
        }

        var path = context.Request.Path;
        var method = context.Request.Method.ToUpperInvariant();

        // Customer restore is edge-owned. Return the explicit unsupported contract without
        // resolving MVC filters, Central services, or a database target.
        if (method == "POST" && path.Equals("/api/licensing/v1/backups/rehearse", StringComparison.OrdinalIgnoreCase))
        {
            SetNoStoreHeaders(context.Response);
            context.Response.StatusCode = StatusCodes.Status501NotImplemented;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"code\":\"CUSTOMER_RESTORE_NOT_SUPPORTED\",\"safeMessageKey\":\"restore.edge_verifier_required\"}",
                context.RequestAborted);
            return;
        }
        var isMutating = method is "POST" or "PUT" or "DELETE" or "PATCH";

        // Enforce no-store cache control on all API, internal, and auth routes
        if (path.StartsWithSegments("/api") || path.StartsWithSegments("/internal"))
        {
            SetNoStoreHeaders(context.Response);
            try
            {
                context.Response.OnStarting(() =>
                {
                    SetNoStoreHeaders(context.Response);
                    return Task.CompletedTask;
                });
            }
            catch
            {
                // Suppress if headers have already started
            }
        }

        // Prohibit cross-origin browser access to internal endpoints
        if (path.StartsWithSegments("/internal"))
        {
            if (context.Request.Headers.ContainsKey("Origin"))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"code\":\"CROSS_ORIGIN_PROHIBITED\",\"safeMessageKey\":\"signer.cross_origin_prohibited\",\"message\":\"Cross-origin requests to internal endpoints are strictly prohibited.\"}");
                return;
            }

            await _next(context);
            return;
        }

        // Check for session credential conflicts across all transports
        var credResult = CentralSessionCredentials.Parse(context.Request);
        if (credResult.ErrorCode == "SESSION_CREDENTIAL_CONFLICT")
        {
            context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"code\":\"SESSION_CREDENTIAL_CONFLICT\",\"safeMessageKey\":\"security.session_credential_conflict\",\"message\":\"Conflicting session credentials provided across transports.\"}");
            return;
        }

        // Enforce CSRF protection for mutating requests bearing session credentials across any transport
        if (isMutating && credResult.IsPresent)
        {
            var expectedHost = context.Request.Host.Host;
            var expectedPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
            var origin = context.Request.Headers["Origin"].ToString();
            var referer = context.Request.Headers["Referer"].ToString();

            var isValidOrigin = false;
            if (!string.IsNullOrWhiteSpace(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var originUri))
            {
                if (string.Equals(originUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(originUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) &&
                    originUri.Port == expectedPort)
                {
                    isValidOrigin = true;
                }
            }
            else if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                if (string.Equals(refererUri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(refererUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) &&
                    refererUri.Port == expectedPort)
                {
                    isValidOrigin = true;
                }
            }

            if (!isValidOrigin)
            {
                context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"code\":\"CSRF_REJECTED\",\"safeMessageKey\":\"security.csrf_rejected\",\"message\":\"Exact scheme, host, and port origin match required.\"}");
                return;
            }
        }

        await _next(context);
    }

    private static void SetNoStoreHeaders(HttpResponse response)
    {
        response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
    }
}
