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
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private string _selectedTab = "Overview";

    public ProductDetailViewModel(
        PosProductItemViewModel product,
        IDialogService dialogService,
        IToastService toastService,
        Action close,
        IBackendPurchasingInventoryService? backendService = null,
        IBackendProductManagementService? catalogService = null)
    {
        Product = product;
        _dialogService = dialogService;
        _toastService = toastService;
        _close = close;
        _backendService = backendService;
        _catalogService = catalogService;
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
            _toastService.Show(ex.Message, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService.Show($"Product editor could not be opened: {ex.Message}", ToastTone.Danger);
        }
    }

    private void OpenAdjustment()
    {
        if (_backendService is not null)
        {
            _toastService.Show(
                "Stock adjustment is blocked until the authoritative backend adjustment flow is attached.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new StockAdjustmentViewModel(Product, _toastService, _dialogService.Close));
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
        Product.Brand = string.IsNullOrWhiteSpace(item.Brand) ? "â€”" : item.Brand;
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
                $"Product history could not be refreshed: {ex.Message}",
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
