using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class PlaceholderPageViewModelFactory : IPageViewModelFactory
{
    private static readonly IReadOnlyDictionary<NavigationTarget, string> Titles =
        new Dictionary<NavigationTarget, string>
        {
            [NavigationTarget.Dashboard] = "Dashboard",
            [NavigationTarget.NewSale] = "New Sale",
            [NavigationTarget.SalesHistory] = "Sales History",
            [NavigationTarget.ThakaProjects] = "Thaka / Projects",
            [NavigationTarget.Purchases] = "Purchases",
            [NavigationTarget.Inventory] = "Inventory",
            [NavigationTarget.Expenses] = "Expenses",
            [NavigationTarget.Customers] = "Customers",
            [NavigationTarget.Suppliers] = "Suppliers",
            [NavigationTarget.Reports] = "Reports",
            [NavigationTarget.Settings] = "Settings",
        };

    public ViewModelBase Create(NavigationTarget target)
    {
        var title = Titles.TryGetValue(target, out var value) ? value : target.ToString();
        return new PlaceholderPageViewModel(target, title);
    }
}
