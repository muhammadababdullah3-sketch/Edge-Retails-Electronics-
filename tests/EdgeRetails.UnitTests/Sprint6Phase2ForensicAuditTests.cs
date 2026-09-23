namespace EdgeRetails.UnitTests;

public sealed class Sprint6Phase2ForensicAuditTests
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

    private static readonly string[] PrimaryViews =
    [
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
    ];

    [Fact]
    public void Phase2_MainWindowLocksCanonicalDesktopMinimum()
    {
        var xaml = ReadDesktop("MainWindow.xaml");

        Assert.Contains("Width=\"1440\"", xaml);
        Assert.Contains("Height=\"900\"", xaml);
        Assert.Contains("MinWidth=\"1366\"", xaml);
        Assert.Contains("MinHeight=\"768\"", xaml);
    }

    [Fact]
    public void Phase2_AllNineteenPrimaryScreensHaveContainedScrolling()
    {
        foreach (var view in PrimaryViews)
        {
            var xaml = ReadDesktop("Views", view);

            Assert.Contains(
                "<ScrollViewer",
                xaml,
                StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Phase2_PrimaryScreensDoNotForceDesktopWidthPastMinimumViewport()
    {
        var widthPattern = new System.Text.RegularExpressions.Regex(
            @"(?<!Min)(?<!Max)Width=""(?<value>\d+(?:\.\d+)?)""",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        foreach (var view in PrimaryViews)
        {
            var xaml = ReadDesktop("Views", view);
            var widths = widthPattern.Matches(xaml)
                .Select(match => decimal.Parse(
                    match.Groups["value"].Value,
                    System.Globalization.CultureInfo.InvariantCulture));

            Assert.DoesNotContain(widths, width => width >= 1366m);
        }
    }

    [Fact]
    public void Phase2_ModalHostProvidesViewportFallbackScrolling()
    {
        var xaml = ReadDesktop("Controls", "ModalHost.xaml");

        Assert.Contains("<ScrollViewer", xaml);
        Assert.Contains("HorizontalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml);
        Assert.Contains("Margin=\"24\"", xaml);
    }

    [Fact]
    public void Phase2_ShellUsesAdaptiveSidebarAndStarContentColumn()
    {
        var shell = ReadDesktop("Views", "ShellView.xaml");
        var sidebar = ReadDesktop("Controls", "AppSidebar.xaml");

        Assert.Contains("<ColumnDefinition Width=\"Auto\" />", shell);
        Assert.Contains("<ColumnDefinition Width=\"*\" />", shell);
        Assert.Contains("Width=\"{Binding SidebarWidth}\"", sidebar);
    }

    [Fact]
    public void Phase2_NoViewHardCodesLightThemeColorKeys()
    {
        var viewRoot = DesktopFile("Views");

        foreach (var file in Directory.EnumerateFiles(
                     viewRoot,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(file);
            Assert.DoesNotContain("Color.Light.", xaml);
        }
    }

    [Fact]
    public void Phase2_SystemThemeFollowsWindowsPreferenceChanges()
    {
        var source = ReadDesktop("Services", "ThemeService.cs");
        var app = ReadDesktop("App.xaml.cs");

        Assert.Contains("SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;", source);
        Assert.Contains("SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;", source);
        Assert.Contains("CurrentTheme != AppTheme.System", source);
        Assert.Contains("ApplyTheme(AppTheme.System);", source);
        Assert.Contains("EdgeRetails.Desktop;component/Resources/Themes/", source);
        Assert.Contains("IDisposable", source);
        Assert.Contains("_themeService?.Dispose();", app);
    }

    [Fact]
    public void Phase2_PosUsesSemanticThemeResourcesForModeSurfaces()
    {
        var xaml = ReadDesktop("Views", "PosView.xaml");

        Assert.DoesNotContain("Brush.Cart.BorderNormal", xaml);
        Assert.DoesNotContain("Brush.Cart.BorderThaka", xaml);
        Assert.DoesNotContain("Brush.Thaka.Purple", xaml);
        Assert.Contains("Brush.Badge.Brand.Background", xaml);
        Assert.Contains("Brush.Badge.Brand.Border", xaml);
        Assert.Contains("Brush.Disabled.Text", xaml);
    }

    [Fact]
    public void Phase2_LoginAndDashboardUseThemeAwareSurfaceResources()
    {
        var login = ReadDesktop("Views", "LoginView.xaml");
        var dashboard = ReadDesktop("Views", "DashboardView.xaml");

        Assert.Contains("Gradient.Login.Background", login);
        Assert.Contains("Brush.Text.Primary", login);
        Assert.Contains("Brush.Surface.Default", login);
        Assert.Contains("Gradient.Surface.Card", dashboard);
        Assert.Contains("Brush.Badge.Warning", dashboard);
        Assert.Contains("Brush.Border.Soft", dashboard);
    }
}
