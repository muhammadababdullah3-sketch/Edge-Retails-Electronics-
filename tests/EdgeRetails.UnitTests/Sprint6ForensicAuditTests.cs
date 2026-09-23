namespace EdgeRetails.UnitTests;

public sealed class Sprint6ForensicAuditTests
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
    public void Phase1_UnknownRolesFailClosed()
    {
        var source = ReadDesktop(
            "Services",
            "FrontendPermissionService.cs");

        Assert.Contains("normalizedRole", source);
        Assert.Contains("\"Cashier\"", source);
        Assert.Contains("&&", source);
        Assert.DoesNotContain(
            "? ManagerCanNavigate(target)\n                : CashierCanNavigate(target)",
            source.Replace("\r", string.Empty));
    }

    [Fact]
    public void Phase1_NavigationServiceIsAuthoritativePermissionBoundary()
    {
        var source = ReadDesktop(
            "Navigation",
            "NavigationService.cs");

        var permissionIndex = source.IndexOf(
            "_permissionService.CanNavigate",
            StringComparison.Ordinal);
        var createIndex = source.IndexOf(
            "_pageFactory.Create(target)",
            StringComparison.Ordinal);

        Assert.True(permissionIndex >= 0);
        Assert.True(createIndex > permissionIndex);
        Assert.Contains("PermissionRequiredViewModel", source);
        Assert.Contains("return false;", source);
    }

    [Fact]
    public void Phase1_LoginUpdatesAuthoritativeNavigationSession()
    {
        var source = ReadDesktop(
            "ViewModels",
            "MainViewModel.cs");

        Assert.Contains(
            "_navigationService.SetSessionContext(session);",
            source);
        Assert.Contains(
            "_permissionService",
            source);
    }

    [Fact]
    public void Phase1_SetupPinIsClearedWhenLeavingShopStep()
    {
        var vm = ReadDesktop(
            "ViewModels",
            "FirstSetupViewModel.cs");
        var view = ReadDesktop(
            "Views",
            "FirstSetupView.xaml.cs");
        var xaml = ReadDesktop(
            "Views",
            "FirstSetupView.xaml");

        Assert.Contains("ClearOwnerPin();", vm);
        Assert.Contains("OwnerPinClearRequested", vm);
        Assert.Contains("Step == FirstSetupStep.ShopSetup", vm);
        Assert.Contains("OwnerPinInput.Clear();", view);
        Assert.Contains("x:Name=\"OwnerPinInput\"", xaml);
    }

    [Fact]
    public void Phase1_LoginClearsTransientPinBeforeSuccessCallbacks()
    {
        var source = ReadDesktop(
            "ViewModels",
            "LoginViewModel.cs");

        var clearIndex = source.IndexOf(
            "ClearPin();",
            source.IndexOf(
                "if (_identityService.VerifyPin",
                StringComparison.Ordinal),
            StringComparison.Ordinal);
        var callbackIndex = source.IndexOf(
            "OnLoginSuccess?.Invoke(session);",
            StringComparison.Ordinal);

        Assert.True(clearIndex >= 0);
        Assert.True(callbackIndex > clearIndex);
    }

    [Fact]
    public void Phase1_IdentityUsesPbkdf2AndFixedTimeComparison()
    {
        var source = ReadDesktop(
            "Services",
            "DemoIdentityService.cs");

        Assert.Contains("Rfc2898DeriveBytes.Pbkdf2", source);
        Assert.Contains("CryptographicOperations.FixedTimeEquals", source);
        Assert.Contains("RandomNumberGenerator.GetBytes", source);
        Assert.DoesNotContain("candidatePin ==", source);
    }

    [Fact]
    public void Phase1_RepeatedSubscriberPagesHaveOwnedLifetime()
    {
        var factory = ReadDesktop(
            "Navigation",
            "PageViewModelFactory.cs");
        var inventory = ReadDesktop(
            "ViewModels",
            "InventoryViewModel.cs");
        var purchase = ReadDesktop(
            "ViewModels",
            "PurchaseHistoryViewModel.cs");
        var detail = ReadDesktop(
            "ViewModels",
            "ProductDetailViewModel.cs");

        Assert.Contains("_inventoryViewModel ??=", factory);
        Assert.Contains("_purchaseHistoryViewModel ??=", factory);
        Assert.Contains("public void Dispose()", factory);
        Assert.Contains("ViewModelBase, IDisposable", inventory);
        Assert.Contains("ViewModelBase, IDisposable", purchase);
        Assert.Contains("ViewModelBase, IDisposable", detail);
        Assert.Contains(
            "_inventoryService.StateChanged -= OnStateChanged;",
            detail);
        Assert.Contains(
            "_transactionService.TransactionRecorded -= OnTransactionRecorded;",
            detail);
        Assert.Contains(
            "CurrentProductDetail?.Dispose();",
            inventory);
    }

    [Fact]
    public void Phase1_AllSharedStatePageSubscribersUnsubscribe()
    {
        var files = new[]
        {
            "CustomersViewModel.cs",
            "ExpensesViewModel.cs",
            "InventoryViewModel.cs",
            "ProductDetailViewModel.cs",
            "PurchaseHistoryViewModel.cs",
            "ReportsViewModel.cs",
            "SettingsViewModel.cs",
            "SuppliersViewModel.cs",
            "ThakaProjectsViewModel.cs"
        };

        foreach (var file in files)
        {
            var source = ReadDesktop("ViewModels", file);
            Assert.Contains("IDisposable", source);
            Assert.Contains("-=", source);
        }
    }

    [Fact]
    public void Phase1_SetupPreviewBypassIsDebugOnly()
    {
        var source = ReadDesktop("App.xaml.cs");

        var debugIndex = source.IndexOf("#if DEBUG", StringComparison.Ordinal);
        var setupIndex = source.IndexOf(
            "\"--setup-preview\"",
            StringComparison.Ordinal);
        var releaseIndex = source.IndexOf("#else", StringComparison.Ordinal);

        Assert.True(debugIndex >= 0);
        Assert.True(setupIndex > debugIndex);
        Assert.True(releaseIndex > setupIndex);
        Assert.Contains("forceSetupPreview = false;", source);
    }

    [Fact]
    public void Phase1_RequiredGlobalStateControlsExist()
    {
        foreach (var file in new[]
        {
            "EmptyState.xaml",
            "LoadingState.xaml",
            "InlineError.xaml"
        })
        {
            Assert.True(
                File.Exists(DesktopFile("Controls", file)),
                $"{file} is missing.");
        }

        var app = ReadDesktop("App.xaml");
        Assert.Contains("PermissionRequiredViewModel", app);
        Assert.Contains("ConfirmationDialogViewModel", app);
    }

    [Fact]
    public void Phase1_DesktopHasNoDirectBackendOrWebRuntimeCoupling()
    {
        var desktopRoot = Path.Combine(
            SolutionRoot(),
            "src",
            "EdgeRetails.Desktop");

        var sourceFiles = Directory.EnumerateFiles(
            desktopRoot,
            "*.*",
            SearchOption.AllDirectories)
            .Where(path =>
                path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var combined = string.Join(
            "\n",
            sourceFiles.Select(File.ReadAllText));

        Assert.DoesNotContain("Npgsql", combined);
        Assert.DoesNotContain("DbContext", combined);
        Assert.DoesNotContain("Dapper", combined);
        Assert.DoesNotContain("ConnectionString", combined);
        Assert.DoesNotContain("<Canvas", combined);
        Assert.DoesNotContain("className=", combined);
        Assert.DoesNotContain("React.", combined);
    }
}
