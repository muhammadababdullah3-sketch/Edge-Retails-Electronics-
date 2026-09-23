namespace EdgeRetails.UnitTests;

public sealed class Sprint5ForensicAuditTests
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
    public void Sprint5Phase1_NavigationReplacesThreePlaceholders()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");

        Assert.Contains("NavigationTarget.Expenses => _expensesViewModel ??= new ExpensesViewModel", factory);
        Assert.Contains("NavigationTarget.Customers => _customersViewModel ??= new CustomersViewModel", factory);
        Assert.Contains("NavigationTarget.Suppliers => _suppliersViewModel ??= new SuppliersViewModel", factory);
    }

    [Fact]
    public void Sprint5Phase1_PageViewModelsAreCachedToAvoidDuplicateSingletonSubscriptions()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");

        Assert.Contains("private ExpensesViewModel? _expensesViewModel", factory);
        Assert.Contains("private CustomersViewModel? _customersViewModel", factory);
        Assert.Contains("private SuppliersViewModel? _suppliersViewModel", factory);
        Assert.Contains("??= new ExpensesViewModel", factory);
        Assert.Contains("??= new CustomersViewModel", factory);
        Assert.Contains("??= new SuppliersViewModel", factory);
    }

    [Fact]
    public void Sprint5Phase1_DataTemplatesRegisterScreensDrawersAndDialogs()
    {
        var app = ReadDesktop("App.xaml");

        var expected = new[]
        {
            "ExpensesViewModel", "CustomersViewModel", "SuppliersViewModel",
            "CustomerDetailViewModel", "SupplierDetailViewModel",
            "ExpenseEditViewModel", "CustomerEditViewModel", "SupplierEditViewModel"
        };

        Assert.All(expected, type => Assert.Contains(type, app));
    }

    [Fact]
    public void Expenses_AreSeparateFromPurchases_AndExposeRequiredFields()
    {
        var service = ReadDesktop("Services", "DemoBusinessDirectoryService.cs");
        var vm = ReadDesktop("ViewModels", "ExpensesViewModel.cs");
        var dialog = ReadDesktop("Views", "Dialogs", "ExpenseEditDialog.xaml");

        Assert.Contains("ObservableCollection<ExpenseRecord> Expenses", service);
        Assert.DoesNotContain("Purchases.Add", vm);
        Assert.Contains("SelectedCategory", dialog);
        Assert.Contains("Subcategory", dialog);
        Assert.Contains("AmountText", dialog);
        Assert.Contains("SelectedPaymentMethod", dialog);
        Assert.Contains("StaffMember", dialog);
        Assert.Contains("Note", dialog);
    }

    [Fact]
    public void Expenses_SupportPeriodCategoryFiltersAndKpis()
    {
        var vm = ReadDesktop("ViewModels", "ExpensesViewModel.cs");
        var view = ReadDesktop("Views", "ExpensesView.xaml");

        Assert.Contains("\"Today\" =>", vm);
        Assert.Contains("\"ThisWeek\" =>", vm);
        Assert.Contains("\"ThisMonth\" =>", vm);
        Assert.Contains("SelectedCategory", vm);
        Assert.Contains("TodayAmountDisplay", view);
        Assert.Contains("ThisMonthAmountDisplay", view);
        Assert.Contains("TopCategory", view);
    }

    [Fact]
    public void Customers_DeriveMetricsFromSalesAndThakaFrontendState()
    {
        var service = ReadDesktop("Services", "DemoBusinessDirectoryService.cs");
        var view = ReadDesktop("Views", "CustomersView.xaml");

        Assert.Contains("_transactionService.GetAllTransactions()", service);
        Assert.Contains("_retailState.ThakaProjects", service);
        Assert.Contains("customer.LocalSales =", service);
        Assert.Contains("customer.ActiveThaka =", service);
        Assert.Contains("LocalSalesDisplay", view);
        Assert.Contains("ActiveThaka", view);
        Assert.Contains("LastSaleDisplay", view);
    }

    [Fact]
    public void Suppliers_DeriveMetricsFromPurchaseHistory_AndFeedPurchaseSupplierChoices()
    {
        var directory = ReadDesktop("Services", "DemoBusinessDirectoryService.cs");
        var purchases = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");
        var view = ReadDesktop("Views", "SuppliersView.xaml");

        Assert.Contains("_purchaseService.Purchases", directory);
        Assert.Contains("_purchaseService.RegisterSupplier(normalizedName)", directory);
        Assert.Contains("public void RegisterSupplier", purchases);
        Assert.Contains("TotalPurchasesDisplay", view);
        Assert.Contains("LastPurchaseDisplay", view);
    }

    [Fact]
    public void CustomerAndSupplierDetailsUseDrawers_AndEditingUsesDialogHost()
    {
        var customers = ReadDesktop("ViewModels", "CustomersViewModel.cs");
        var suppliers = ReadDesktop("ViewModels", "SuppliersViewModel.cs");

        Assert.Contains("_drawerService.Show(new CustomerDetailViewModel", customers);
        Assert.Contains("_dialogService.Show(new CustomerEditViewModel", customers);
        Assert.Contains("_drawerService.Show(new SupplierDetailViewModel", suppliers);
        Assert.Contains("_dialogService.Show(new SupplierEditViewModel", suppliers);
    }

    [Fact]
    public void Sprint5Phase1_UsesNoCanvasOrWebFrontendResidue()
    {
        var views = new[]
        {
            ReadDesktop("Views", "ExpensesView.xaml"),
            ReadDesktop("Views", "CustomersView.xaml"),
            ReadDesktop("Views", "SuppliersView.xaml"),
            ReadDesktop("Views", "CustomerDetailView.xaml"),
            ReadDesktop("Views", "SupplierDetailView.xaml"),
            ReadDesktop("Views", "Dialogs", "ExpenseEditDialog.xaml"),
            ReadDesktop("Views", "Dialogs", "CustomerEditDialog.xaml"),
            ReadDesktop("Views", "Dialogs", "SupplierEditDialog.xaml")
        };

        Assert.All(views, xaml => Assert.DoesNotContain("<Canvas", xaml));
        Assert.All(views, xaml => Assert.DoesNotContain("className=", xaml));
    }

    [Fact]
    public void Sprint5Phase1_DesktopStateRemainsBackendAgnostic()
    {
        var service = ReadDesktop("Services", "DemoBusinessDirectoryService.cs");

        Assert.DoesNotContain("DbContext", service);
        Assert.DoesNotContain("Npgsql", service);
        Assert.DoesNotContain("HttpClient", service);
        Assert.DoesNotContain("ApiClient", service);
    }

    [Fact]
    public void Sprint5MasterPlan_LocksThreePhaseBoundary()
    {
        var plan = File.ReadAllText(Path.Combine(SolutionRoot(), "docs", "Sprint5_Master_Implementation_Plan.md"));

        Assert.Contains("Phase 1 — Expenses + Customers + Suppliers", plan);
        Assert.Contains("Phase 2 — Reports", plan);
        Assert.Contains("Phase 3 — Settings", plan);
        Assert.Contains("Backend attachment:** DEFERRED", plan);
    }

    [Fact]
    public void Sprint5Phase2_ReportsNavigationIsRealAndCached()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");

        Assert.Contains("private ReportsViewModel? _reportsViewModel", factory);
        Assert.Contains(
            "NavigationTarget.Reports => _reportsViewModel ??= new ReportsViewModel(",
            factory);
        Assert.Contains("_businessOperationsService", factory);
    }

    [Fact]
    public void Sprint5Phase2_ReportsViewIsRegistered()
    {
        var app = ReadDesktop("App.xaml");

        Assert.Contains("ReportsViewModel", app);
        Assert.Contains("<views:ReportsView />", app);
    }

    [Fact]
    public void Sprint5Phase2_ReportingReadsExistingOperationalLedgers()
    {
        var service = ReadDesktop("Services", "DemoReportingService.cs");

        Assert.Contains("DemoTransactionService.Instance", service);
        Assert.Contains("DemoPurchaseInventoryService.Instance", service);
        Assert.Contains("DemoBusinessDirectoryService.Instance", service);
        Assert.Contains("DemoRetailState.Instance", service);
        Assert.Contains("_transactions.GetAllTransactions()", service);
        Assert.Contains("_transactions.GetAllReturns()", service);
        Assert.Contains("_purchases.Purchases", service);
        Assert.Contains("_directory.Expenses", service);
        Assert.Contains("_retailState.GetMaterialLedger(project)", service);
    }

    [Fact]
    public void Sprint5Phase2_SalesCaptureHistoricalCostSnapshots()
    {
        var contract = ReadDesktop("Services", "ITransactionService.cs");
        var pos = ReadDesktop("ViewModels", "PosViewModel.cs");
        var demo = ReadDesktop("Services", "DemoTransactionService.cs");

        Assert.Contains("UnitCostSnapshot", contract);
        Assert.Contains("TotalCostSnapshot", contract);
        Assert.Contains("GrossProfitSnapshot", contract);
        Assert.Contains("UnitCostSnapshot = item.Product.Cost", pos);
        Assert.Contains("UnitCostSnapshot = unitCost", demo);
    }

    [Fact]
    public void Sprint5Phase2_NetSalesAndReturnProfitRulesAreExplicit()
    {
        var service = ReadDesktop("Services", "DemoReportingService.cs");

        Assert.Contains("var netSales = grossSales - refunds", service);
        Assert.Contains("SaleReturnDisposition.RestockSellable", service);
        Assert.Contains("CalculateRestockedReturnCost", service);
        Assert.Contains("var netCogs = cogs - reversedCogs", service);
        Assert.Contains("netSales - netCogs", service);
    }

    [Fact]
    public void Sprint5Phase2_ChartsZeroFillDailyMonthlyAndYearlyPeriods()
    {
        var service = ReadDesktop("Services", "DemoReportingService.cs");

        Assert.Contains("for (var hour = 0; hour < 24; hour++)", service);
        Assert.Contains("DateTime.DaysInMonth(year, month)", service);
        Assert.Contains("for (var day = 1; day <= days; day++)", service);
        Assert.Contains("for (var month = 1; month <= 12; month++)", service);
    }

    [Fact]
    public void Sprint5Phase2_AveragesFollowCalendarPeriodRules()
    {
        var service = ReadDesktop("Services", "DemoReportingService.cs");

        Assert.Contains("DateTime.DaysInMonth(selectedYear, selectedMonth)", service);
        Assert.Contains("DateTime.IsLeapYear(selectedYear) ? 366 : 365", service);
        Assert.Contains("DateTime.Today.Month", service);
        Assert.Contains("netSales / calendarDays", service);
        Assert.Contains("expenseTotal / calendarDays", service);
    }

    [Fact]
    public void Sprint5Phase2_MonthlyIncludesExpenseBreakdownAndSeparateThakaActivity()
    {
        var service = ReadDesktop("Services", "DemoReportingService.cs");
        var view = ReadDesktop("Views", "ReportsView.xaml");

        Assert.Contains("BuildExpenseBreakdown", service);
        Assert.Contains("BuildThakaActivity", service);
        Assert.Contains("Expense Breakdown", view);
        Assert.Contains("Thaka Activity", view);
        Assert.Contains("Material issues remain separate from local sales", view);
    }

    [Fact]
    public void Sprint5Phase2_ReportsHaveAnalyticalGraphWithoutCanvas()
    {
        var view = ReadDesktop("Views", "ReportsView.xaml");
        var chart = ReadDesktop("Controls", "FinancialTrendChart.cs");
        var dashboard = ReadDesktop("Views", "DashboardView.xaml");

        Assert.Contains("FinancialTrendChart", view);
        Assert.Contains("DrawSeries", chart);
        Assert.Contains("Hourly Sales & Gross Profit", ReadDesktop("ViewModels", "ReportsViewModel.cs"));
        Assert.DoesNotContain("<Canvas", view);
        Assert.DoesNotContain("FinancialTrendChart", dashboard);
        Assert.DoesNotContain("className=", view);
    }

    [Fact]
    public void Sprint5Phase2_PurchaseCostStateUsesLandedMovingWeightedAverage()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("allocatedOtherCost", service);
        Assert.Contains("EffectiveUnitCost", service);
        Assert.Contains("movingWeightedAverage", service);
        Assert.Contains("line.Product.Cost = movingWeightedAverage", service);
        Assert.DoesNotContain("line.Product.Cost = Math.Round(line.Cost", service);
    }

    [Fact]
    public void Sprint5Phase2_ReportingFrontendHasNoDirectBackendTransportOrDatabaseCoupling()
    {
        var service = ReadDesktop("Services", "DemoReportingService.cs");
        var vm = ReadDesktop("ViewModels", "ReportsViewModel.cs");

        foreach (var source in new[] { service, vm })
        {
            Assert.DoesNotContain("DbContext", source);
            Assert.DoesNotContain("Npgsql", source);
            Assert.DoesNotContain("Dapper", source);
            Assert.DoesNotContain("HttpClient", source);
            Assert.DoesNotContain("ApiClient", source);
        }
    }

    [Fact]
    public void Sprint5Phase2_MasterPlanMatchesLockedReportingArchitecture()
    {
        var plan = File.ReadAllText(Path.Combine(
            SolutionRoot(),
            "docs",
            "Sprint5_Master_Implementation_Plan.md"));

        Assert.Contains("Moving Weighted Average", plan);
        Assert.Contains("Daily graph: Hourly Sales + Hourly Gross Profit", plan);
        Assert.Contains("Monthly graph: Daily Sales + Daily Net Profit + Daily Expenses", plan);
        Assert.Contains("Yearly graph: Jan-Dec Sales + Net Profit + Expenses", plan);
        Assert.Contains("Missing chart periods are zero-filled", plan);
    }

    [Fact]
    public void Sprint5Phase3_SettingsNavigationIsRealAndCached()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");
        var app = ReadDesktop("App.xaml");

        Assert.Contains("private SettingsViewModel? _settingsViewModel", factory);
        Assert.Contains("NavigationTarget.Settings => _settingsViewModel ??= new SettingsViewModel", factory);
        Assert.Contains("SettingsViewModel", app);
        Assert.Contains("<views:SettingsView />", app);
    }

    [Fact]
    public void Sprint5Phase3_ContainsAllEightInternalSettingsSections()
    {
        var view = ReadDesktop("Views", "SettingsView.xaml");

        foreach (var label in new[]
        {
            "Shop", "Receipt", "Users &amp; Access", "Categories &amp; Units",
            "Backup", "License", "Appearance", "Database"
        })
        {
            Assert.Contains(label, view);
        }

        Assert.Contains("Shop Settings", view);
        Assert.Contains("Receipt Settings", view);
        Assert.Contains("Permission Matrix", view);
        Assert.Contains("Backup &amp; Restore", view);
        Assert.Contains("PostgreSQL Diagnostics", view);
    }

    [Fact]
    public void Sprint5Phase3_UserManagementProtectsLastActiveOwner()
    {
        var state = ReadDesktop("Services", "DemoSettingsState.cs");

        Assert.Contains("At least one active Owner must remain.", state);
        Assert.Contains("Users.Count(user => user.IsActive && user.Role == \"Owner\") <= 1", state);
        Assert.Contains("Another user already uses this name.", state);
    }

    [Fact]
    public void Sprint5Phase3_CategoryAndUnitManagementDeactivatesInsteadOfDeletes()
    {
        var state = ReadDesktop("Services", "DemoSettingsState.cs");
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");

        Assert.Contains("SetCategoryActive", state);
        Assert.Contains("SetUnitActive", state);
        Assert.Contains("Deactivate Category", vm);
        Assert.Contains("Deactivate Unit", vm);
        Assert.DoesNotContain("RemoveCategory", state);
        Assert.DoesNotContain("RemoveUnit", state);
        Assert.DoesNotContain("Categories.Remove", state);
        Assert.DoesNotContain("Units.Remove", state);
    }

    [Fact]
    public void Sprint5Phase3_PermissionMatrixMatchesCanonicalRoles()
    {
        var state = ReadDesktop("Services", "DemoSettingsState.cs");

        Assert.Contains("Create Sale", state);
        Assert.Contains("View Profit", state);
        Assert.Contains("Create Purchase", state);
        Assert.Contains("Stock Adjustment", state);
        Assert.Contains("Manage Users", state);
        Assert.Contains("Manage Settings", state);
        Assert.Contains("Backup / Restore", state);
        Assert.Contains("Manager = \"Optional\"", state);
        Assert.Contains("Cashier = \"No\"", state);
    }

    [Fact]
    public void Sprint5Phase3_AppearanceUsesExistingThemeService()
    {
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");

        Assert.Contains("IThemeService", vm);
        Assert.Contains("_themeService.ApplyTheme(SelectedTheme)", vm);
        Assert.Contains("AppTheme.Light, AppTheme.Dark, AppTheme.System", vm);
    }

    [Fact]
    public void Sprint5Phase3_BackupLicenseDatabaseRemainFrontendShells()
    {
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");
        var view = ReadDesktop("Views", "SettingsView.xaml");

        Assert.Contains("Worker integration is deferred until backend attachment.", vm);
        Assert.Contains("Licensing backend is not attached yet.", vm);
        Assert.Contains("Backend restore is not attached yet.", vm);
        Assert.Contains("PostgreSQL host, port, passwords and connection strings are intentionally not exposed.", view);

        foreach (var source in new[] { vm, ReadDesktop("Services", "DemoSettingsState.cs") })
        {
            Assert.DoesNotContain("Npgsql", source);
            Assert.DoesNotContain("DbContext", source);
            Assert.DoesNotContain("ConnectionString", source);
        }
    }

    [Fact]
    public void Sprint5Phase3_SettingsDialogsAreRegisteredAndSupportingOnly()
    {
        var app = ReadDesktop("App.xaml");

        Assert.Contains("SettingsEditorViewModel", app);
        Assert.Contains("<dialogs:SettingsEditorDialog />", app);
        Assert.Contains("SettingsConfirmViewModel", app);
        Assert.Contains("<dialogs:SettingsConfirmDialog />", app);
    }

    [Fact]
    public void Sprint5Phase3_PinIsTransientAndNotStoredOnUserRecord()
    {
        var state = ReadDesktop("Services", "DemoSettingsState.cs");
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");
        var dialog = ReadDesktop("Views", "Dialogs", "SettingsEditorDialog.xaml");

        Assert.DoesNotContain("public string Pin", state);
        Assert.Contains("NewPin = string.Empty", vm);
        Assert.Contains("is not persisted as plaintext", dialog);
    }

    [Fact]
    public void Sprint5Phase3_UsesNoCanvasOrWebFrontendResidue()
    {
        var sources = new[]
        {
            ReadDesktop("Views", "SettingsView.xaml"),
            ReadDesktop("Views", "Dialogs", "SettingsEditorDialog.xaml"),
            ReadDesktop("Views", "Dialogs", "SettingsConfirmDialog.xaml")
        };

        Assert.All(sources, source => Assert.DoesNotContain("<Canvas", source));
        Assert.All(sources, source => Assert.DoesNotContain("className=", source));
        Assert.All(sources, source => Assert.DoesNotContain("<script", source));
    }

    [Fact]
    public void Sprint5Forensic_SettingsDeferredSystemsNeverClaimLiveBackendHealth()
    {
        var state = ReadDesktop("Services", "DemoSettingsState.cs");
        var view = ReadDesktop("Views", "SettingsView.xaml");

        Assert.Contains("Integration Pending", state);
        Assert.Contains("Not Connected · Frontend Shell", state);
        Assert.Contains("Status = \"Sample\"", state);
        Assert.Contains("Integration Pending", view);
        Assert.Contains("Preview Data", view);
        Assert.DoesNotContain("<TextBlock Text=\"Connected\" FontWeight=\"SemiBold\"/>", view);
        Assert.DoesNotContain("<TextBlock Text=\"Active\" FontWeight=\"SemiBold\"/>", view);
    }

    [Fact]
    public void Sprint5Forensic_SettingsPinUsesMaskedPasswordBoxAndClearsTransientValue()
    {
        var view = ReadDesktop("Views", "Dialogs", "SettingsEditorDialog.xaml");
        var codeBehind = ReadDesktop("Views", "Dialogs", "SettingsEditorDialog.xaml.cs");
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");

        Assert.Contains("<PasswordBox", view);
        Assert.Contains("PasswordChanged=\"OnPinPasswordChanged\"", view);
        Assert.DoesNotContain("Text=\"{Binding NewPin", view);
        Assert.Contains("passwordBox.Password", codeBehind);
        Assert.Contains("NewPin = string.Empty", vm);
    }

    [Fact]
    public void Sprint5Forensic_LicenseImportUsesFilePicker()
    {
        var vm = ReadDesktop("ViewModels", "SettingsViewModel.cs");
        var view = ReadDesktop("Views", "Dialogs", "SettingsEditorDialog.xaml");

        Assert.Contains("BrowseLicenseFileCommand", vm);
        Assert.Contains("Select Edge Retails License", vm);
        Assert.Contains("*.lic;*.key", vm);
        Assert.Contains("File.Exists(FilePath)", vm);
        Assert.Contains("Content=\"Browse\"", view);
        Assert.Contains("IsReadOnly=\"True\"", view);
    }

    [Fact]
    public void Sprint5Forensic_CategoryAndUnitRenameMigrateProductReferences()
    {
        var state = ReadDesktop("Services", "DemoSettingsState.cs");

        Assert.Contains("product.Category = normalized", state);
        Assert.Contains("product.Unit = normalizedName", state);
        Assert.Contains("oldSymbol + \"s\"", state);
        Assert.Contains("DemoRetailState.Instance.NotifyProductChanged()", state);
    }

    [Fact]
    public void Sprint5Forensic_SettingsAvoidInvalidAutoThicknessLayout()
    {
        var view = ReadDesktop("Views", "SettingsView.xaml");

        Assert.DoesNotContain("Margin=\"Auto", view);
        Assert.Contains("<ColumnDefinition Width=\"*\"/>", view);
    }

    [Fact]
    public void Sprint5Forensic_MasterPlanUsesLockedCostingLanguage()
    {
        var plan = File.ReadAllText(Path.Combine(
            SolutionRoot(),
            "docs",
            "Sprint5_Master_Implementation_Plan.md"));

        Assert.Contains("Moving Weighted Average costing is now locked", plan);
        Assert.Contains("historical cost-snapshot rules", plan);
        Assert.DoesNotContain("until costing policy is approved", plan);
        Assert.DoesNotContain("must not invent FIFO/weighted-average profit calculations", plan);
    }
}
