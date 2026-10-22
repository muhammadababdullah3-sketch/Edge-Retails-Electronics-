using System.Collections.Concurrent;
using System.Net;

namespace EdgeRetails.Central.Api.Middleware;

public class RateLimitingMiddleware
{
    private static readonly ConcurrentDictionary<string, (int Count, DateTimeOffset ResetAt)> RequestCounts = new();
    private const int MaxAttemptsPerWindow = 5;
    private static readonly TimeSpan WindowDuration = TimeSpan.FromMinutes(1);

    public static void Reset() => RequestCounts.Clear();

    private readonly RequestDelegate _next;

    public RateLimitingMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value?.ToLowerInvariant() ?? string.Empty;
        var isThrottledPath = path.Contains("/api/admin/v1/auth/login") || path.Contains("/api/admin/v1/auth/mfa") || path.Contains("/api/admin/v1/auth/step-up") || path.Contains("/api/licensing/v1/bootstrap");

        if (isThrottledPath && context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
        {
            var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown_client";
            var key = $"{ip}:{path}";
            var now = DateTimeOffset.UtcNow;

            var (count, resetAt) = RequestCounts.AddOrUpdate(
                key,
                _ => (1, now.Add(WindowDuration)),
                (_, existing) =>
                {
                    if (now > existing.ResetAt)
                    {
                        return (1, now.Add(WindowDuration));
                    }
                    return (existing.Count + 1, existing.ResetAt);
                });

            if (count > MaxAttemptsPerWindow)
            {
                context.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"code\":\"RATE_LIMITED\",\"safeMessageKey\":\"security.rate_limited\",\"retryable\":true}");
                return;
            }
        }

        await _next(context);
    }
}
