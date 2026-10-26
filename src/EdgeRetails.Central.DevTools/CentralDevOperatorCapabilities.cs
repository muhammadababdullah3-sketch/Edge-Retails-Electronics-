namespace EdgeRetails.Central.DevTools;

/// <summary>
/// Capability identifiers aligned with <c>CentralRequestAuthorizationFilter.ResolveRequiredPermission</c> for local portal workflows.
/// </summary>
internal static class CentralDevOperatorCapabilities
{
    public static readonly string[] PortalOperator =
    [
        "dashboard.view",
        "queues.view",
        "sessions.view",
        "admin.manage",
        "admin.users.read",
        "activation.review",
        "activation.issue",
        "license.view",
        "license.issue",
        "license.policy",
        "customer.view",
        "customer.create",
        "shop.view",
        "shop.register",
        "payment.review",
        "order.create",
        "search.masked",
        "notifications.view",
        "audit.read",
        "sensitive.approve",
        "security.manage",
        "evidence.upload.init",
        "evidence.upload.finalize",
        "evidence.download",
        "backup.download",
        "support.view",
        "support.create",
        "support.reply",
        "hardware.review",
        "operation.admin.lookup",
        "auth.stepup"
    ];
}
