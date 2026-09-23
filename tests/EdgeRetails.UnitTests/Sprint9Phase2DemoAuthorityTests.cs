namespace EdgeRetails.UnitTests;

public sealed class Sprint9Phase2DemoAuthorityTests
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
    public void Dashboard_UsesBackendAuthority_AndHasNoDemoKpiLoader()
    {
        var vm = ReadDesktop("ViewModels", "DashboardViewModel.cs");
        var service = ReadDesktop("Services", "BackendDashboardService.cs");
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");

        Assert.Contains("IBackendDashboardService", vm);
        Assert.Contains("_backendService.LoadAsync()", vm);
        Assert.Contains("new BackendDashboardService(", factory);
        Assert.Contains("_dashboardService)", factory);
        Assert.DoesNotContain("LoadDemoData", vm);
        Assert.DoesNotContain("84,500", vm);
        Assert.DoesNotContain("13,400", vm);
        Assert.DoesNotContain("3,200", vm);
        Assert.DoesNotContain("Database Connected = true", vm);
        Assert.Contains("Unavailable", vm);
        Assert.Contains("IDatabaseReadinessService", service);
    }

    [Fact]
    public void Settings_ProductionComposition_UsesBackendSettingsService()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");
        var service = ReadDesktop("Services", "BackendSettingsService.cs");

        Assert.Contains("new BackendSettingsService(", factory);
        Assert.Contains("() => _sessionContext.UserId", factory);
        Assert.Contains("_settingsService)", factory);
        Assert.Contains("IBackendSettingsService", vm);
        Assert.Contains("_backendService.LoadAsync()", vm);
        Assert.Contains("_backendService.SaveShopAsync(", vm);
        Assert.Contains("_backendService.SaveReceiptTemplateAsync(", vm);
        Assert.Contains("GetLoginAccountsHandler", service);
        Assert.Contains("GetSettingsConfigurationHandler", service);
        Assert.Contains("UpdateShopProfileHandler", service);
        Assert.Contains("UpdateReceiptTemplateHandler", service);
        Assert.Contains("IDatabaseReadinessService", service);
        Assert.Contains("IProductionMaintenanceBarrier", service);
    }

    [Fact]
    public void Settings_UsesAuthoritativeWrites_AndDeferredOperationsCannotFakeSuccess()
    {
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");
        var service = ReadDesktop("Services", "BackendSettingsService.cs");
        var handlers = File.ReadAllText(Path.Combine(
            SolutionRoot(),
            "src",
            "EdgeRetails.Application",
            "Features",
            "Settings",
            "SettingsConfigurationHandlers.cs"));

        Assert.Contains("_backendService.SaveShopAsync(", vm);
        Assert.Contains("_backendService.SaveReceiptTemplateAsync(", vm);
        Assert.Contains("PermissionKeys.SettingsManage", handlers);
        Assert.Contains("GetShopProfileForUpdateAsync", handlers);
        Assert.Contains("GetReceiptTemplateSettingsForUpdateAsync", handlers);
        Assert.Contains("SHOP_PROFILE_UPDATED", handlers);
        Assert.Contains("RECEIPT_TEMPLATE_UPDATED", handlers);
        Assert.Contains("SaveShopAsync(", service);
        Assert.Contains("SaveReceiptTemplateAsync(", service);
        Assert.Contains("No backup was started.", vm);
        Assert.Contains("No restore was prepared or executed.", vm);
        Assert.Contains("No license state was changed.", vm);
        Assert.Contains("#if DEBUG", vm);
        Assert.Contains("DemoSettingsState.Instance", vm);
    }

    [Fact]
    public void Thaka_ProductionComposition_UsesBackendService_AndSerializedFlowUsesExactUnitAuthority()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");
        var backend = ReadDesktop("Services", "BackendThakaService.cs");
        var projects = ReadDesktop("ViewModels", "ThakaProjectsViewModel.cs");
        var workspace = ReadDesktop("ViewModels", "ThakaWorkspaceViewModel.cs");
        var addMaterial = ReadDesktop("ViewModels", "ThakaAddMaterialViewModel.cs");

        Assert.Contains("new BackendThakaService(", factory);
        Assert.Contains("_backendThakaService)", factory);
        Assert.Contains("_backendService.GetProjectsPageAsync(", projects);
        Assert.Contains("ProjectPageSize", projects);
        Assert.Contains("_backendService.IssueMaterialAsync(", addMaterial);
        Assert.Contains("_backendService", workspace);
        Assert.Contains("IReadOnlyList<Guid> inventoryUnitIds", backend);
        Assert.Contains("inventoryUnitIds", backend);
        Assert.Contains("SelectExactUnitsCommand", addMaterial);
        Assert.Contains("_selectedExactUnits.Select(x => x.InventoryUnitId)", addMaterial);
        Assert.Contains("return backendService is null ? DemoRetailState.Instance : null;", projects);
        Assert.Contains("#if DEBUG", projects);
        Assert.Contains("#if DEBUG", workspace);
        Assert.Contains("#if DEBUG", addMaterial);
    }

    [Fact]
    public void Pos_DoesNotOwnProductionThakaMutation()
    {
        var pos = ReadDesktop("ViewModels", "PosViewModel.cs");

        Assert.DoesNotContain("IssueMaterialAsync(", pos);
        Assert.DoesNotContain("IssueMaterialBatch(", pos);
        Assert.DoesNotContain("SwitchToThaka", pos);
        Assert.DoesNotContain("_invoiceCounter = 1293", pos);
        Assert.DoesNotContain("PreloadDemoCartItems", pos);
        Assert.DoesNotContain("[\"All\", \"Lighting\"", pos);
        Assert.DoesNotContain("[\"All\", \"Philips\"", pos);
        Assert.Contains("rows.Select(row => row.Category)", pos);
        Assert.Contains("Invoice assigned on completion", pos);
        Assert.Contains("Thaka material issuance belongs exclusively to Thaka Workspace.", pos);
        Assert.Contains("#if DEBUG", pos);
    }

    [Fact]
    public void SalesReturn_DemoStockProjection_IsDebugPreviewOnly()
    {
        var source = ReadDesktop("ViewModels", "SalesReturnViewModel.cs");

        Assert.Contains("ResolveTransactionService", source);
        Assert.Contains("Production sale returns require an authoritative transaction service.", source);
        Assert.Contains("#if DEBUG", source);
        Assert.Contains("_previewRetailState?.ApplySaleReturnStock(record);", source);
    }

    [Fact]
    public void InventoryAndProductDetail_DoNotInstantiateDemoProviders_WhenBackendExists()
    {
        var inventory = ReadDesktop("ViewModels", "InventoryViewModel.cs");
        var detail = ReadDesktop("ViewModels", "ProductDetailViewModel.cs");

        Assert.DoesNotContain("_retailState = DemoRetailState.Instance;", inventory);
        Assert.DoesNotContain("_inventoryService = DemoPurchaseInventoryService.Instance;", inventory);
        Assert.Contains("ResolvePreviewRetailState(backendService)", inventory);
        Assert.Contains("ResolvePreviewInventoryService(backendService)", inventory);

        Assert.DoesNotContain("_retailState = DemoRetailState.Instance;", detail);
        Assert.DoesNotContain("_inventoryService = DemoPurchaseInventoryService.Instance;", detail);
        Assert.DoesNotContain("_transactionService = DemoTransactionService.Instance;", detail);
        Assert.Contains("ResolvePreviewRetailState(backendService)", detail);
        Assert.Contains("ResolvePreviewInventoryService(backendService)", detail);
        Assert.Contains("ResolvePreviewTransactionService(backendService)", detail);
    }

    [Fact]
    public void NewPurchase_DemoProviders_ArePreviewOnly()
    {
        var source = ReadDesktop("ViewModels", "NewPurchaseViewModel.cs");

        Assert.DoesNotContain("_service = DemoPurchaseInventoryService.Instance;", source);
        Assert.Contains("ResolvePreviewService(backendService)", source);
        Assert.Contains("#if DEBUG", source);
        Assert.Contains("Production purchase creation requires an authoritative backend service.", source);
    }

    [Fact]
    public void FirstSetup_DoesNotWriteDemoSettings_AfterBackendBootstrap()
    {
        var source = ReadDesktop("ViewModels", "FirstSetupViewModel.cs");

        Assert.Contains("ResolvePreviewSettingsState", source);
        Assert.Contains("backendSetupService is null", source);
        Assert.Contains("#if DEBUG", source);
        Assert.Contains("_previewSettingsState.SaveShop(", source);
        Assert.DoesNotContain("_settingsState.SaveShop(", source);
    }

    [Fact]
    public void Phase1CanonicalNavigation_RemainsIntact()
    {
        var targets = ReadDesktop("Navigation", "NavigationTarget.cs");

        Assert.Contains("POS,", targets);
        Assert.Contains("ProductManagement,", targets);
        Assert.Contains("Warranty,", targets);
        Assert.DoesNotContain("NewSale", targets);
    }
}
