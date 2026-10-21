using System.Net;

namespace EdgeRetails.Desktop.Services;

/// <summary>
/// Authoritative deterministic rejection classifier for Desktop operation intents.
/// Only proven no-commit terminal business rejections release durable intent in IClientOperationIntentStore.
/// Retryable, concurrency, auth, timeout, transport, gateway, and unknown errors must retain intent for idempotent replay.
/// </summary>
public static class DesktopOperationRejectionClassifier
{
    public static bool IsAuthoritativeDeterministicBusinessRejection(Exception? ex) =>
        ex is DesktopApiException apiEx && IsAuthoritativeDeterministicBusinessRejection(apiEx);

    public static bool IsAuthoritativeDeterministicBusinessRejection(DesktopApiException ex)
    {
        if (string.IsNullOrWhiteSpace(ex.Code))
        {
            return false;
        }

        // Transport, network, gateway, or generic HTTP synthesized status codes are never positive business rejections.
        if (ex.Code.StartsWith("network.", StringComparison.OrdinalIgnoreCase) ||
            ex.Code.StartsWith("gateway.", StringComparison.OrdinalIgnoreCase) ||
            ex.Code.StartsWith("http.", StringComparison.OrdinalIgnoreCase) ||
            ex.Code.StartsWith("system.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Retryable or ambiguous HTTP status codes must preserve intent for replay or reconciliation.
        if (ex.StatusCode is null or
            HttpStatusCode.Unauthorized or
            HttpStatusCode.Forbidden or
            HttpStatusCode.Conflict or
            HttpStatusCode.RequestTimeout or
            (HttpStatusCode)429 or
            >= HttpStatusCode.InternalServerError)
        {
            return false;
        }

        // Concurrency or idempotency collision errors represent conflicting operations or active executions.
        // Idempotency mismatches (payload, type, context) are conflicting existing operation evidence,
        // never known-no-commit merely because returned as HTTP 400. Intent must be preserved for reconciliation.
        if (ex.Code.StartsWith("idempotency.", StringComparison.OrdinalIgnoreCase) ||
            ex.Code.Equals("concurrency.conflict", StringComparison.OrdinalIgnoreCase) ||
            ex.Code.Equals("database.deadlock", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Positive evidence: Must be an authoritative business domain error code indicating terminal no-commit business rejection
        return ex.Code.StartsWith("thaka.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("validation.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("cash.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("inventory.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("catalog.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("sales.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("parties.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("finance.", StringComparison.OrdinalIgnoreCase) ||
               ex.Code.StartsWith("supplier.", StringComparison.OrdinalIgnoreCase);
    }
}
