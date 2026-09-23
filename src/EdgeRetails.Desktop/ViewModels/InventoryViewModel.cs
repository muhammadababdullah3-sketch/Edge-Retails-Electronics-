using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class InventoryViewModel : ViewModelBase, IDisposable
{
    private readonly DemoRetailState? _retailState;
    private readonly DemoPurchaseInventoryService? _inventoryService;
    private readonly IBackendPurchasingInventoryService? _backendService;
    private readonly IBackendProductManagementService? _catalogService;
    private readonly IBackendPhase4WorkflowService? _phase4Service;
    private readonly List<PosProductItemViewModel> _backendProducts = [];
    private readonly List<InventoryMovementRecord> _backendMovements = [];
    private bool _backendLoaded;
    private bool _backendLoading;
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
        IDialogService dialogService,
        IBackendPurchasingInventoryService? backendService = null,
        IBackendProductManagementService? catalogService = null,
        IBackendPhase4WorkflowService? phase4Service = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _backendService = backendService;
        _catalogService = catalogService;
        _phase4Service = phase4Service;
        _retailState = ResolvePreviewRetailState(backendService);
        _inventoryService = ResolvePreviewInventoryService(backendService);

        StockStatuses = ["All", "In Stock", "Low Stock", "Out of Stock"];
        FilteredProducts = [];
        FilteredMovements = [];

        SelectTabCommand = new RelayCommand<string>(SelectTab);
        OpenProductCommand = new RelayCommand<PosProductItemViewModel>(OpenProduct);
        StockAdjustmentCommand = new RelayCommand(OpenSelectedAdjustment);
        ViewPhysicalUnitsCommand = new RelayCommand(OpenPhysicalUnits);
        StocktakeCommand = new RelayCommand(OpenStocktake);

        if (_backendService is null &&
            _inventoryService is not null &&
            _retailState is not null)
        {
            _inventoryService.StateChanged += OnStateChanged;
            _retailState.StateChanged += OnStateChanged;
        }

        Refresh();
    }

    public void Dispose()
    {
        CurrentProductDetail?.Dispose();
        CurrentProductDetail = null;
        if (_backendService is null &&
            _inventoryService is not null &&
            _retailState is not null)
        {
            _inventoryService.StateChanged -= OnStateChanged;
            _retailState.StateChanged -= OnStateChanged;
        }
    }

    private IReadOnlyList<PosProductItemViewModel> ProductSource =>
        _backendService is null
            ? _retailState?.Products ?? []
            : _backendProducts;

    public IReadOnlyList<string> Categories =>
        ["All", .. ProductSource.Select(p => p.Category).Distinct().OrderBy(x => x)];
    public IReadOnlyList<string> Brands =>
        ["All", .. ProductSource.Select(p => p.Brand).Where(x => x != "—").Distinct().OrderBy(x => x)];
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

    public int TotalProducts => ProductSource.Count;
    public int LowStockCount => ProductSource.Count(p => p.IsLowStock);
    public int OutOfStockCount => ProductSource.Count(p => p.IsOutOfStock);

    public string TotalProductsDisplay => TotalProducts.ToString();
    public string LowStockDisplay => LowStockCount.ToString();
    public string OutOfStockDisplay => OutOfStockCount.ToString();
    public int StockTruthIssueCount { get; private set; }
    public bool IsStockTruthHealthy => StockTruthIssueCount == 0;

    public ICommand SelectTabCommand { get; }
    public ICommand OpenProductCommand { get; }
    public ICommand StockAdjustmentCommand { get; }
    public ICommand ViewPhysicalUnitsCommand { get; }
    public ICommand StocktakeCommand { get; }

    private void SelectTab(string tab)
    {
        if (!string.IsNullOrWhiteSpace(tab))
        {
            SelectedTab = tab;
        }
    }

    private void OpenProduct(PosProductItemViewModel product)
    {
        CurrentProductDetail?.Dispose();
        SelectedProduct = product;
        CurrentProductDetail = new ProductDetailViewModel(
            product,
            _dialogService,
            _toastService,
            CloseProductDetail,
            _backendService,
            _catalogService);
        IsDetailViewActive = true;
    }

    private void CloseProductDetail()
    {
        IsDetailViewActive = false;
        CurrentProductDetail?.Dispose();
        CurrentProductDetail = null;
    }

    // Product creation backend flow is not attached to legacy Inventory; it is authoritative in ProductManagement.
    private void OpenPhysicalUnits()
    {
        if (_phase4Service is null)
        {
            _toastService.Show(
                "Authoritative physical-unit view is unavailable.",
                ToastTone.Warning);
            return;
        }

        if (SelectedProduct?.BackendProductId is not Guid productId)
        {
            _toastService.Show(
                "Select a backend product first.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new ExactUnitPickerViewModel(
            "Physical Inventory Units",
            $"{SelectedProduct.Name} · TrackingCode / Serial / IMEI / provenance",
            productId,
            _phase4Service,
            _dialogService,
            _ => { },
            requiredCount: 0,
            status: null,
            sourcePurchaseItemId: null,
            selectionRequired: false));
    }

    private void OpenStocktake()
    {
        if (_phase4Service is null)
        {
            _toastService.Show(
                "Authoritative stocktake workflow is unavailable.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new StocktakeViewModel(
            _phase4Service,
            _dialogService,
            _toastService,
            completed: () => _ = RefreshBackendAsync()));
    }

    private void OpenSelectedAdjustment()
    {
        if (_backendService is not null)
        {
            _toastService.Show(
                "Stock adjustment is blocked until the authoritative backend adjustment flow is attached.",
                ToastTone.Warning);
            return;
        }

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
        if (_backendService is not null)
        {
            if (!_backendLoaded && !_backendLoading)
            {
                _ = RefreshBackendAsync();
                return;
            }

            ApplyFilters(_backendProducts, _backendMovements);
            return;
        }

        if (_retailState is null || _inventoryService is null)
        {
            ApplyFilters([], []);
            StockTruthIssueCount = 0;
            OnPropertyChanged(nameof(StockTruthIssueCount));
            OnPropertyChanged(nameof(IsStockTruthHealthy));
            return;
        }

        ApplyFilters(
            _retailState.Products,
            _inventoryService.Movements.OrderByDescending(x => x.Timestamp));

        var stockTruth = _inventoryService.AuditStockTruth();
        StockTruthIssueCount = stockTruth.Count(result => !result.IsReconciled);
        OnPropertyChanged(nameof(StockTruthIssueCount));
        OnPropertyChanged(nameof(IsStockTruthHealthy));
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null || _backendLoading)
        {
            return;
        }

        _backendLoading = true;
        try
        {
            var snapshot = await _backendService.GetInventorySnapshotAsync();

            _backendProducts.Clear();
            _backendProducts.AddRange(snapshot.Products);
            _backendMovements.Clear();
            _backendMovements.AddRange(snapshot.Movements);
            _backendLoaded = true;

            StockTruthIssueCount = 0;
            ApplyFilters(_backendProducts, _backendMovements);
            OnPropertyChanged(nameof(StockTruthIssueCount));
            OnPropertyChanged(nameof(IsStockTruthHealthy));
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Inventory could not be refreshed: {ex.Message}",
                ToastTone.Danger);
        }
        finally
        {
            _backendLoading = false;
        }
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

    private static DemoPurchaseInventoryService? ResolvePreviewInventoryService(
        IBackendPurchasingInventoryService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoPurchaseInventoryService.Instance : null;
#else
        return null;
#endif
    }

    private void ApplyFilters(
        IEnumerable<PosProductItemViewModel> productSource,
        IEnumerable<InventoryMovementRecord> movementSource)
    {
        var products = productSource;

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
            "In Stock" when _backendService is not null =>
                products.Where(p => p.Stock > 0m),
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
        foreach (var movement in movementSource.OrderByDescending(m => m.Timestamp))
        {
            FilteredMovements.Add(movement);
        }

        OnPropertyChanged(nameof(TotalProducts));
        OnPropertyChanged(nameof(LowStockCount));
        OnPropertyChanged(nameof(OutOfStockCount));
        OnPropertyChanged(nameof(TotalProductsDisplay));
        OnPropertyChanged(nameof(LowStockDisplay));
        OnPropertyChanged(nameof(OutOfStockDisplay));
        OnPropertyChanged(nameof(Categories));
        OnPropertyChanged(nameof(Brands));
    }
}
