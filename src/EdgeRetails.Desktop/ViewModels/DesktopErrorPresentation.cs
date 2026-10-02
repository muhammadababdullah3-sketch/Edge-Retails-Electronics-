using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Converts typed Desktop Server failures into operator-safe text. Callers supply
/// the operation-specific fallback; raw exception messages are never displayed.
/// </summary>
public static class DesktopErrorPresentation
{
    public static string ForException(Exception exception, string fallbackMessage)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackMessage);

        var code = exception switch
        {
            DesktopApiException apiException => apiException.Code,
            BackendOperationException backendOperationException => backendOperationException.Code,
            OperationException operationException => operationException.Code,
            BackendCatalogOperationException catalogOperationException => catalogOperationException.Code,
            _ => null
        };

        return code is null ? fallbackMessage : ForCode(code, fallbackMessage);
    }

    public static string ForCode(string code, string fallbackMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(fallbackMessage);

        return code switch
        {
            "network.server_unavailable" or "system.not_ready" =>
                "The local Server is unavailable. Check that it is running, then try again.",
            "network.timeout" =>
                "The Server did not respond in time. Check the operation status before retrying.",
            "auth.session_invalid" or "auth.session_missing" or "auth.user_disabled" or
                "auth.role_disabled" =>
                "Your sign-in is no longer valid. Sign in again to continue.",
            "auth.terminal_revoked" or "auth.terminal_suspended" =>
                "This terminal no longer has access. Contact an administrator.",
            "auth.permission_denied" or "authorization.denied" or "authorization.forbidden" =>
                "Your account does not have permission to perform this action.",
            "idempotency.payload_mismatch" =>
                "This operation ID is already tied to different details. Refresh the screen before continuing.",
            "concurrency.stale_product" or "inventory.stocktake_concurrency_conflict" =>
                "The record changed since it was loaded. Refresh it before continuing.",
            "gateway.invalid_response" or "gateway.empty_response" =>
                "The Server returned an incomplete response. Check the operation status before retrying.",
            _ when HasSuffix(code, "unit_not_found") =>
                "The selected physical unit could not be found. Refresh the list and try again.",
            _ when HasSuffix(code, "unit_not_in_stock") =>
                "The selected unit is no longer available in stock. Refresh the list and try again.",
            _ when HasSuffix(code, "wrong_product") =>
                "The selected unit does not belong to this product.",
            _ when HasSuffix(code, "already_sold") =>
                "This unit has already been sold. Refresh the list before continuing.",
            _ when HasSuffix(code, "exact_unit_required") =>
                "Select an available physical unit to continue.",
            _ when HasSuffix(code, "stale_state") ||
                   HasSuffix(code, "conflict") =>
                "The record changed since it was loaded. Refresh it before continuing.",
            _ => fallbackMessage
        };
    }

    private static bool HasSuffix(string code, string suffix) =>
        code.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
}
