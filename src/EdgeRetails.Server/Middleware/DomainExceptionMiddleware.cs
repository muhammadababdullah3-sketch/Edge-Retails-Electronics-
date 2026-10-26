using System.Text.Json;
using EdgeRetails.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace EdgeRetails.Server.Middleware;

public sealed class DomainExceptionMiddleware(RequestDelegate next, ILogger<DomainExceptionMiddleware> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (BusinessRuleException ex)
        {
            var correlationId = ResolveCorrelationId(context);
            logger.LogWarning(
                ex,
                "Business rule violation {Code} (correlation {CorrelationId})",
                ex.Code,
                correlationId);

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { code = ex.Code, message = ex.Message }, JsonOptions));
        }
        catch (DbUpdateConcurrencyException ex)
        {
            var correlationId = ResolveCorrelationId(context);
            logger.LogWarning(
                ex,
                "Concurrency conflict (correlation {CorrelationId})",
                correlationId);

            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(
                    new
                    {
                        code = "concurrency_conflict",
                        message = "The record changed while the operation was in progress. Refresh and try again."
                    },
                    JsonOptions));
        }
    }

    private static string? ResolveCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Correlation-Id", out var values) &&
            !string.IsNullOrWhiteSpace(values.FirstOrDefault()))
        {
            return values.First();
        }

        return context.TraceIdentifier;
    }
}
