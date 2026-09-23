namespace EdgeRetails.UnitTests;

public sealed class Sprint7Phase2BusinessCutoverForensicTests
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

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([SolutionRoot(), .. parts]));

    [Fact]
    public void Thaka_Has_Persistent_Read_And_Write_Gateways()
    {
        var reads = Read(
            "src", "EdgeRetails.Infrastructure", "Services", "ThakaReadService.cs");
        var gateway = Read(
            "src", "EdgeRetails.Desktop", "Services", "BackendThakaService.cs");

        Assert.Contains("thaka.projects", reads);
        Assert.Contains("thaka.material_issues", reads);
        Assert.Contains("thaka.payments", reads);
        Assert.Contains("CreateThakaProjectHandler", gateway);
        Assert.Contains("IssueThakaMaterialHandler", gateway);
        Assert.Contains("RecordThakaPaymentHandler", gateway);
        Assert.Contains("SettleThakaHandler", gateway);
        Assert.Contains("IReadOnlyList<Guid> inventoryUnitIds", gateway);
        Assert.Contains("inventoryUnitIds", gateway);
    }

    [Fact]
    public void Thaka_Workspace_Reloads_Authoritative_Ledgers()
    {
        var workspace = Read(
            "src", "EdgeRetails.Desktop", "ViewModels", "ThakaWorkspaceViewModel.cs");
        var project = Read(
            "src", "EdgeRetails.Desktop", "ViewModels",
            "ThakaProjectListItemViewModel.cs");

        Assert.Contains("GetWorkspaceAsync", workspace);
        Assert.Contains("MaterialLedger.Clear()", workspace);
        Assert.Contains("PaymentLedger.Clear()", workspace);
        Assert.Contains("BackendProjectId", project);
        Assert.Contains("SettlementDiscount", project);
    }

    [Fact]
    public void Expenses_Use_Backend_And_Stay_Immutable_After_Posting()
    {
        var gateway = Read(
            "src", "EdgeRetails.Desktop", "Services",
            "BackendBusinessOperationsService.cs");
        var view = Read(
            "src", "EdgeRetails.Desktop", "ViewModels", "ExpensesViewModel.cs");

        Assert.Contains("PostExpenseHandler", gateway);
        Assert.Contains("GetExpensesAsync", gateway);
        Assert.Contains("Posted expenses are immutable", view);
        Assert.Contains("RefreshBackendAsync", view);
    }

    [Fact]
    public void Parties_Use_Backend_Ids_And_Persist_Notes()
    {
        var domain = Read(
            "src", "EdgeRetails.Domain", "Parties", "PartyModels.cs");
        var gateway = Read(
            "src", "EdgeRetails.Desktop", "Services",
            "BackendBusinessOperationsService.cs");
        var models = Read(
            "src", "EdgeRetails.Desktop", "Services",
            "DemoBusinessDirectoryService.cs");

        Assert.Contains("public string? Notes", domain);
        Assert.Contains("SaveCustomerHandler", gateway);
        Assert.Contains("SaveSupplierHandler", gateway);
        Assert.Contains("BackendId", models);
    }

    [Fact]
    public void Party_Metrics_Are_Relational_Not_Name_Matched()
    {
        var reads = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "BusinessOperationsReadServices.cs");

        Assert.Contains("x.CustomerId == customer.Id", reads);
        Assert.Contains("x.SupplierId == supplier.Id", reads);
        Assert.Contains("sale.CustomerId", reads);
        Assert.DoesNotContain("Contains(customer.Name", reads);
    }

    [Fact]
    public void Reports_Use_Persisted_Financial_Ledgers()
    {
        var reads = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "BusinessOperationsReadServices.cs");
        var view = Read(
            "src", "EdgeRetails.Desktop", "ViewModels", "ReportsViewModel.cs");

        Assert.Contains("_db.Sales.AsNoTracking()", reads);
        Assert.Contains("_db.SaleReturns.AsNoTracking()", reads);
        Assert.Contains("CostReversalAmount", reads);
        Assert.Contains("_db.Expenses.AsNoTracking()", reads);
        Assert.Contains("_db.Purchases.AsNoTracking()", reads);
        Assert.Contains("_db.ThakaMaterialIssues.AsNoTracking()", reads);
        Assert.Contains("GetReportAsync", view);
    }

    [Fact]
    public void Composition_Root_Registers_Phase2_Business_Authorities()
    {
        var source = Read(
            "src", "EdgeRetails.Infrastructure",
            "InfrastructureServiceCollectionExtensions.cs");

        Assert.Contains("IThakaRepository, ThakaRepository", source);
        Assert.Contains("IExpenseRepository, ExpenseRepository", source);
        Assert.Contains("IThakaReadService, ThakaReadService", source);
        Assert.Contains("IExpenseReadService, ExpenseReadService", source);
        Assert.Contains("IPartyDirectoryReadService, PartyDirectoryReadService", source);
        Assert.Contains("IReportingReadService, ReportingReadService", source);
        Assert.Contains("PostExpenseHandler", source);
        Assert.Contains("SaveSupplierHandler", source);
    }

    [Fact]
    public void Desktop_Still_Does_Not_Own_Database_Persistence()
    {
        var desktopRoot = Path.Combine(
            SolutionRoot(),
            "src",
            "EdgeRetails.Desktop");

        var combined = string.Join(
            "\n",
            Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("EdgeRetailsDbContext", combined);
        Assert.DoesNotContain("NpgsqlConnection", combined);
        Assert.DoesNotContain("SaveChangesAsync", combined);
        Assert.DoesNotContain("FromSql", combined);
    }
}
