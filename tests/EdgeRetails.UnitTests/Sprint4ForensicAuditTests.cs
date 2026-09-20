namespace EdgeRetails.UnitTests;

public sealed class Sprint4ForensicAuditTests
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
    public void Sprint4Navigation_MapsPurchasesAndInventory()
    {
        var factory = ReadDesktop("Navigation", "PageViewModelFactory.cs");
        var app = ReadDesktop("App.xaml");

        Assert.Contains("NavigationTarget.Purchases => new PurchaseHistoryViewModel", factory);
        Assert.Contains("NavigationTarget.Inventory => new InventoryViewModel", factory);
        Assert.Contains("DataType=\"{x:Type viewModels:PurchaseHistoryViewModel}\"", app);
        Assert.Contains("DataType=\"{x:Type viewModels:NewPurchaseViewModel}\"", app);
        Assert.Contains("DataType=\"{x:Type viewModels:InventoryViewModel}\"", app);
    }

    [Fact]
    public void PurchaseInventory_UsesSharedProductState()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");
        var purchase = ReadDesktop("ViewModels", "NewPurchaseViewModel.cs");

        Assert.Contains("DemoRetailState.Instance", service);
        Assert.Contains("DemoRetailState.Instance.Products", purchase);
        Assert.Contains("line.Product.Stock =", service);
        Assert.Contains("line.Product.Cost =", service);
        Assert.Contains("line.Product.Price =", service);
    }

    [Fact]
    public void InventoryMovement_IsTruthForSprint4Mutations()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("InventoryMovementKind.PurchaseIn", service);
        Assert.Contains("InventoryMovementKind.PurchaseReturnOut", service);
        Assert.Contains("InventoryMovementKind.AdjustmentIn", service);
        Assert.Contains("InventoryMovementKind.AdjustmentOut", service);
        Assert.Contains("BeforeQuantity = before", service);
        Assert.Contains("AfterQuantity =", service);
    }

    [Fact]
    public void PurchaseReturn_EnforcesEligibleQuantityAndStockCap()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("EligibleReturnQuantity", service);
        Assert.Contains("exceeds eligible quantity", service);
        Assert.Contains("Quantity > Item.Product.Stock", service);
        Assert.Contains("ReturnedQuantity =", service);
    }

    [Fact]
    public void Inventory_DoesNotExposeEditableCurrentStockOrInventoryValue()
    {
        var view = ReadDesktop("Views", "InventoryView.xaml");

        Assert.DoesNotContain("Inventory Value", view);
        Assert.DoesNotContain("Stock, Mode=TwoWay", view);
        Assert.Contains("IsReadOnly=\"True\"", view);
        Assert.Contains("StockAdjustmentCommand", view);
    }
    [Fact]
    public void Sprint4Views_UseNoCanvas()
    {
        var files = new[]
        {
            ReadDesktop("Views", "PurchaseHistoryView.xaml"),
            ReadDesktop("Views", "NewPurchaseView.xaml"),
            ReadDesktop("Views", "InventoryView.xaml")
        };

        Assert.All(files, content => Assert.DoesNotContain("<Canvas", content));
    }

    [Fact]
    public void SharedProduct_HasCostMinimumStockAndDecimalStock()
    {
        var product = ReadDesktop("ViewModels", "PosProductItemViewModel.cs");

        Assert.Contains("private decimal _stock", product);
        Assert.Contains("private decimal _cost", product);
        Assert.Contains("private decimal _minimumStock", product);
        Assert.Contains("public decimal Cost", product);
        Assert.Contains("public decimal MinimumStock", product);
        Assert.Contains("public bool IsLowStock", product);
        Assert.Contains("public bool IsOutOfStock", product);
    }

    [Fact]
    public void PurchaseDetail_UsesDrawer_AndReturnUsesModal()
    {
        var history = ReadDesktop("ViewModels", "PurchaseHistoryViewModel.cs");
        var detail = ReadDesktop("ViewModels", "PurchaseDetailViewModel.cs");

        Assert.Contains("_drawerService.Show(new PurchaseDetailViewModel", history);
        Assert.Contains("_dialogService.Show(new PurchaseReturnViewModel", detail);
        Assert.Contains("completed: OnReturnProcessed", detail);
    }

    [Fact]
    public void ProductDetail_RemainsInsideInventoryFlow()
    {
        var inventory = ReadDesktop("ViewModels", "InventoryViewModel.cs");
        var view = ReadDesktop("Views", "InventoryView.xaml");

        Assert.Contains("CurrentProductDetail = new ProductDetailViewModel", inventory);
        Assert.Contains("IsDetailViewActive = true", inventory);
        Assert.Contains("Content=\"{Binding CurrentProductDetail}\"", view);
    }

    [Fact]
    public void ProductEdit_DoesNotDirectlyEditStock()
    {
        var edit = ReadDesktop("ViewModels", "ProductEditViewModel.cs");
        var dialog = ReadDesktop("Views", "Dialogs", "ProductEditDialog.xaml");

        Assert.DoesNotContain("_product.Stock =", edit);
        Assert.DoesNotContain("Stock, UpdateSourceTrigger", dialog);
        Assert.Contains("Use Stock Adjustment for audited changes", dialog);
    }

    [Fact]
    public void StockAdjustment_WritesControlledMovement()
    {
        var vm = ReadDesktop("ViewModels", "StockAdjustmentViewModel.cs");
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("_service.AdjustStock(Product, delta, Reason, Note)", vm);
        Assert.Contains("Adjustment cannot make sellable stock negative", service);
        Assert.Contains("InventoryMovementKind.AdjustmentIn", service);
        Assert.Contains("InventoryMovementKind.AdjustmentOut", service);
    }

    [Fact]
    public void ProductDetail_SalesTabUsesSharedSalesLedger()
    {
        var vm = ReadDesktop("ViewModels", "ProductDetailViewModel.cs");
        var view = ReadDesktop("Views", "ProductDetailView.xaml");

        Assert.Contains("DemoTransactionService.Instance", vm);
        Assert.Contains("_transactionService.GetAllTransactions()", vm);
        Assert.Contains("string.Equals(item.ProductId, Product.Id", vm);
        Assert.Contains("ItemsSource=\"{Binding Sales}\"", view);
        Assert.DoesNotContain("will be populated from the shared sales ledger", view);
    }

    [Fact]
    public void ProductEdit_RejectsDuplicateSku()
    {
        var edit = ReadDesktop("ViewModels", "ProductEditViewModel.cs");

        Assert.Contains("Another product already uses this SKU.", edit);
        Assert.Contains("!ReferenceEquals(existing, _product)", edit);
    }

    [Fact]
    public void Sprint4OverlayTemplates_AreRegistered()
    {
        var app = ReadDesktop("App.xaml");

        Assert.Contains("PurchaseDetailViewModel", app);
        Assert.Contains("PurchaseReturnViewModel", app);
        Assert.Contains("ProductDetailViewModel", app);
        Assert.Contains("StockAdjustmentViewModel", app);
        Assert.Contains("ProductEditViewModel", app);
    }

    [Fact]
    public void LiveSales_WriteInventoryMovementWithInvoiceReference()
    {
        var retail = ReadDesktop("Services", "DemoRetailState.cs");
        var sale = ReadDesktop("ViewModels", "NewSaleViewModel.cs");

        Assert.Contains("DemoStockMutationKind.SaleOut", retail);
        Assert.Contains("record.InvoiceNumber", sale);
        Assert.Contains("RecordStockMutation", retail);
    }

    [Fact]
    public void ThakaMaterialIssue_WritesInventoryMovement()
    {
        var retail = ReadDesktop("Services", "DemoRetailState.cs");

        Assert.Contains("DemoStockMutationKind.ThakaOut", retail);
        Assert.Contains("Material issue {challanNumber}", retail);
        Assert.Contains("Reference = project.Id", retail);
    }

    [Fact]
    public void SaleReturn_ReconcilesSellableAndNonSellableStock()
    {
        var retail = ReadDesktop("Services", "DemoRetailState.cs");
        var returns = ReadDesktop("ViewModels", "SalesReturnViewModel.cs");

        Assert.Contains("ApplySaleReturnStock(record)", returns);
        Assert.Contains("SaleReturnDisposition.RestockSellable", retail);
        Assert.Contains("DemoStockMutationKind.SaleReturnIn", retail);
        Assert.Contains("DemoStockMutationKind.Damage", retail);
    }

    [Fact]
    public void InventoryService_ImportsAndSubscribesToRetailMutations()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("_retailState.StockMutations.OrderBy", service);
        Assert.Contains("_retailState.StockMutationRecorded += OnRetailStockMutationRecorded", service);
        Assert.Contains("InventoryMovementKind.SaleOut", service);
        Assert.Contains("InventoryMovementKind.SaleReturnIn", service);
        Assert.Contains("InventoryMovementKind.ThakaOut", service);
    }

    [Fact]
    public void Phase5_UsesCanonicalOpeningBalanceAndStockTruthAudit()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("InventoryMovementKind.OpeningBalance", service);
        Assert.Contains("Reference = \"OPENING-S4\"", service);
        Assert.Contains("AuditStockTruth()", service);
        Assert.Contains("ProjectedQuantity = projected", service);
        Assert.Contains("ActualQuantity = product.Stock", service);
        Assert.Contains("public bool IsReconciled", service);
    }

    [Fact]
    public void Phase5_NewProductsReceiveAStockBaseline()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("_retailState.StateChanged += OnRetailStateChanged", service);
        Assert.Contains("EnsureOpeningBalancesForNewProducts()", service);
        Assert.Contains("movement.Kind == InventoryMovementKind.OpeningBalance", service);
    }

    [Fact]
    public void Phase5_PurchaseReturnsHaveSeparateAuditRecords()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("ObservableCollection<PurchaseReturnRecord> PurchaseReturns", service);
        Assert.Contains("ReturnNumber = returnNumber", service);
        Assert.Contains("PurchaseNumber = purchase.PurchaseNumber", service);
        Assert.Contains("GetPurchaseReturns(string purchaseNumber)", service);
        Assert.Contains("Reference = returnNumber", service);
    }

    [Fact]
    public void Phase5_CheckoutRevalidatesLiveStockBeforeRecordingTransaction()
    {
        var sale = ReadDesktop("ViewModels", "NewSaleViewModel.cs");
        var checkout = ReadDesktop("ViewModels", "CompleteSaleViewModel.cs");

        Assert.Contains("preCommitValidation: ValidateCartStockForCommit", sale);
        Assert.Contains("item.Quantity > item.Product.Stock", sale);
        Assert.Contains("_preCommitValidation?.Invoke()", checkout);

        var validationIndex = checkout.IndexOf("_preCommitValidation?.Invoke()", StringComparison.Ordinal);
        var recordIndex = checkout.IndexOf("RecordTransactionAsync(request)", StringComparison.Ordinal);
        Assert.True(validationIndex >= 0 && recordIndex > validationIndex);
    }

    [Fact]
    public void Phase5_AllOperationalStockPathsUseSharedProductObjects()
    {
        var retail = ReadDesktop("Services", "DemoRetailState.cs");
        var purchase = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");
        var sale = ReadDesktop("ViewModels", "NewSaleViewModel.cs");
        var thaka = ReadDesktop("ViewModels", "ThakaAddMaterialViewModel.cs");

        Assert.Contains("AllProducts = _retailState.Products", sale);
        Assert.Contains("Product = product", thaka);
        Assert.Contains("line.Product.Stock =", purchase);
        Assert.Contains("product.Stock =", retail);
    }

    [Fact]
    public void Phase5_PurchaseReturnEligibilityAlsoCapsAtCurrentSellableStock()
    {
        var service = ReadDesktop("Services", "DemoPurchaseInventoryService.cs");

        Assert.Contains("PurchasedQuantity - UsedQuantity - ReturnedQuantity", service);
        Assert.Contains("Product.Stock", service);
        Assert.Contains("Math.Min(", service);
    }

    [Fact]
    public void Phase5_InventoryConsumesStockTruthAudit()
    {
        var inventory = ReadDesktop("ViewModels", "InventoryViewModel.cs");

        Assert.Contains("_inventoryService.AuditStockTruth()", inventory);
        Assert.Contains("StockTruthIssueCount", inventory);
        Assert.Contains("IsStockTruthHealthy", inventory);
    }
}
