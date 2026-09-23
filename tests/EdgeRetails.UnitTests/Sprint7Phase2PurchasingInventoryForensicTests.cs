namespace EdgeRetails.UnitTests;

public sealed class Sprint7Phase2PurchasingInventoryForensicTests
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
    public void Purchase_Catalog_Uses_Authoritative_Purchase_Unit()
    {
        var contract = Read(
            "src", "EdgeRetails.Application", "Features", "Purchasing",
            "PurchaseCatalogQueries.cs");
        var service = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "PurchaseCatalogReadService.cs"); Assert.Contains("IPurchaseCatalogReadService", contract);
        Assert.Contains("Guid ProductUnitId", contract);
        Assert.Contains("SerialTrackingEnabled", contract);
        Assert.Contains("ImeiTrackingEnabled", contract);
        Assert.Contains("productUnit.CanPurchase", service);
        Assert.Contains("productUnit.IsDefaultPurchaseUnit", service);
        Assert.DoesNotContain("IsDefaultSaleUnit", service);
    }

    [Fact]
    public void Purchase_Detail_Uses_Origin_Specific_Return_Eligibility()
    {
        var contract = Read(
            "src", "EdgeRetails.Application", "Features", "Purchasing",
            "PurchaseQueries.cs");
        var reads = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "DapperReadServices.cs");

        Assert.Contains("ReturnedBaseQuantity", contract);
        Assert.Contains("EligibleBaseReturnQuantity", contract);
        Assert.Contains("bool IsSerialized", contract);
        Assert.Contains("string UnitSymbol", contract);
        Assert.Contains("l.purchase_item_id = pi.id", reads);
        Assert.Contains("cp.tracking_mode = 3", reads);
        Assert.Contains("inventory.lot_bucket_balances", reads);
        Assert.Contains("lb.stock_bucket = 1", reads);
        Assert.Contains("purchasing.return_items", reads);
    }

    [Fact]
    public void Purchase_Gateway_Uses_Handlers_And_Fails_Closed()
    {
        var gateway = Read(
            "src", "EdgeRetails.Desktop", "Services",
            "BackendPurchasingInventoryService.cs");

        Assert.Contains("CreatePurchaseHandler", gateway);
        Assert.Contains("CreatePurchaseReturnHandler", gateway);
        Assert.Contains("VoidPurchaseHandler", gateway);
        Assert.Contains("A persistent backend user session is required", gateway);
        Assert.Contains("PurchaseSettlementMode.External", gateway);
        Assert.Contains("PurchaseReturnSettlementMode.External", gateway);
        Assert.Contains("Serialized purchase requires Serial/IMEI intake", gateway);
        Assert.Contains("Serialized purchase return requires exact unit selection", gateway);
    }

    [Fact]
    public void New_Purchase_Propagates_Supplier_And_Product_Uuids()
    {
        var source = Read(
            "src", "EdgeRetails.Desktop", "ViewModels",
            "NewPurchaseViewModel.cs");

        Assert.Contains("_backendSupplierIds", source);
        Assert.Contains("GroupBy(x => x.Name", source);
        Assert.Contains("matches.Length != 1", source);
        Assert.Contains("backendProductId: item.ProductId", source);
        Assert.Contains("backendProductUnitId: item.ProductUnitId", source);
        Assert.Contains("CreatePurchaseAsync", source);
    }
    [Fact]
    public void Purchase_Return_Uses_Backend_Purchase_Item_Identity()
    {
        var source = Read(
            "src", "EdgeRetails.Desktop", "ViewModels",
            "PurchaseReturnViewModel.cs");
        var model = Read(
            "src", "EdgeRetails.Desktop", "Services",
            "DemoPurchaseInventoryService.cs");

        Assert.Contains("BackendPurchaseItemId", model);
        Assert.Contains("BackendEligibleReturnQuantity", model);
        Assert.Contains("BackendPurchaseItemId!.Value", source);
        Assert.Contains("ReturnPurchaseAsync", source);
    }

    [Fact]
    public void Inventory_Backend_Reads_Authoritative_Stock_And_Movements()
    {
        var contract = Read(
            "src", "EdgeRetails.Application", "Features", "Inventory",
            "InventoryOverviewQueries.cs");
        var service = Read(
            "src", "EdgeRetails.Infrastructure", "Services",
            "InventoryOverviewReadService.cs");
        var viewModel = Read(
            "src", "EdgeRetails.Desktop", "ViewModels",
            "InventoryViewModel.cs");

        Assert.Contains("SellableQty", contract);
        Assert.Contains("DamagedQty", contract);
        Assert.Contains("DefectiveQty", contract);
        Assert.Contains("_db.InventoryMovements.AsNoTracking()", service);
        Assert.Contains("_db.InventoryMovementEffects.AsNoTracking()", service);
        Assert.Contains("GetInventorySnapshotAsync", viewModel);
    }
    [Fact]
    public void Inventory_Backend_Blocks_Demo_Mutations()
    {
        var inventory = Read(
            "src", "EdgeRetails.Desktop", "ViewModels",
            "InventoryViewModel.cs");
        var detail = Read(
            "src", "EdgeRetails.Desktop", "ViewModels",
            "ProductDetailViewModel.cs");

        Assert.DoesNotContain("AddProductCommand", inventory);
        Assert.Contains("IBackendProductManagementService", inventory);
        Assert.Contains("Stock adjustment is blocked", inventory);
        Assert.Contains("_catalogService", detail);
        Assert.Contains("OpenEditProductAsync", detail);
        Assert.Contains("GetProductSalesAsync", detail);
        Assert.Contains("GetPurchasesAsync", detail);
        Assert.Contains("GetInventorySnapshotAsync", detail);
    }

    [Fact]
    public void Desktop_Remains_Free_Of_Direct_Persistence_Authority()
    {
        var desktopRoot = Path.Combine(
            SolutionRoot(),
            "src",
            "EdgeRetails.Desktop");

        var combined = string.Join(
            "\n",
            Directory.EnumerateFiles(desktopRoot, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText)); Assert.DoesNotContain("NpgsqlConnection", combined);
        Assert.DoesNotContain("FromSql", combined);
        Assert.DoesNotContain("SaveChangesAsync", combined);
        Assert.DoesNotContain("EdgeRetailsDbContext", combined);
    }
}
