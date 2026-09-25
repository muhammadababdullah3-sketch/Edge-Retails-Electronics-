using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ProductManagementViewModel : ViewModelBase, IDisposable
{
    private readonly IBackendProductManagementService? _catalogService;
    private readonly IBackendPurchasingInventoryService? _inventoryService;
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private BackendProductManagementSnapshot? _snapshot;
    private string _searchText = string.Empty;
    private string _selectedCategory = "All categories";
    private string _selectedStatus = "All";
    private BackendProductManagementItem? _selectedProduct;
    private bool _isLoading;
    private string? _errorMessage;
    private ProductDetailViewModel? _currentProductDetail;
    private bool _isDetailViewActive;
    private CancellationTokenSource? _productPageSearchCts;
    private long _productPageSearchVersion;
    private const int ProductPageSize = 200;

    public ProductManagementViewModel(
        IToastService toastService,
        IDialogService dialogService,
        IBackendProductManagementService? catalogService = null,
        IBackendPurchasingInventoryService? inventoryService = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _catalogService = catalogService;
        _inventoryService = inventoryService;

        FilteredProducts = [];
        Categories = new ObservableCollection<string>(["All categories"]);
        StatusFilters = ["All", "Active", "Inactive"];

        AddProductCommand = new RelayCommand(OpenAddProduct, CanMutate);
        ManageCategoriesCommand = new RelayCommand(
            () => OpenCatalogReferenceManager(CatalogReferenceKind.Category),
            CanMutate);
        ManageUnitsCommand = new RelayCommand(
            () => OpenCatalogReferenceManager(CatalogReferenceKind.Unit),
            CanMutate);
        EditProductCommand = new RelayCommand<BackendProductManagementItem>(OpenEditProduct);
        OpenProductCommand = new RelayCommand<BackendProductManagementItem>(OpenProduct);
        ToggleActiveCommand = new RelayCommand<BackendProductManagementItem>(
            product => _ = ToggleActiveAsync(product));
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync(), () => !IsLoading);
        CloseDetailCommand = new RelayCommand(CloseProductDetail);

        _ = RefreshAsync();
    }

    public ObservableCollection<BackendProductManagementItem> FilteredProducts { get; }
    public ObservableCollection<string> Categories { get; }
    public IReadOnlyList<string> StatusFilters { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ScheduleBackendPageRefresh();
            }
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value ?? "All categories"))
            {
                ScheduleBackendPageRefresh();
            }
        }
    }

    public string SelectedStatus
    {
        get => _selectedStatus;
        set
        {
            if (SetProperty(ref _selectedStatus, value ?? "All"))
            {
                ScheduleBackendPageRefresh();
            }
        }
    }

    public BackendProductManagementItem? SelectedProduct
    {
        get => _selectedProduct;
        set => SetProperty(ref _selectedProduct, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsContentReady));
                if (RefreshCommand is RelayCommand refresh)
                {
                    refresh.NotifyCanExecuteChanged();
                }
                if (AddProductCommand is RelayCommand add)
                {
                    add.NotifyCanExecuteChanged();
                }
                (ManageCategoriesCommand as RelayCommand)?.NotifyCanExecuteChanged();
                (ManageUnitsCommand as RelayCommand)?.NotifyCanExecuteChanged();
            }
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
                OnPropertyChanged(nameof(IsContentReady));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool IsContentReady => !IsLoading && !HasError;
    public bool IsEmpty => IsContentReady && (_snapshot?.Products.Count ?? 0) == 0;
    public bool IsSearchNoResults =>
        IsContentReady &&
        (_snapshot?.Products.Count ?? 0) > 0 &&
        FilteredProducts.Count == 0;

    public string ResultCountDisplay =>
        IsLoading ? "Loading catalog…" : $"{FilteredProducts.Count} product(s)";

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

    public ICommand AddProductCommand { get; }
    public ICommand ManageCategoriesCommand { get; }
    public ICommand ManageUnitsCommand { get; }
    public ICommand EditProductCommand { get; }
    public ICommand OpenProductCommand { get; }
    public ICommand ToggleActiveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CloseDetailCommand { get; }

    public void Dispose()
    {
        CurrentProductDetail?.Dispose();
        CurrentProductDetail = null;
    }

    private bool CanMutate() =>
        _catalogService is not null &&
        !IsLoading &&
        _snapshot is not null;

    private async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        if (_catalogService is null)
        {
            ErrorMessage = "Production catalog backend is unavailable.";
            ApplyFilters();
            return;
        }

        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _snapshot = await _catalogService.GetSnapshotAsync(cancellationToken: CancellationToken.None);
            RebuildCategoryFilter();
            await LoadCurrentProductPageAsync(CancellationToken.None);
        }
        catch (BackendCatalogOperationException ex)
        {
            ErrorMessage = ex.Code == "authorization.denied"
                ? "You do not have permission to manage the catalog."
                : ex.Message;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Catalog could not be loaded: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            NotifyState();
            if (AddProductCommand is RelayCommand add)
            {
                add.NotifyCanExecuteChanged();
            }
        }
    }

    private void RebuildCategoryFilter()
    {
        var selected = SelectedCategory;
        Categories.Clear();
        Categories.Add("All categories");

        if (_snapshot is not null)
        {
            foreach (var category in _snapshot.Categories
                .Select(x => x.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Categories.Add(category);
            }
        }

        SelectedCategory = Categories.Contains(selected)
            ? selected
            : "All categories";
    }

    private void ApplyFilters()
    {
        if (_catalogService is not null)
        {
            ScheduleBackendPageRefresh();
            return;
        }

        var products = _snapshot?.Products.AsEnumerable()
            ?? Enumerable.Empty<BackendProductManagementItem>();

        if (!string.Equals(SelectedCategory, "All categories", StringComparison.OrdinalIgnoreCase))
        {
            products = products.Where(x => string.Equals(x.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        products = SelectedStatus switch
        {
            "Active" => products.Where(x => x.IsActive),
            "Inactive" => products.Where(x => !x.IsActive),
            _ => products
        };

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            products = products.Where(x =>
                x.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (x.Brand?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.Model?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                x.Category.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FilteredProducts.Clear();
        foreach (var product in products.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Sku, StringComparer.OrdinalIgnoreCase))
        {
            FilteredProducts.Add(product);
        }

        NotifyState();
    }

    private void ScheduleBackendPageRefresh()
    {
        if (_catalogService is null || IsLoading)
        {
            if (_catalogService is null)
            {
                ApplyFilters();
            }
            return;
        }

        var version = Interlocked.Increment(ref _productPageSearchVersion);
        var previous = Interlocked.Exchange(ref _productPageSearchCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cts = _productPageSearchCts!;
        _ = RefreshProductPageAfterDebounceAsync(version, cts);
    }

    private async Task RefreshProductPageAfterDebounceAsync(long version, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(250, cts.Token);
            if (version != Volatile.Read(ref _productPageSearchVersion))
            {
                return;
            }

            await LoadCurrentProductPageAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _productPageSearchVersion))
        {
            ErrorMessage = $"Catalog search failed: {ex.Message}";
            NotifyState();
        }
    }

    private async Task LoadCurrentProductPageAsync(CancellationToken cancellationToken)
    {
        if (_catalogService is null)
        {
            return;
        }

        var categoryId = _snapshot?.Categories
            .FirstOrDefault(x => string.Equals(x.Name, SelectedCategory, StringComparison.OrdinalIgnoreCase))?.Id;
        var isActive = SelectedStatus switch
        {
            "Active" => true,
            "Inactive" => false,
            _ => (bool?)null
        };

        var rows = await _catalogService.GetProductsPageAsync(
            SearchText,
            isActive,
            categoryId,
            ProductPageSize,
            cancellationToken: cancellationToken);

        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        FilteredProducts.Clear();
        foreach (var product in rows)
        {
            FilteredProducts.Add(product);
        }

        NotifyState();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(IsSearchNoResults));
        OnPropertyChanged(nameof(ResultCountDisplay));
        OnPropertyChanged(nameof(IsContentReady));
    }

    private void OpenCatalogReferenceManager(CatalogReferenceKind kind)
    {
        if (_catalogService is null)
        {
            _toastService.Show("Catalog backend is not ready.", ToastTone.Warning);
            return;
        }

        _dialogService.Show(new CatalogReferenceManagerViewModel(
            kind,
            _catalogService,
            _toastService,
            _dialogService.Close,
            RefreshAsync));
    }

    private void OpenAddProduct()
    {
        if (_catalogService is null || _snapshot is null)
        {
            _toastService.Show("Catalog backend is not ready.", ToastTone.Warning);
            return;
        }

        if (_snapshot.Units.All(x => !x.IsActive))
        {
            _toastService.Show(
                "At least one active catalog unit is required before creating a product.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new ProductEditViewModel(
            null,
            _snapshot,
            _catalogService,
            _toastService,
            _dialogService.Close,
            RefreshAsync));
    }

    private void OpenEditProduct(BackendProductManagementItem product)
    {
        if (_catalogService is null || _snapshot is null)
        {
            _toastService.Show("Catalog backend is not ready.", ToastTone.Warning);
            return;
        }

        _dialogService.Show(new ProductEditViewModel(
            product,
            _snapshot,
            _catalogService,
            _toastService,
            _dialogService.Close,
            RefreshAsync));
    }

    private void OpenProduct(BackendProductManagementItem item)
    {
        CurrentProductDetail?.Dispose();

        var product = new PosProductItemViewModel(
            id: item.ProductId.ToString("D"),
            name: item.Name,
            sku: item.Sku,
            brand: string.IsNullOrWhiteSpace(item.Brand) ? "—" : item.Brand,
            category: item.Category,
            stock: 0m,
            price: item.DefaultSalePrice,
            unit: item.BaseUnit,
            cost: 0m,
            minimumStock: item.MinimumStockLevel,
            model: item.Model ?? string.Empty,
            backendProductId: item.ProductId,
            backendProductUnitId: item.ProductUnits
                .FirstOrDefault(x => x.IsActive && x.IsDefaultSaleUnit)?.ProductUnitId,
            isSerialized: item.TrackingMode == EdgeRetails.Domain.Catalog.TrackingMode.Serialized);

        CurrentProductDetail = new ProductDetailViewModel(
            product,
            _dialogService,
            _toastService,
            CloseProductDetail,
            _inventoryService,
            _catalogService);
        IsDetailViewActive = true;
    }

    private void CloseProductDetail()
    {
        IsDetailViewActive = false;
        CurrentProductDetail?.Dispose();
        CurrentProductDetail = null;
        _ = RefreshAsync();
    }

    private async Task ToggleActiveAsync(BackendProductManagementItem product)
    {
        if (_catalogService is null)
        {
            return;
        }

        try
        {
            await _catalogService.SetProductActiveAsync(
                product.ProductId,
                product.Version,
                !product.IsActive);
            _toastService.Show(
                product.IsActive ? "Product deactivated." : "Product reactivated.",
                ToastTone.Success);
            await RefreshAsync();
        }
        catch (BackendCatalogOperationException ex)
        {
            var message = ex.Code == "concurrency.stale_product"
                ? "Product changed elsewhere. Refresh and try again."
                : ex.Message;
            _toastService.Show(message, ToastTone.Danger);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _toastService.Show($"Catalog status change failed: {ex.Message}", ToastTone.Danger);
        }
    }
}
