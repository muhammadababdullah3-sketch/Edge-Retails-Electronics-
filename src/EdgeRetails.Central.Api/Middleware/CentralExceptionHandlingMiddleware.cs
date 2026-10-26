using System.Net;
using System.Text.Json;
using EdgeRetails.Central.Application.DTOs;
using EdgeRetails.Central.Domain.Common;

namespace EdgeRetails.Central.Api.Middleware;

public class CentralExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CentralExceptionHandlingMiddleware> _logger;

    public CentralExceptionHandlingMiddleware(RequestDelegate next, ILogger<CentralExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestId = Guid.NewGuid();
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CentralRequestId"] = requestId
        });

        try
        {
            await _next(context);
        }
        catch (CentralDomainException ex)
        {
            if (context.Response.HasStarted)
            {
                _logger.LogError("Central domain request {RequestId} failed after the response started.", requestId);
                context.Abort();
                return;
            }
            _logger.LogWarning("Central domain request {RequestId} failed with code {Code}.", requestId, ex.Code);
            await HandleDomainExceptionAsync(context, ex, requestId);
        }
        catch (Exception ex)
        {
            if (context.Response.HasStarted)
            {
                _logger.LogError("Unhandled server exception after the response started ({FailureType}) for request {RequestId}.", ex.GetType().Name, requestId);
                context.Abort();
                return;
            }
            _logger.LogError("Unhandled server exception occurred ({FailureType}) for request {RequestId}.", ex.GetType().Name, requestId);
            await HandleGenericExceptionAsync(context, ex, requestId);
        }
    }

    private static async Task HandleDomainExceptionAsync(HttpContext context, CentralDomainException ex, Guid requestId)
    {
        var statusCode = ex.Code switch
        {
            "INVALID_CREDENTIALS" or "INVALID_MFA_PROOF" or "INVALID_STEPUP_PROOF" => HttpStatusCode.Unauthorized,
            "SESSION_INVALID" or "INVALID_CHALLENGE" or "USER_INACTIVE" or "CALLER_UNAUTHORIZED" or "INTERNAL_AUTH_REQUIRED" => HttpStatusCode.Unauthorized,
            "SECURITY_POLICY_REQUIRED" or "INDEPENDENT_APPROVAL_UNAVAILABLE" or "DELEGATION_FORBIDDEN" or "FORBIDDEN" or "UNAUTHORIZED_CAPABILITY" or "SELF_ESCALATION_PROHIBITED" or "APPROVAL_REQUIRED" or "INVALID_STEPUP_GRANT" or "STEP_UP_REQUIRED" or "CROSS_ORIGIN_PROHIBITED" => HttpStatusCode.Forbidden,
            "APPROVAL_POLICY_NOT_CONFIGURED" => HttpStatusCode.ServiceUnavailable,
            "KEY_NOT_FOUND" or "NOT_FOUND" or "SUPPORT_ATTACHMENT_NOT_FOUND" or "OBJECT_NOT_FOUND" or "EVIDENCE_NOT_FOUND" or "DOWNLOAD_GRANT_NOT_FOUND" => HttpStatusCode.NotFound,
            "OPERATION_TYPE_CONFLICT" or "OPERATION_ID_CONFLICT" or "CONCURRENCY_CONFLICT" or "SIGNING_REQUEST_CONFLICT" or "UPLOAD_CHUNK_CONFLICT" or "UPLOAD_OFFSET_CONFLICT" or "STORAGE_OBJECT_CONFLICT" => HttpStatusCode.Conflict,
            _ => HttpStatusCode.BadRequest
        };

        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "application/json";

        var errorDto = new ErrorDto
        {
            Code = ex.Code,
            RequestId = requestId,
            Retryable = statusCode == HttpStatusCode.Conflict || statusCode == HttpStatusCode.ServiceUnavailable,
            SafeMessageKey = ex.SafeMessageKey
        };

        var json = JsonSerializer.Serialize(errorDto);
        await context.Response.WriteAsync(json);
    }

    private static async Task HandleGenericExceptionAsync(HttpContext context, Exception ex, Guid requestId)
    {
        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
        context.Response.ContentType = "application/json";

        var errorDto = new ErrorDto
        {
            Code = "INTERNAL_SERVER_ERROR",
            RequestId = requestId,
            Retryable = true,
            SafeMessageKey = "error.internal"
        };

        var json = JsonSerializer.Serialize(errorDto);
        await context.Response.WriteAsync(json);
    }
}
