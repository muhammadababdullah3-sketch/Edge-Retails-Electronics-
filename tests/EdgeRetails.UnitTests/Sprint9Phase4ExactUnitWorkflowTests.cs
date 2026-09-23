namespace EdgeRetails.UnitTests;

public sealed class Sprint9Phase4ExactUnitWorkflowTests
{
    private static string Root()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EdgeRetails.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(parts.Aggregate(Root(), Path.Combine));

    private static string MethodSlice(string source, string start, string end)
    {
        var a = source.IndexOf(start, StringComparison.Ordinal);
        var b = source.IndexOf(end, a + start.Length, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        Assert.True(b > a, end);
        return source[a..b];
    }

    [Fact]
    public void UniversalScanner_UsesCanonicalExactFirstPrecedence()
    {
        var source = Read("src", "EdgeRetails.Infrastructure", "Services", "Phase4WorkflowReadService.cs");
        var method = MethodSlice(source, "ResolveScannerAsync(", "GetOpenDraftsAsync(");
        var tracking = method.IndexOf("ScannerResolutionNamespace.TrackingCode", StringComparison.Ordinal);
        var manufacturer = method.IndexOf("ScannerResolutionNamespace.ManufacturerSerialOrImei", StringComparison.Ordinal);
        var barcode = method.IndexOf("ScannerResolutionNamespace.ProductUnitBarcode", StringComparison.Ordinal);
        var sku = method.IndexOf("ScannerResolutionNamespace.ProductBarcode", StringComparison.Ordinal);
        var broad = method.IndexOf("ScannerResolutionNamespace.BroaderSearch", StringComparison.Ordinal);
        Assert.True(tracking >= 0 && tracking < manufacturer);
        Assert.True(manufacturer < barcode && barcode < sku && sku < broad);
    }

    [Fact]
    public void Pos_ExactUnitScannerAndDuplicateProtection_AreOperational()
    {
        var pos = Read("src", "EdgeRetails.Desktop", "ViewModels", "PosViewModel.cs");
        var transaction = Read("src", "EdgeRetails.Desktop", "Services", "BackendTransactionService.cs");
        var sale = Read("src", "EdgeRetails.Application", "Features", "Sales", "CompleteSaleHandler.cs");
        Assert.Contains("ResolveScannerAsync(input)", pos);
        Assert.Contains("AddExactUnitToCart", pos);
        Assert.Contains("already in the cart", pos);
        Assert.Contains("Interlocked.Exchange(ref _scanInFlight", pos);
        Assert.Contains("InventoryUnitIds = item.InventoryUnitIds", pos);
        Assert.Contains("item.InventoryUnitIds", transaction);
        Assert.Contains("sales.serial_selected_twice", sale);
        Assert.Contains("sales.serial_not_sellable", sale);
    }

    [Fact]
    public void Pos_OperationIdentity_IsOwnedByCheckoutIntentAndRetainedAcrossRetry()
    {
        var pos = Read("src", "EdgeRetails.Desktop", "ViewModels", "PosViewModel.cs");
        var checkout = Read("src", "EdgeRetails.Desktop", "ViewModels", "CompleteSaleViewModel.cs");
        Assert.Contains("_pendingSaleOperationId ??= Guid.CreateVersion7()", pos);
        Assert.Contains("clientOperationId: _pendingSaleOperationId", pos);
        Assert.Contains("_clientOperationId = clientOperationId ?? Guid.CreateVersion7()", checkout);
        Assert.Contains("ClientOperationId = _clientOperationId", checkout);
    }

    [Fact]
    public void Pos_DraftHoldResumeCancel_ReachesBackendAuthority()
    {
        var pos = Read("src", "EdgeRetails.Desktop", "ViewModels", "PosViewModel.cs");
        var drafts = Read("src", "EdgeRetails.Desktop", "ViewModels", "PosDraftsViewModel.cs");
        var backend = Read("src", "EdgeRetails.Desktop", "Services", "BackendPhase4WorkflowService.cs");
        Assert.Contains("SaveDraftAsync", pos);
        Assert.Contains("ResumeDraftAsync", pos);
        Assert.Contains("CancelDraftAsync", drafts);
        Assert.Contains("SavePosDraftHandler", backend);
        Assert.Contains("CancelPosDraftHandler", backend);
        Assert.Contains("GetDraftAsync", backend);
    }

    [Fact]
    public void PriceCheck_IsAuthoritativeAndDoesNotMutateCart()
    {
        var pos = Read("src", "EdgeRetails.Desktop", "ViewModels", "PosViewModel.cs");
        var method = MethodSlice(pos, "private async Task PriceCheckAsync()", "private async Task<bool> SaveDraftAsync(");
        var price = Read("src", "EdgeRetails.Desktop", "ViewModels", "PriceCheckViewModel.cs");
        Assert.Contains("ResolveScannerAsync(input)", method);
        Assert.Contains("new PriceCheckViewModel", method);
        Assert.DoesNotContain("AddToCart(", method);
        Assert.DoesNotContain("CartItems.Add", price);
    }

    [Fact]
    public void SerializedPurchase_RequiresIdentityCountAndRejectsDuplicateManufacturerIds()
    {
        var purchase = Read("src", "EdgeRetails.Desktop", "ViewModels", "NewPurchaseViewModel.cs");
        var intake = Read("src", "EdgeRetails.Desktop", "ViewModels", "SerializedPurchaseIntakeViewModel.cs");
        var backend = Read("src", "EdgeRetails.Desktop", "Services", "BackendPurchasingInventoryService.cs");
        Assert.Contains("RequiredSerializedUnitCount", purchase);
        Assert.Contains("HasValidSerializedIntake", purchase);
        Assert.Contains("Duplicate Serial Number", intake);
        Assert.Contains("Duplicate IMEI", intake);
        Assert.Contains("SerializedIdentities", backend);
        Assert.Contains("_clientOperationId", purchase);
    }

    [Fact]
    public void PurchaseReturn_UsesExactOriginalPurchaseUnits_AndProductionFailsClosed()
    {
        var vm = Read("src", "EdgeRetails.Desktop", "ViewModels", "PurchaseReturnViewModel.cs");
        var backend = Read("src", "EdgeRetails.Desktop", "Services", "BackendPurchasingInventoryService.cs");
        Assert.Contains("sourcePurchaseItemId: purchaseItemId", vm);
        Assert.Contains("SelectedUnits.Select(x => x.InventoryUnitId)", vm);
        Assert.Contains("Production purchase return requires authoritative backend service.", vm);
        Assert.Contains("#if DEBUG", vm);
        Assert.Contains("PurchaseReturnLineInput", backend);
        Assert.Contains("selection.InventoryUnitIds", backend);
    }

    [Fact]
    public void SerializedThaka_ExactUnitIdentity_ReachesBackendCommand()
    {
        var vm = Read("src", "EdgeRetails.Desktop", "ViewModels", "ThakaAddMaterialViewModel.cs");
        var backend = Read("src", "EdgeRetails.Desktop", "Services", "BackendThakaService.cs");
        var handler = Read("src", "EdgeRetails.Application", "Features", "Thaka", "ThakaHandlers.cs");
        Assert.Contains("SelectExactUnitsCommand", vm);
        Assert.Contains("_selectedExactUnits.Select(x => x.InventoryUnitId)", vm);
        Assert.Contains("_clientOperationId", vm);
        Assert.Contains("IReadOnlyList<Guid> inventoryUnitIds", backend);
        Assert.Contains("inventoryUnitIds", backend);
        Assert.Contains("thaka.serial_selection_invalid", handler);
        Assert.Contains("InventoryUnitStatus.InStock", handler);

        var workspace = Read("src", "EdgeRetails.Desktop", "ViewModels", "ThakaWorkspaceViewModel.cs");
        var reversal = Read("src", "EdgeRetails.Application", "Features", "Thaka", "ThakaReversalHandlers.cs");
        Assert.Contains("ReverseMaterialAsync", workspace);
        Assert.Contains("_pendingMaterialReversalOperationId ??= Guid.CreateVersion7()", workspace);
        Assert.Contains("ReverseThakaMaterialHandler", backend);
        Assert.Contains("InventoryUnitStatus.IssuedThaka", reversal);
        Assert.Contains("unit.Status = InventoryUnitStatus.InStock", reversal);
    }

    [Fact]
    public void Inventory_ExposesPhysicalUnitsAndCanonicalStocktakeWithoutCatalogMutation()
    {
        var inventory = Read("src", "EdgeRetails.Desktop", "ViewModels", "InventoryViewModel.cs");
        var stocktake = Read("src", "EdgeRetails.Desktop", "ViewModels", "StocktakeViewModel.cs");
        var serialized = Read("src", "EdgeRetails.Desktop", "ViewModels", "SerializedStocktakeScanViewModel.cs");
        Assert.Contains("ViewPhysicalUnitsCommand", inventory);
        Assert.Contains("new ExactUnitPickerViewModel", inventory);
        Assert.Contains("StocktakeCommand", inventory);
        Assert.Contains("new StocktakeViewModel", inventory);
        Assert.Contains("RecordStocktakeCountAsync", stocktake);
        Assert.Contains("RecordSerializedStocktakeAsync", serialized);
        Assert.DoesNotContain("AddProductCommand", inventory);
    }

    [Fact]
    public void Phase4Composition_IsReachableFromCanonicalDesktop()
    {
        var factory = Read("src", "EdgeRetails.Desktop", "Navigation", "PageViewModelFactory.cs");
        var app = Read("src", "EdgeRetails.Desktop", "App.xaml");
        Assert.Contains("new BackendPhase4WorkflowService", factory);
        Assert.Contains("_phase4WorkflowService", factory);
        Assert.Contains("ExactUnitPickerViewModel", app);
        Assert.Contains("SerializedPurchaseIntakeViewModel", app);
        Assert.Contains("PriceCheckViewModel", app);
        Assert.Contains("PosDraftsViewModel", app);
        Assert.Contains("StocktakeViewModel", app);
    }
}
