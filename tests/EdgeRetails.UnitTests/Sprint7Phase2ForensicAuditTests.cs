namespace EdgeRetails.UnitTests;

public sealed class Sprint7Phase2ForensicAuditTests
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
    public void Phase2_Defines_Authoritative_Pos_Catalog_Read_Model()
    {
        var source = Read(
            "src", "EdgeRetails.Application", "Features", "Sales",
            "PosCatalogQueries.cs");

        Assert.Contains("Guid ProductId", source);
        Assert.Contains("Guid ProductUnitId", source);
        Assert.Contains("decimal SellableStock", source);
        Assert.Contains("decimal UnitPrice", source);
        Assert.Contains("IPosCatalogReadService", source);
    }
    [Fact]
    public void Phase2_Pos_Catalog_Comes_From_Infrastructure_Persistence()
    {
        var source = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "PosCatalogReadService.cs");

        Assert.Contains("_db.Products.AsNoTracking()", source);
        Assert.Contains("_db.ProductUnits.AsNoTracking()", source);
        Assert.Contains("_db.StockBalances.AsNoTracking()", source);
        Assert.Contains("IsDefaultSaleUnit", source);
        Assert.DoesNotContain("DemoRetailState", source);
    }

    [Fact]
    public void Phase2_Registers_Transactional_Handlers_In_Composition_Root()
    {
        var source = Read(
            "src", "EdgeRetails.Infrastructure",
            "InfrastructureServiceCollectionExtensions.cs");

        Assert.Contains("IPosCatalogReadService, PosCatalogReadService", source);
        Assert.Contains("CompleteSaleHandler", source);
        Assert.Contains("CreateSaleReturnHandler", source);
        Assert.Contains("CreatePurchaseHandler", source);
        Assert.Contains("CreatePurchaseReturnHandler", source);
        Assert.Contains("VoidPurchaseHandler", source);
    }
    [Fact]
    public void Phase2_Desktop_Still_Has_No_Direct_Database_Authority()
    {
        var desktopRoot = Path.Combine(
            SolutionRoot(),
            "src",
            "EdgeRetails.Desktop");

        var combined = string.Join(
            "\n",
            Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

        Assert.DoesNotContain("NpgsqlConnection", combined);
        Assert.DoesNotContain("FromSql", combined);
        Assert.DoesNotContain("SaveChangesAsync", combined);
    }

    [Fact]
    public void Phase2_Backend_Runtime_Is_Environment_Gated()
    {
        var runtime = Read(
            "src", "EdgeRetails.Desktop", "Services", "BackendRuntime.cs");

        Assert.Contains("EDGE_RETAILS_DB", runtime);
        Assert.Contains("AddEdgeRetailsInfrastructure", runtime);
        Assert.Contains("IServiceScopeFactory", runtime);
        Assert.DoesNotContain("Host=localhost", runtime);
        Assert.DoesNotContain("Password=", runtime);
    }

    [Fact]
    public void Phase2_Sales_Write_Requires_Authoritative_Actor_And_Uuids()
    {
        var service = Read(
            "src", "EdgeRetails.Desktop", "Services",
            "BackendTransactionService.cs");

        Assert.Contains("A persistent backend user session is required", service);
        Assert.Contains("BackendProductId", service);
        Assert.Contains("BackendProductUnitId", service);
        Assert.Contains("CompleteSaleHandler", service);
        Assert.Contains("CreateSaleReturnHandler", service);
        Assert.Contains("Guid.CreateVersion7()", service);
    }

    [Fact]
    public void Phase2_Pos_Propagates_Backend_Catalog_Identity()
    {
        var sale = Read(
            "src", "EdgeRetails.Desktop", "ViewModels", "PosViewModel.cs");

        Assert.Contains("LoadBackendCatalogAsync", sale);
        Assert.Contains("backendProductId: row.ProductId", sale);
        Assert.Contains("backendProductUnitId: row.ProductUnitId", sale);
        Assert.Contains("BackendProductId = item.Product.BackendProductId", sale);
        Assert.Contains("BackendProductUnitId = item.Product.BackendProductUnitId", sale);
        Assert.Contains("SelectedCustomer?.BackendId", sale);
        Assert.Contains("_isBackendCatalog", sale);
    }

    [Fact]
    public void Phase2_SalesHistory_Uses_Async_Backend_Read_Path()
    {
        var history = Read(
            "src", "EdgeRetails.Desktop", "ViewModels",
            "SalesHistoryViewModel.cs");

        Assert.Contains("GetAllTransactionsAsync", history);
        Assert.Contains("invoiceDisplayOverride", history);
        Assert.Contains("s.InvoiceDisplay.Contains", history);
    }

    [Fact]
    public void Phase2_SaleDetail_Reads_Exact_Return_Items()
    {
        var contract = Read(
            "src", "EdgeRetails.Application", "Features", "Sales",
            "SalesQueries.cs");
        var reads = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "DapperReadServices.cs");

        Assert.Contains("SaleReturnItemDetailDto", contract);
        Assert.Contains("IReadOnlyList<SaleReturnItemDetailDto> ReturnItems", contract);
        Assert.Contains("sales.return_items", reads);
        Assert.Contains("ri.sale_item_id AS SaleItemId", reads);
        Assert.Contains("returnItems", reads);
    }
}

