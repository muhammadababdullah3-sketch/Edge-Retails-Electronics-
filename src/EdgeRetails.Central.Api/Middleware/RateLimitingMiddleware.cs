using System.Collections.Concurrent;
using System.Globalization;
using System.Net;

namespace EdgeRetails.Central.Api.Middleware;

public class RateLimitingMiddleware
{
    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset ResetAt)> RequestCounts = new();

    public static void Reset() => RequestCounts.Clear();

    private readonly RequestDelegate _next;

    public RateLimitingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var isThrottledPath = string.Equals(path, "/api/admin/v1/auth/login", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/admin/v1/auth/mfa", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/admin/v1/auth/step-up", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/licensing/v1/bootstrap/challenge", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/licensing/v1/primary/register", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(path, "/api/licensing/v1/activation/redeem", StringComparison.OrdinalIgnoreCase);

        if (isThrottledPath && context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryReadPolicy(out var maxAttempts, out var windowDuration))
            {
                W1DenialAuditMiddleware.SetDenialCode(context, "RATE_LIMIT_CONFIGURATION_REQUIRED");
                CsrfProtectionMiddleware.SetNoStoreHeaders(context.Response);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"code\":\"RATE_LIMIT_CONFIGURATION_REQUIRED\",\"safeMessageKey\":\"security.rate_limit_configuration_required\"}", context.RequestAborted);
                return;
            }

            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown_client";
            var key = $"{ip}:{path.ToLowerInvariant()}";
            var now = DateTimeOffset.UtcNow;

            var (count, resetAt) = RequestCounts.AddOrUpdate(
                key,
                _ => (1, now.Add(windowDuration)),
                (_, existing) =>
                {
                    if (now >= existing.ResetAt)
                    {
                        return (1, now.Add(windowDuration));
                    }
                    return (existing.Count + 1, existing.ResetAt);
                });

            if (count > maxAttempts)
            {
                W1DenialAuditMiddleware.SetDenialCode(context, "RATE_LIMITED");
                CsrfProtectionMiddleware.SetNoStoreHeaders(context.Response);
                context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"code\":\"RATE_LIMITED\",\"safeMessageKey\":\"security.rate_limited\",\"retryable\":true}");
                return;
            }
        }

        await _next(context);
    }

    private static bool TryReadPolicy(out int maxAttempts, out TimeSpan windowDuration)
    {
        maxAttempts = 0;
        windowDuration = TimeSpan.Zero;
        var attemptsValue = Environment.GetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_MAX_ATTEMPTS");
        var windowSecondsValue = Environment.GetEnvironmentVariable("CENTRAL_AUTH_RATE_LIMIT_WINDOW_SECONDS");
        if (!int.TryParse(attemptsValue, NumberStyles.None, CultureInfo.InvariantCulture, out maxAttempts) || maxAttempts < 1 ||
            !int.TryParse(windowSecondsValue, NumberStyles.None, CultureInfo.InvariantCulture, out var windowSeconds) || windowSeconds < 1)
        {
            maxAttempts = 0;
            return false;
        }

        windowDuration = TimeSpan.FromSeconds(windowSeconds);
        return true;
    }
}
