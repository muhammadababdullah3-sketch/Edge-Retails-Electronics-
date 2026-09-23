using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Desktop.Navigation;

namespace EdgeRetails.Desktop.Services;

public interface IFrontendPermissionService
{
    bool CanNavigate(
        ISessionContext sessionContext,
        NavigationTarget target,
        out string denialReason);

    bool CanNavigate(
        string roleName,
        NavigationTarget target,
        out string denialReason);
}

public sealed class DemoFrontendPermissionService : IFrontendPermissionService
{
    public bool CanNavigate(
        ISessionContext sessionContext,
        NavigationTarget target,
        out string denialReason)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);

        if (sessionContext.UserId is null)
        {
            return CanNavigate(
                sessionContext.RoleName,
                target,
                out denialReason);
        }

        var requiredPermission = PermissionFor(target);
        var allowed = requiredPermission is not null &&
            sessionContext.PermissionKeys.Contains(requiredPermission);

        denialReason = allowed
            ? string.Empty
            : BuildDenialReason(sessionContext.RoleName, target);
        return allowed;
    }

    public bool CanNavigate(
        string roleName,
        NavigationTarget target,
        out string denialReason)
    {
        var normalizedRole = roleName?.Trim() ?? string.Empty;

        if (string.Equals(
                normalizedRole,
                "Owner",
                StringComparison.OrdinalIgnoreCase))
        {
            denialReason = string.Empty;
            return true;
        }

        var allowed = string.Equals(
                normalizedRole,
                "Manager",
                StringComparison.OrdinalIgnoreCase)
            ? ManagerCanNavigate(target)
            : string.Equals(
                normalizedRole,
                "Cashier",
                StringComparison.OrdinalIgnoreCase) &&
              CashierCanNavigate(target);

        denialReason = allowed
            ? string.Empty
            : BuildDenialReason(normalizedRole, target);

        return allowed;
    }

    private static bool ManagerCanNavigate(NavigationTarget target) =>
        target is
            NavigationTarget.Dashboard or
            NavigationTarget.POS or
            NavigationTarget.SalesHistory or
            NavigationTarget.Purchases or
            NavigationTarget.ProductManagement or
            NavigationTarget.Inventory or
            NavigationTarget.Customers or
            NavigationTarget.Suppliers or
            NavigationTarget.Warranty;

    private static bool CashierCanNavigate(NavigationTarget target) =>
        target is
            NavigationTarget.Dashboard or
            NavigationTarget.POS or
            NavigationTarget.SalesHistory;

    private static string BuildDenialReason(
        string roleName,
        NavigationTarget target)
    {
        var role = string.IsNullOrWhiteSpace(roleName)
            ? "Current user"
            : roleName;

        return $"{role} does not have permission to open {DisplayName(target)}.";
    }

    private static string? PermissionFor(NavigationTarget target) =>
        target switch
        {
            NavigationTarget.Dashboard => PermissionKeys.DashboardView,
            NavigationTarget.POS => PermissionKeys.SalesPosUse,
            NavigationTarget.SalesHistory => PermissionKeys.SalesView,
            NavigationTarget.ThakaProjects => PermissionKeys.ThakaManage,
            NavigationTarget.ThakaWorkspace => PermissionKeys.ThakaManage,
            NavigationTarget.Purchases => PermissionKeys.PurchasingManage,
            NavigationTarget.ProductManagement => PermissionKeys.InventoryManage,
            NavigationTarget.Inventory => PermissionKeys.InventoryManage,
            NavigationTarget.Expenses => PermissionKeys.ExpensesManage,
            NavigationTarget.Customers => PermissionKeys.CustomersManage,
            NavigationTarget.Suppliers => PermissionKeys.SuppliersManage,
            NavigationTarget.Warranty => PermissionKeys.WarrantyView,
            NavigationTarget.Reports => PermissionKeys.ReportsView,
            NavigationTarget.Settings => PermissionKeys.SettingsManage,
#if DEBUG
            NavigationTarget.Sprint1Verification => PermissionKeys.SettingsManage,
#endif
            _ => null
        };

    private static string DisplayName(NavigationTarget target) =>
        target switch
        {
            NavigationTarget.ThakaProjects => "Thaka / Projects",
            NavigationTarget.ThakaWorkspace => "Thaka Workspace",
            NavigationTarget.POS => "POS",
            NavigationTarget.ProductManagement => "Product Management",
            NavigationTarget.SalesHistory => "Sales History",
            _ => target.ToString()
        };
}
