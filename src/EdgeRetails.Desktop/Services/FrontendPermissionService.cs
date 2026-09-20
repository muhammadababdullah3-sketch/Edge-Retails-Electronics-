using EdgeRetails.Desktop.Navigation;

namespace EdgeRetails.Desktop.Services;

public interface IFrontendPermissionService
{
    bool CanNavigate(
        string roleName,
        NavigationTarget target,
        out string denialReason);
}

public sealed class DemoFrontendPermissionService : IFrontendPermissionService
{
    public bool CanNavigate(
        string roleName,
        NavigationTarget target,
        out string denialReason)
    {
        if (string.Equals(roleName, "Owner", StringComparison.OrdinalIgnoreCase))
        {
            denialReason = string.Empty;
            return true;
        }

        var allowed = string.Equals(
            roleName,
            "Manager",
            StringComparison.OrdinalIgnoreCase)
                ? ManagerCanNavigate(target)
                : CashierCanNavigate(target);
        denialReason = allowed
            ? string.Empty
            : BuildDenialReason(roleName, target);

        return allowed;
    }

    private static bool ManagerCanNavigate(NavigationTarget target) =>
        target is
            NavigationTarget.Dashboard or
            NavigationTarget.NewSale or
            NavigationTarget.SalesHistory or
            NavigationTarget.Purchases or
            NavigationTarget.Inventory or
            NavigationTarget.Customers or
            NavigationTarget.Suppliers;

    private static bool CashierCanNavigate(NavigationTarget target) =>
        target is
            NavigationTarget.Dashboard or
            NavigationTarget.NewSale or
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

    private static string DisplayName(NavigationTarget target) =>
        target switch
        {
            NavigationTarget.ThakaProjects => "Thaka / Projects",
            NavigationTarget.ThakaWorkspace => "Thaka Workspace",
            NavigationTarget.NewSale => "New Sale",
            NavigationTarget.SalesHistory => "Sales History",
            _ => target.ToString()
        };
}
