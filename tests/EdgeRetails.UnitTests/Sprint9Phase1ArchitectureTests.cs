namespace EdgeRetails.UnitTests;

public sealed class Sprint9Phase1ArchitectureTests
{
    private static string SolutionRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EdgeRetails.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("EdgeRetails.sln was not found.");
    }

    private static string DesktopFile(params string[] parts) =>
        Path.Combine([SolutionRoot(), "src", "EdgeRetails.Desktop", .. parts]);

    private static string ReadDesktop(params string[] parts) =>
        File.ReadAllText(DesktopFile(parts));

    private static readonly (string ViewModel, string View)[] CanonicalScreens =
    [
        ("FirstSetupViewModel", "FirstSetupView.xaml"),
        ("LoginViewModel", "LoginView.xaml"),
        ("DashboardViewModel", "DashboardView.xaml"),
        ("PosViewModel", "PosView.xaml"),
        ("SalesHistoryViewModel", "SalesHistoryView.xaml"),
        ("SaleDetailViewModel", "SaleDetailView.xaml"),
        ("ThakaProjectsViewModel", "ThakaProjectsView.xaml"),
        ("ThakaWorkspaceViewModel", "ThakaWorkspaceView.xaml"),
        ("NewPurchaseViewModel", "NewPurchaseView.xaml"),
        ("PurchaseHistoryViewModel", "PurchaseHistoryView.xaml"),
        ("ProductManagementViewModel", "ProductManagementView.xaml"),
        ("ProductDetailViewModel", "ProductDetailView.xaml"),
        ("InventoryViewModel", "InventoryView.xaml"),
        ("ExpensesViewModel", "ExpensesView.xaml"),
        ("CustomersViewModel", "CustomersView.xaml"),
        ("SuppliersViewModel", "SuppliersView.xaml"),
        ("WarrantyViewModel", "WarrantyView.xaml"),
        ("ReportsViewModel", "ReportsView.xaml"),
        ("SettingsViewModel", "SettingsView.xaml")
    ];

    [Fact]
    public void CanonicalFullScreenContract_IsExactlyNineteenAndComposed()
    {
        Assert.Equal(19, CanonicalScreens.Length);
        var app = ReadDesktop("App.xaml");

        foreach (var (viewModel, view) in CanonicalScreens)
        {
            Assert.True(File.Exists(DesktopFile("Views", view)), view);
            Assert.Contains($"viewModels:{viewModel}", app);
            Assert.Contains($"<views:{Path.GetFileNameWithoutExtension(view)} />", app);
        }
    }

    [Fact]
    public void NavigationAuthority_UsesPosProductManagementAndWarranty()
    {
        var targets = ReadDesktop("Navigation", "NavigationTarget.cs");
        var shell = ReadDesktop("ViewModels", "ShellViewModel.cs");
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");

        Assert.Contains("POS,", targets);
        Assert.Contains("ProductManagement,", targets);
        Assert.Contains("Warranty,", targets);
        Assert.DoesNotContain("NewSale", targets);
        Assert.Contains("NavigationTarget.POS => new PosViewModel(", factory);
        Assert.Contains("NavigationTarget.ProductManagement => new ProductManagementViewModel(", factory);
        Assert.Contains("_productManagementService", factory);
        Assert.Contains("NavigationTarget.Warranty => new WarrantyViewModel(", factory);
        Assert.Contains("CreateNavigationItem(\"POS\", NavigationTarget.POS", shell);
        Assert.Contains("CreateNavigationItem(\"Product Management\", NavigationTarget.ProductManagement", shell);
        Assert.Contains("CreateNavigationItem(\"Warranty\", NavigationTarget.Warranty", shell);
        Assert.DoesNotContain("\"New Sale\"", shell);
    }

    [Fact]
    public void ProductManagementAndInventory_AreSeparateTargets()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");
        Assert.Contains("NavigationTarget.ProductManagement =>", factory);
        Assert.Contains("NavigationTarget.Inventory =>", factory);
        Assert.NotEqual(
            factory.IndexOf("NavigationTarget.ProductManagement =>", StringComparison.Ordinal),
            factory.IndexOf("NavigationTarget.Inventory =>", StringComparison.Ordinal));
    }

    [Fact]
    public void PermissionSurface_IsExplicitAndFailClosed()
    {
        var permissions = ReadDesktop("Services", "FrontendPermissionService.cs");
        Assert.Contains("NavigationTarget.POS => PermissionKeys.SalesPosUse", permissions);
        Assert.Contains("NavigationTarget.ProductManagement => PermissionKeys.InventoryManage", permissions);
        Assert.Contains("NavigationTarget.Warranty => PermissionKeys.WarrantyView", permissions);
        Assert.Contains("_ => null", permissions);
    }
}
