using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ProductSaleHistoryItemViewModel
{
    public required string InvoiceNumber { get; init; }
    public required DateTime Timestamp { get; init; }
    public required string CustomerName { get; init; }
    public required decimal Quantity { get; init; }
    public required decimal UnitPrice { get; init; }
    public required decimal LineTotal { get; init; }

    public string DateDisplay => Timestamp.ToString("dd MMM yyyy");
    public string QuantityDisplay => Quantity.ToString("0.##");
    public string UnitPriceDisplay => $"Rs. {UnitPrice:N0}";
    public string LineTotalDisplay => $"Rs. {LineTotal:N0}";
}

public sealed class ProductDetailViewModel : ViewModelBase, IDisposable
{
    private readonly DemoPurchaseInventoryService? _inventoryService;
    private readonly DemoRetailState? _retailState;
    private readonly ITransactionService? _transactionService;
    private readonly IBackendPurchasingInventoryService? _backendService;
    private readonly IBackendProductManagementService? _catalogService;
    private readonly IBackendStockAdjustmentService? _stockAdjustmentService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly IBackendLabelService? _labelService;
    private readonly Func<string?> _chooseExportFolder;
    private bool _isLabelBusy;
    private string? _labelStatusMessage;
    private string _selectedTab = "Overview";

    public ProductDetailViewModel(
        PosProductItemViewModel product,
        IDialogService dialogService,
        IToastService toastService,
        Action close,
        IBackendPurchasingInventoryService? backendService = null,
        IBackendProductManagementService? catalogService = null,
        IBackendStockAdjustmentService? stockAdjustmentService = null,
        IBackendLabelService? labelService = null,
        Func<string?>? chooseExportFolder = null)
    {
        Product = product;
        _dialogService = dialogService;
        _toastService = toastService;
        _close = close;
        _backendService = backendService;
        _catalogService = catalogService;
        _stockAdjustmentService = stockAdjustmentService;
        _labelService = labelService ?? backendService as IBackendLabelService;
        _chooseExportFolder = chooseExportFolder ?? LabelExportFolderPicker.Choose;
        _inventoryService = ResolvePreviewInventoryService(backendService);
        _retailState = ResolvePreviewRetailState(backendService);
        _transactionService = ResolvePreviewTransactionService(backendService);

        Movements = [];
        Purchases = [];
        Sales = [];
        SelectTabCommand = new RelayCommand<string>(SelectTab);
        BackCommand = new RelayCommand(close);
        EditProductCommand = new RelayCommand(() => _ = OpenEditProductAsync());
        StockAdjustmentCommand = new RelayCommand(OpenAdjustment);
        PrintProductLabelCommand = new RelayCommand(() => _ = RunProductLabelAsync(false, false), CanOutputLabel);
        ReprintProductLabelCommand = new RelayCommand(() => _ = RunProductLabelAsync(true, false), CanOutputLabel);
        ExportProductLabelPdfCommand = new RelayCommand(() => _ = RunProductLabelAsync(false, true), CanOutputLabel);

        if (_backendService is null &&
            _inventoryService is not null &&
            _retailState is not null &&
            _transactionService is not null)
        {
            _inventoryService.StateChanged += OnStateChanged;
            _retailState.StateChanged += OnStateChanged;
            _transactionService.TransactionRecorded += OnTransactionRecorded;
            _transactionService.ReturnRecorded += OnReturnRecorded;
        }

        Refresh();
    }

    public void Dispose()
    {
        if (_backendService is null &&
            _inventoryService is not null &&
            _retailState is not null &&
            _transactionService is not null)
        {
            _inventoryService.StateChanged -= OnStateChanged;
            _retailState.StateChanged -= OnStateChanged;
            _transactionService.TransactionRecorded -= OnTransactionRecorded;
            _transactionService.ReturnRecorded -= OnReturnRecorded;
        }
    }

    public PosProductItemViewModel Product { get; }
    public ObservableCollection<InventoryMovementRecord> Movements { get; }
    public ObservableCollection<PurchaseRecord> Purchases { get; }
    public ObservableCollection<ProductSaleHistoryItemViewModel> Sales { get; }

    public string SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsOverview));
                OnPropertyChanged(nameof(IsMovements));
                OnPropertyChanged(nameof(IsPurchases));
                OnPropertyChanged(nameof(IsSales));
            }
        }
    }

    public bool IsOverview => SelectedTab == "Overview";
    public bool IsMovements => SelectedTab == "StockMovement";
    public bool IsPurchases => SelectedTab == "Purchases";
    public bool IsSales => SelectedTab == "Sales";

    public string StockDisplay => $"{Product.Stock:0.##} {Product.Unit}";
    public string CostDisplay => Product.CostDisplay;
    public string PriceDisplay => Product.PriceDisplay;
    public string MinimumStockDisplay => $"{Product.MinimumStock:0.##} {Product.Unit}";
    public string SkuDisplay => $"SKU: {Product.Sku}";
    public string CategoryDisplay => $"Category: {Product.Category}";
    public string UnitDisplay => $"Unit: {Product.Unit}";
    public string ModelDisplay => string.IsNullOrWhiteSpace(Product.Model) ? "Model: —" : $"Model: {Product.Model}";

    public ICommand SelectTabCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand EditProductCommand { get; }
    public ICommand StockAdjustmentCommand { get; }
    public ICommand PrintProductLabelCommand { get; }
    public ICommand ReprintProductLabelCommand { get; }
    public ICommand ExportProductLabelPdfCommand { get; }
    public string? LabelStatusMessage
    {
        get => _labelStatusMessage;
        private set => SetProperty(ref _labelStatusMessage, value);
    }

    private bool CanOutputLabel() => !_isLabelBusy && _labelService is not null
        && Product.BackendProductUnitId is Guid id && id != Guid.Empty;

    private async Task RunProductLabelAsync(bool isReprint, bool export)
    {
        if (!CanOutputLabel()) { return; }
        var unitId = Product.BackendProductUnitId!.Value;
        _isLabelBusy = true;
        NotifyLabelCommands();
        try
        {
            if (export)
            {
                var folder = _chooseExportFolder();
                if (string.IsNullOrWhiteSpace(folder)) { return; }
                var output = await _labelService!.ExportLabelPdfAsync([], [unitId], folder!, isReprint);
                LabelStatusMessage = output.IsSuccess
                    ? $"Product label PDFs and manifest saved in {folder}."
                    : $"Label export failed: {output.Error?.Message}. Product identity is unchanged.";
            }
            else
            {
                var output = await _labelService!.PrintProductLabelAsync(unitId, null, isReprint);
                if (!output.IsSuccess || output.Value is null)
                {
                    LabelStatusMessage = $"Product label failed: {output.Error?.Message}. Product identity is unchanged.";
                    return;
                }
                var job = output.Value;
                LabelStatusMessage = job.Succeeded
                    ? job.AuditPersisted
                        ? "Product label submitted to Windows. Check the physical printer."
                        : "Product label submitted to Windows, but its audit receipt could not be recorded. Check the printer before reprinting."
                    : "Product label submission failed or is unconfirmed. Check the physical printer before reprinting; product identity is unchanged.";
            }
        }
        catch (Exception ex)
        {
            LabelStatusMessage = DesktopErrorPresentation.ForException(ex,
                "Label output could not be confirmed. Check the printer or export folder before retrying; product identity is unchanged.");
        }
        finally
        {
            _isLabelBusy = false;
            NotifyLabelCommands();
        }
    }

    private void NotifyLabelCommands()
    {
        ((RelayCommand)PrintProductLabelCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReprintProductLabelCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ExportProductLabelPdfCommand).NotifyCanExecuteChanged();
    }

    private void SelectTab(string tab)
    {
        if (!string.IsNullOrWhiteSpace(tab))
        {
            SelectedTab = tab;
        }
    }

    private async Task OpenEditProductAsync()
    {
        if (_catalogService is null ||
            Product.BackendProductId is not Guid productId)
        {
            _toastService.Show(
                "Authoritative catalog editing is unavailable for this product.",
                ToastTone.Warning);
            return;
        }

        try
        {
            var item = await _catalogService.GetProductAsync(productId);
            var snapshot = await _catalogService.GetSnapshotAsync();
            if (item is null)
            {
                _toastService.Show("Product no longer exists in the catalog.", ToastTone.Warning);
                return;
            }

            _dialogService.Show(new ProductEditViewModel(
                item,
                snapshot,
                _catalogService,
                _toastService,
                _dialogService.Close,
                RefreshCatalogAsync));
        }
        catch (BackendCatalogOperationException ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Product details could not be loaded."),
                ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Product editor could not be opened."),
                ToastTone.Danger);
        }
    }

    private void OpenAdjustment()
    {
        if (_backendService is not null && _stockAdjustmentService is null)
        {
            _toastService.Show(
                "Stock adjustment is blocked until the authoritative backend adjustment flow is attached.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new StockAdjustmentViewModel(
            Product,
            _toastService,
            _dialogService.Close,
            _stockAdjustmentService,
            () => _ = RefreshBackendAsync()));
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        Refresh();
    }

    private void OnTransactionRecorded(object? sender, SaleTransactionRecord e)
    {
        Refresh();
    }

    private void OnReturnRecorded(object? sender, SaleReturnRecord e)
    {
        Refresh();
    }

    private void Refresh()
    {
        if (_backendService is not null)
        {
            _ = RefreshBackendAsync();
            return;
        }

        if (_inventoryService is null || _transactionService is null)
        {
            Movements.Clear();
            Purchases.Clear();
            Sales.Clear();
            NotifyHeaderChanged();
            return;
        }

        Movements.Clear();
        foreach (var movement in _inventoryService.GetMovements(Product.Id))
        {
            Movements.Add(movement);
        }

        Purchases.Clear();
        foreach (var purchase in _inventoryService.Purchases
            .Where(p => p.Items.Any(item => item.Product.Id == Product.Id))
            .OrderByDescending(p => p.Date))
        {
            Purchases.Add(purchase);
        }

        Sales.Clear();
        foreach (var sale in _transactionService.GetAllTransactions()
            .OrderByDescending(transaction => transaction.Timestamp))
        {
            foreach (var item in sale.Items.Where(item =>
                string.Equals(item.ProductId, Product.Id, StringComparison.OrdinalIgnoreCase)))
            {
                Sales.Add(new ProductSaleHistoryItemViewModel
                {
                    InvoiceNumber = sale.InvoiceNumber,
                    Timestamp = sale.Timestamp,
                    CustomerName = sale.CustomerName,
                    Quantity = item.Quantity,
                    UnitPrice = item.UnitPrice,
                    LineTotal = item.LineTotal
                });
            }
        }

        NotifyHeaderChanged();
    }

    private static DemoPurchaseInventoryService? ResolvePreviewInventoryService(
        IBackendPurchasingInventoryService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoPurchaseInventoryService.Instance : null;
#else
        return null;
#endif
    }

    private static DemoRetailState? ResolvePreviewRetailState(
        IBackendPurchasingInventoryService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoRetailState.Instance : null;
#else
        return null;
#endif
    }

    private static ITransactionService? ResolvePreviewTransactionService(
        IBackendPurchasingInventoryService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoTransactionService.Instance : null;
#else
        return null;
#endif
    }

    private async Task RefreshCatalogAsync()
    {
        if (_catalogService is null ||
            Product.BackendProductId is not Guid productId)
        {
            return;
        }

        var item = await _catalogService.GetProductAsync(productId);
        if (item is null)
        {
            return;
        }

        Product.Name = item.Name;
        Product.Sku = item.Sku;
        Product.Brand = string.IsNullOrWhiteSpace(item.Brand) ? "—" : item.Brand;
        Product.Model = item.Model ?? string.Empty;
        Product.Category = item.Category;
        Product.Unit = item.BaseUnit;
        Product.Price = item.DefaultSalePrice;
        Product.MinimumStock = item.MinimumStockLevel;
        NotifyHeaderChanged();
        await RefreshBackendAsync();
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null ||
            Product.BackendProductId is not Guid productId)
        {
            return;
        }

        try
        {
            var snapshot = await _backendService.GetInventorySnapshotAsync();
            var inventoryProduct = snapshot.Products.FirstOrDefault(
                x => x.BackendProductId == productId);
            if (inventoryProduct is not null)
            {
                Product.Stock = inventoryProduct.Stock;
                Product.Cost = inventoryProduct.Cost;
                Product.MinimumStock = inventoryProduct.MinimumStock;
            }

            // Bounded product purchases replaces full GetPurchasesAsync
            var purchases = await _backendService.GetProductPurchasesAsync(productId);
            var sales = await _backendService.GetProductSalesAsync(productId);

            Movements.Clear();
            foreach (var movement in snapshot.Movements
                .Where(x => string.Equals(
                    x.ProductId,
                    productId.ToString("D"),
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.Timestamp))
            {
                Movements.Add(movement);
            }

            Purchases.Clear();
            foreach (var purchase in purchases)
            {
                Purchases.Add(purchase);
            }

            Sales.Clear();
            foreach (var sale in sales.OrderByDescending(x => x.Timestamp))
            {
                Sales.Add(new ProductSaleHistoryItemViewModel
                {
                    InvoiceNumber = sale.InvoiceNumber,
                    Timestamp = sale.Timestamp,
                    CustomerName = sale.CustomerName,
                    Quantity = sale.Quantity,
                    UnitPrice = sale.UnitPrice,
                    LineTotal = sale.LineTotal
                });
            }

            NotifyHeaderChanged();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "Product history could not be refreshed. Check the connection and try again."),
                ToastTone.Danger);
        }
    }

    private void NotifyHeaderChanged()
    {
        OnPropertyChanged(nameof(StockDisplay));
        OnPropertyChanged(nameof(CostDisplay));
        OnPropertyChanged(nameof(PriceDisplay));
        OnPropertyChanged(nameof(MinimumStockDisplay));
        OnPropertyChanged(nameof(SkuDisplay));
        OnPropertyChanged(nameof(CategoryDisplay));
        OnPropertyChanged(nameof(UnitDisplay));
        OnPropertyChanged(nameof(ModelDisplay));
    }
}
