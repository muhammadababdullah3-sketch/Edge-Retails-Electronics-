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

public sealed class ProductDetailViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService _inventoryService;
    private readonly DemoRetailState _retailState;
    private readonly ITransactionService _transactionService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private string _selectedTab = "Overview";

    public ProductDetailViewModel(
        PosProductItemViewModel product,
        IDialogService dialogService,
        IToastService toastService,
        Action close)
    {
        Product = product;
        _dialogService = dialogService;
        _toastService = toastService;
        _close = close;
        _inventoryService = DemoPurchaseInventoryService.Instance;
        _retailState = DemoRetailState.Instance;
        _transactionService = DemoTransactionService.Instance;

        Movements = [];
        Purchases = [];
        Sales = [];
        SelectTabCommand = new RelayCommand<string>(SelectTab);
        BackCommand = new RelayCommand(close);
        EditProductCommand = new RelayCommand(OpenEditProduct);
        StockAdjustmentCommand = new RelayCommand(OpenAdjustment);

        _inventoryService.StateChanged += OnStateChanged;
        _retailState.StateChanged += OnStateChanged;
        _transactionService.TransactionRecorded += OnTransactionRecorded;
        _transactionService.ReturnRecorded += OnReturnRecorded;
        Refresh();
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

    private void OpenEditProduct()
    {
        _dialogService.Show(new ProductEditViewModel(Product, _toastService, _dialogService.Close));
    }

    private void OpenAdjustment()
    {
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
