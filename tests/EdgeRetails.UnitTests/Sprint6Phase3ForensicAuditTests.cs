namespace EdgeRetails.UnitTests;

public sealed class Sprint6Phase3ForensicAuditTests
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

    [Fact]
    public void Phase3_ShellExposesRealSwitchUserAction()
    {
        var shell = ReadDesktop("ViewModels", "ShellViewModel.cs");
        var topBar = ReadDesktop("Controls", "AppTopBar.xaml");
        var main = ReadDesktop("ViewModels", "MainViewModel.cs");

        Assert.Contains("SwitchUserCommand", shell);
        Assert.Contains("Command=\"{Binding SwitchUserCommand}\"", topBar);
        Assert.Contains("AutomationProperties.Name=\"Switch user\"", topBar);
        Assert.Contains("private void SwitchUser()", main);
        Assert.Contains("_dialogService.Close();", main);
        Assert.Contains("_drawerService.Close();", main);
        Assert.Contains("_navigationService.Reset();", main);
        Assert.Contains("_pageFactory.ClearCachedPages();", main);
        Assert.Contains("ShowLogin();", main);
    }

    [Fact]
    public void Phase3_NavigationAndPageFactorySupportSessionReset()
    {
        var navigation = ReadDesktop("Navigation", "NavigationService.cs");
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");

        Assert.Contains("public void Reset()", navigation);
        Assert.Contains("CurrentTarget = null;", navigation);
        Assert.Contains("CurrentViewModel = null;", navigation);
        Assert.Contains("public void ClearCachedPages()", factory);
        Assert.Contains("ResetCachedPages();", factory);
    }

    [Fact]
    public void Phase3_OverlayServicesDisposeReplacedAndClosedContent()
    {
        foreach (var file in new[] { "DialogService.cs", "DrawerService.cs" })
        {
            var source = ReadDesktop("Services", file);

            Assert.Contains("IDisposable disposable", source);
            Assert.Contains("disposable.Dispose();", source);
            Assert.Contains("Content = null;", source);
        }
    }

    [Fact]
    public void Phase3_SharedReportingAggregatesOperationalModules()
    {
        var reporting = ReadDesktop("Services", "DemoReportingService.cs");

        Assert.Contains("DemoTransactionService.Instance", reporting);
        Assert.Contains("DemoPurchaseInventoryService.Instance", reporting);
        Assert.Contains("DemoBusinessDirectoryService.Instance", reporting);
        Assert.Contains("DemoRetailState.Instance", reporting);
        Assert.Contains("_transactions.TransactionRecorded +=", reporting);
        Assert.Contains("_transactions.ReturnRecorded +=", reporting);
        Assert.Contains("_purchases.StateChanged +=", reporting);
        Assert.Contains("_directory.StateChanged +=", reporting);
        Assert.Contains("_retailState.StateChanged +=", reporting);
    }

    [Fact]
    public void Phase3_CustomerAndSupplierMetricsFollowTransactionsAndPurchases()
    {
        var directory = ReadDesktop("Services", "DemoBusinessDirectoryService.cs");

        Assert.Contains("_transactionService.TransactionRecorded += OnTransactionRecorded;", directory);
        Assert.Contains("_transactionService.ReturnRecorded += OnReturnRecorded;", directory);
        Assert.Contains("_purchaseService.StateChanged += OnPurchaseStateChanged;", directory);
        Assert.Contains("RefreshMetrics()", directory);
    }

    [Fact]
    public void Phase3_StockTruthAuditRemainsAvailableForCrossModuleMutations()
    {
        var inventory = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("AuditStockTruth()", inventory);
        Assert.Contains("SaleOut", inventory);
        Assert.Contains("SaleReturnIn", inventory);
        Assert.Contains("ThakaOut", inventory);
        Assert.Contains("PurchaseIn", inventory);
        Assert.Contains("PurchaseReturnOut", inventory);
        Assert.Contains("AdjustmentIn", inventory);
        Assert.Contains("AdjustmentOut", inventory);
    }

    [Fact]
    public void Phase3_OperationalListScreensUseReusableEmptyState()
    {
        var required = new[]
        {
            "CustomersView.xaml",
            "SuppliersView.xaml",
            "ExpensesView.xaml",
            "PurchaseHistoryView.xaml",
            "InventoryView.xaml"
        };

        foreach (var view in required)
        {
            var xaml = ReadDesktop("Views", view);
            Assert.Contains("<controls:EmptyState", xaml);
        }
    }

    [Fact]
    public void Phase3_NoAccidentalToolPreviewGarbageRemainsInDesktopSources()
    {
        var root = DesktopFile();

        foreach (var file in Directory.EnumerateFiles(
                     root,
                     "*.*",
                     SearchOption.AllDirectories)
                 .Where(path =>
                     path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                     path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain("[Reading ", text);
            Assert.DoesNotContain("[executed on device:", text);
        }
    }

    [Fact]
    public void Phase3_SupportingActionsRemainOverlaysNotPrimaryScreens()
    {
        var app = ReadDesktop("App.xaml");

        Assert.Contains("PermissionRequiredViewModel", app);
        Assert.Contains("ConfirmationDialogViewModel", app);
        Assert.Contains("SaleCustomerPickerViewModel", app);
        Assert.DoesNotContain("NavigationTarget.Permission", ReadDesktop("Navigation", "NavigationTarget.cs"));
        Assert.DoesNotContain("NavigationTarget.Confirmation", ReadDesktop("Navigation", "NavigationTarget.cs"));
    }

    [Fact]
    public void Phase3_DesktopHasExactlyNineteenCanonicalPrimaryViewFiles()
    {
        var expected = new[]
        {
            "FirstSetupView.xaml",
            "LoginView.xaml",
            "DashboardView.xaml",
            "PosView.xaml",
            "SalesHistoryView.xaml",
            "SaleDetailView.xaml",
            "ThakaProjectsView.xaml",
            "ThakaWorkspaceView.xaml",
            "NewPurchaseView.xaml",
            "PurchaseHistoryView.xaml",
            "ProductManagementView.xaml",
            "InventoryView.xaml",
            "ProductDetailView.xaml",
            "ExpensesView.xaml",
            "CustomersView.xaml",
            "SuppliersView.xaml",
            "WarrantyView.xaml",
            "ReportsView.xaml",
            "SettingsView.xaml"
        };

        foreach (var view in expected)
        {
            Assert.True(File.Exists(DesktopFile("Views", view)), view);
        }

        Assert.Equal(19, expected.Length);
    }
}
