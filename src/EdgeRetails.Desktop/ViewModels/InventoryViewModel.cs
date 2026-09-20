using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class InventoryViewModel : ViewModelBase
{
    private readonly DemoRetailState _retailState;
    private readonly DemoPurchaseInventoryService _inventoryService;
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private string _searchText = string.Empty;
    private string _selectedTab = "AllStock";
    private string _selectedCategory = "All";
    private string _selectedBrand = "All";
    private string _selectedStockStatus = "All";
    private PosProductItemViewModel? _selectedProduct;
    private bool _isDetailViewActive;
    private ProductDetailViewModel? _currentProductDetail;

    public InventoryViewModel(
        IToastService toastService,
        IDialogService dialogService)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _retailState = DemoRetailState.Instance;
        _inventoryService = DemoPurchaseInventoryService.Instance;

        StockStatuses = ["All", "In Stock", "Low Stock", "Out of Stock"];
        FilteredProducts = [];
        FilteredMovements = [];

        SelectTabCommand = new RelayCommand<string>(SelectTab);
        OpenProductCommand = new RelayCommand<PosProductItemViewModel>(OpenProduct);
        AddProductCommand = new RelayCommand(OpenAddProduct);
        StockAdjustmentCommand = new RelayCommand(OpenSelectedAdjustment);

        _inventoryService.StateChanged += OnStateChanged;
        _retailState.StateChanged += OnStateChanged;
        Refresh();
    }

    public IReadOnlyList<string> Categories =>
        ["All", .. _retailState.Products.Select(p => p.Category).Distinct().OrderBy(x => x)];
    public IReadOnlyList<string> Brands =>
        ["All", .. _retailState.Products.Select(p => p.Brand).Distinct().OrderBy(x => x)];
    public IReadOnlyList<string> StockStatuses { get; }
    public ObservableCollection<PosProductItemViewModel> FilteredProducts { get; }
    public ObservableCollection<InventoryMovementRecord> FilteredMovements { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Refresh();
            }
        }
    }

    public string SelectedTab
    {
        get => _selectedTab;
        private set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsStockListTab));
                OnPropertyChanged(nameof(IsMovementsTab));
                Refresh();
            }
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value ?? "All"))
            {
                Refresh();
            }
        }
    }

    public string SelectedBrand
    {
        get => _selectedBrand;
        set
        {
            if (SetProperty(ref _selectedBrand, value ?? "All"))
            {
                Refresh();
            }
        }
    }

    public string SelectedStockStatus
    {
        get => _selectedStockStatus;
        set
        {
            if (SetProperty(ref _selectedStockStatus, value ?? "All"))
            {
                Refresh();
            }
        }
    }

    public PosProductItemViewModel? SelectedProduct
    {
        get => _selectedProduct;
        set => SetProperty(ref _selectedProduct, value);
    }

    public bool IsStockListTab => SelectedTab != "Movements";
    public bool IsMovementsTab => SelectedTab == "Movements";

    public bool IsDetailViewActive
    {
        get => _isDetailViewActive;
        private set => SetProperty(ref _isDetailViewActive, value);
    }

    public ProductDetailViewModel? CurrentProductDetail
    {
        get => _currentProductDetail;
        private set => SetProperty(ref _currentProductDetail, value);
    }

    public int TotalProducts => _retailState.Products.Count;
    public int LowStockCount => _retailState.Products.Count(p => p.IsLowStock);
    public int OutOfStockCount => _retailState.Products.Count(p => p.IsOutOfStock);

    public string TotalProductsDisplay => TotalProducts.ToString();
    public string LowStockDisplay => LowStockCount.ToString();
    public string OutOfStockDisplay => OutOfStockCount.ToString();
    public int StockTruthIssueCount { get; private set; }
    public bool IsStockTruthHealthy => StockTruthIssueCount == 0;

    public ICommand SelectTabCommand { get; }
    public ICommand OpenProductCommand { get; }
    public ICommand AddProductCommand { get; }
    public ICommand StockAdjustmentCommand { get; }

    private void SelectTab(string tab)
    {
        if (!string.IsNullOrWhiteSpace(tab))
        {
            SelectedTab = tab;
        }
    }

    private void OpenProduct(PosProductItemViewModel product)
    {
        SelectedProduct = product;
        CurrentProductDetail = new ProductDetailViewModel(
            product,
            _dialogService,
            _toastService,
            CloseProductDetail);
        IsDetailViewActive = true;
    }

    private void CloseProductDetail()
    {
        IsDetailViewActive = false;
        CurrentProductDetail = null;
    }

    private void OpenAddProduct()
    {
        _dialogService.Show(new ProductEditViewModel(null, _toastService, _dialogService.Close));
    }

    private void OpenSelectedAdjustment()
    {
        if (SelectedProduct is null)
        {
            _toastService.Show("Select a product before stock adjustment.", ToastTone.Warning);
            return;
        }

        _dialogService.Show(new StockAdjustmentViewModel(
            SelectedProduct,
            _toastService,
            _dialogService.Close));
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        IEnumerable<PosProductItemViewModel> products = _retailState.Products;

        products = SelectedTab switch
        {
            "LowStock" => products.Where(p => p.IsLowStock),
            "OutOfStock" => products.Where(p => p.IsOutOfStock),
            _ => products
        };

        if (!string.Equals(SelectedCategory, "All", StringComparison.OrdinalIgnoreCase))
        {
            products = products.Where(p => p.Category == SelectedCategory);
        }

        if (!string.Equals(SelectedBrand, "All", StringComparison.OrdinalIgnoreCase))
        {
            products = products.Where(p => p.Brand == SelectedBrand);
        }

        products = SelectedStockStatus switch
        {
            "In Stock" => products.Where(p => p.Stock > p.MinimumStock),
            "Low Stock" => products.Where(p => p.IsLowStock),
            "Out of Stock" => products.Where(p => p.IsOutOfStock),
            _ => products
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            products = products.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Brand.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Model.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FilteredProducts.Clear();
        foreach (var product in products)
        {
            FilteredProducts.Add(product);
        }

        FilteredMovements.Clear();
        foreach (var movement in _inventoryService.Movements.OrderByDescending(m => m.Timestamp))
        {
            FilteredMovements.Add(movement);
        }

        OnPropertyChanged(nameof(TotalProducts));
        OnPropertyChanged(nameof(LowStockCount));
        OnPropertyChanged(nameof(OutOfStockCount));
        var stockTruth = _inventoryService.AuditStockTruth();
        StockTruthIssueCount = stockTruth.Count(result => !result.IsReconciled);

        OnPropertyChanged(nameof(TotalProductsDisplay));
        OnPropertyChanged(nameof(LowStockDisplay));
        OnPropertyChanged(nameof(OutOfStockDisplay));
        OnPropertyChanged(nameof(StockTruthIssueCount));
        OnPropertyChanged(nameof(IsStockTruthHealthy));
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(Brands));
    }
}
