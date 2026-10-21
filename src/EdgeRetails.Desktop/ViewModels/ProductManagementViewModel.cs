using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Application.Features.Catalog;
using EdgeRetails.Domain.Catalog;
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
    private CancellationTokenSource? _refreshCts;
    private long _productPageSearchVersion;
    private string? _nextProductName;
    private Guid? _nextProductId;
    public bool HasMoreProducts => _nextProductId.HasValue;
    public ICommand MoreProductsCommand { get; }
    private const int ProductPageSize = 200;

    public ProductManagementViewModel()
        : this(null!, null!, null, null)
    {
    }

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
        ManageCompaniesCommand = new RelayCommand(
            () => OpenCatalogReferenceManager(CatalogReferenceKind.Company),
            CanMutate);
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
        MoreProductsCommand = new RelayCommand(() => _ = LoadMoreProductsAsync());
        CloseDetailCommand = new RelayCommand(CloseProductDetail);
        FilterBySelectedTaxonomyCommand = new RelayCommand(FilterBySelectedTaxonomy, () => HasSelectedTaxonomyNode);

        InitializeTaxonomyTree();
        _ = RefreshAsync();
    }

    public ObservableCollection<BackendProductManagementItem> FilteredProducts { get; }
    public ObservableCollection<string> Categories { get; }
    public IReadOnlyList<string> StatusFilters { get; }
    public ObservableCollection<TaxonomyTreeNodeViewModel> TaxonomyTree { get; } = new();

    private TaxonomyTreeNodeViewModel? _selectedTaxonomyNode;
    public TaxonomyTreeNodeViewModel? SelectedTaxonomyNode
    {
        get => _selectedTaxonomyNode;
        set
        {
            if (SetProperty(ref _selectedTaxonomyNode, value))
            {
                OnPropertyChanged(nameof(SelectedTaxonomyNodeDisplay));
                OnPropertyChanged(nameof(HasSelectedTaxonomyNode));
                OnPropertyChanged(nameof(HasTaxonomyRedirect));
                OnPropertyChanged(nameof(TaxonomyRedirectNotice));
                ((RelayCommand)FilterBySelectedTaxonomyCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedTaxonomyNode => _selectedTaxonomyNode is not null;

    public string? SelectedTaxonomyNodeDisplay => _selectedTaxonomyNode is not null
        ? $"{_selectedTaxonomyNode.Level}: {_selectedTaxonomyNode.Code} — {_selectedTaxonomyNode.Name}"
        : "No node selected";

    public bool HasTaxonomyRedirect => _selectedTaxonomyNode?.IsDeprecated == true;

    public string? TaxonomyRedirectNotice => _selectedTaxonomyNode?.IsDeprecated == true
        ? $"REDIRECT NOTICE: Class {_selectedTaxonomyNode.Code} is deprecated. Active canonical target: {_selectedTaxonomyNode.RedirectTargetCode}"
        : null;

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public ICommand FilterBySelectedTaxonomyCommand { get; }

    public void FilterBySelectedTaxonomy()
    {
        if (_selectedTaxonomyNode is not null)
        {
            var targetCode = !string.IsNullOrWhiteSpace(_selectedTaxonomyNode.RedirectTargetCode)
                ? _selectedTaxonomyNode.RedirectTargetCode
                : _selectedTaxonomyNode.Code;

            SearchText = targetCode;
            SelectedTabIndex = 0;
            ApplyFilters();
        }
    }

    public void InitializeTaxonomyTree()
    {
        if (TaxonomyTree.Count > 0)
        {
            return;
        }

        var divisions = TaxonomyV3Store.Divisions;
        var departments = TaxonomyV3Store.Departments;
        var classes = TaxonomyV3Store.ProductClasses;

        var deptLookup = departments.GroupBy(d => d.DivisionCode)
            .ToDictionary(g => g.Key, g => g.ToList());
        var classLookup = classes.GroupBy(c => c.DepartmentCode)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var div in divisions)
        {
            var divNode = new TaxonomyTreeNodeViewModel(
                div.Code,
                div.Name,
                "Division");

            if (deptLookup.TryGetValue(div.Code, out var depts))
            {
                foreach (var dept in depts)
                {
                    var deptNode = new TaxonomyTreeNodeViewModel(
                        dept.Code,
                        dept.Name,
                        "Department");

                    if (classLookup.TryGetValue(dept.Code, out var clsList))
                    {
                        foreach (var cls in clsList)
                        {
                            var clsNode = new TaxonomyTreeNodeViewModel(
                                cls.ClassRef,
                                cls.ClassName,
                                "Class",
                                isSellable: !cls.IsNavigationOnly,
                                isDeprecated: cls.IsRedirect,
                                redirectTargetCode: cls.CanonicalClassRef);

                            deptNode.Children.Add(clsNode);
                        }
                    }

                    divNode.Children.Add(deptNode);
                }
            }

            TaxonomyTree.Add(divNode);
        }
    }

    public static (bool IsValid, string? ErrorMessage) ValidateAttributeValue(AttributeProfile profile, string? rawValue)
    {
        if (profile is null)
        {
            return (false, "Profile is null.");
        }

        var value = rawValue?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            if (string.Equals(profile.EffectiveRequiredness, "REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"Attribute '{profile.AttributeName}' is required.");
            }
            return (true, null);
        }

        if (string.Equals(profile.ValueType, "NUMBER", StringComparison.OrdinalIgnoreCase))
        {
            if (!decimal.TryParse(value, out var numericVal))
            {
                return (false, $"Attribute '{profile.AttributeName}' must be a valid number.");
            }

            if (!string.IsNullOrWhiteSpace(profile.NumericMin) && decimal.TryParse(profile.NumericMin, out var minVal))
            {
                if (numericVal < minVal)
                {
                    return (false, $"Attribute '{profile.AttributeName}' value {numericVal} is less than minimum allowed bound {minVal}.");
                }
            }

            if (!string.IsNullOrWhiteSpace(profile.NumericMax) && decimal.TryParse(profile.NumericMax, out var maxVal))
            {
                if (numericVal > maxVal)
                {
                    return (false, $"Attribute '{profile.AttributeName}' value {numericVal} exceeds maximum allowed bound {maxVal}.");
                }
            }

            return (true, null);
        }

        if (string.Equals(profile.ValueType, "ENUM", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(profile.EnumSuggestedValues))
            {
                var allowed = profile.EnumSuggestedValues.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (!allowed.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase)))
                {
                    return (false, $"Attribute '{profile.AttributeName}' value '{value}' is not among allowed choices: {profile.EnumSuggestedValues}.");
                }
            }

            return (true, null);
        }

        if (string.Equals(profile.ValueType, "BOOLEAN", StringComparison.OrdinalIgnoreCase))
        {
            if (!bool.TryParse(value, out _))
            {
                return (false, $"Attribute '{profile.AttributeName}' must be true or false.");
            }

            return (true, null);
        }

        return (true, null);
    }

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
                (ManageCompaniesCommand as RelayCommand)?.NotifyCanExecuteChanged();
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
    public ICommand ManageCompaniesCommand { get; }
    public ICommand ManageCategoriesCommand { get; }
    public ICommand ManageUnitsCommand { get; }
    public ICommand EditProductCommand { get; }
    public ICommand OpenProductCommand { get; }
    public ICommand ToggleActiveCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CloseDetailCommand { get; }

    public void Dispose()
    {
        Interlocked.Increment(ref _productPageSearchVersion);
        Interlocked.Exchange(ref _refreshCts, null)?.Cancel();
        var searchCts = Interlocked.Exchange(ref _productPageSearchCts, null);
        searchCts?.Cancel();
        searchCts?.Dispose();
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

        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _refreshCts, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }

        var refreshGeneration = Volatile.Read(ref _productPageSearchVersion);
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            _snapshot = await _catalogService.GetSnapshotAsync(cancellationToken: cts.Token);
            RebuildCategoryFilter();
            await LoadCurrentProductPageAsync(cts.Token, refreshGeneration);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (BackendCatalogOperationException ex)
        {
            ErrorMessage = DesktopErrorPresentation.ForException(
                ex,
                "The catalog could not be loaded. Refresh and try again.");
        }
        catch (Exception ex)
        {
            ErrorMessage = DesktopErrorPresentation.ForException(
                ex,
                "The catalog could not be loaded. Refresh and try again.");
        }
        finally
        {
            Interlocked.CompareExchange(ref _refreshCts, null, cts);
            cts.Dispose();
            IsLoading = false;
            if (refreshGeneration != Volatile.Read(ref _productPageSearchVersion))
            {
                ScheduleBackendPageRefresh();
            }
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
            var matchingTaxonomyCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var matchingClassNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var cls = TaxonomyV3Store.ProductClasses.FirstOrDefault(c =>
                string.Equals(c.ClassRef, term, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.CanonicalClassRef, term, StringComparison.OrdinalIgnoreCase));

            if (cls is not null)
            {
                matchingClassNames.Add(cls.ClassName);
                var dept = TaxonomyV3Store.Departments.FirstOrDefault(d => string.Equals(d.Code, cls.DepartmentCode, StringComparison.OrdinalIgnoreCase));
                if (dept is not null)
                {
                    matchingTaxonomyCategories.Add(dept.Name);
                }
            }

            var deptByCode = TaxonomyV3Store.Departments.FirstOrDefault(d => string.Equals(d.Code, term, StringComparison.OrdinalIgnoreCase));
            if (deptByCode is not null)
            {
                matchingTaxonomyCategories.Add(deptByCode.Name);
            }

            products = products.Where(x =>
                x.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                x.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (x.Brand?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (x.Model?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                x.Category.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                matchingTaxonomyCategories.Contains(x.Category) ||
                matchingClassNames.Any(cn => x.Name.Contains(cn, StringComparison.OrdinalIgnoreCase) || (x.AttributesJson?.Contains(cn, StringComparison.OrdinalIgnoreCase) ?? false)) ||
                (x.AttributesJson?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        FilteredProducts.Clear();
        foreach (var product in products.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Sku, StringComparer.OrdinalIgnoreCase))
        {
            FilteredProducts.Add(product);
        }

        NotifyState();
    }

    internal void SetSnapshotForTesting(BackendProductManagementSnapshot snapshot)
    {
        _snapshot = snapshot;
        RebuildCategoryFilter();
        ApplyFilters();
    }

    private void ScheduleBackendPageRefresh()
    {
        if (_catalogService is null) { ApplyFilters(); return; }
        var version = Interlocked.Increment(ref _productPageSearchVersion);
        _nextProductId = null;
        _nextProductName = null;
        OnPropertyChanged(nameof(HasMoreProducts));
        _productPageSearchCts?.Cancel();
        if (IsLoading)
        {
            return;
        }

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

            await LoadCurrentProductPageAsync(cts.Token, version);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (version != Volatile.Read(ref _productPageSearchVersion))
            {
                return;
            }
            ErrorMessage = DesktopErrorPresentation.ForException(
                ex,
                "Catalog search failed. Check the connection and try again.");
            NotifyState();
        }
    }

    private async Task LoadCurrentProductPageAsync(CancellationToken cancellationToken, long generation)
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

        if (cancellationToken.IsCancellationRequested || generation != Volatile.Read(ref _productPageSearchVersion))
        {
            return;
        }

        FilteredProducts.Clear();
        foreach (var product in rows)
        {
            FilteredProducts.Add(product);
        }

        _nextProductId = rows.Count == ProductPageSize ? rows[^1].ProductId : null;
        _nextProductName = rows.Count == ProductPageSize ? rows[^1].Name : null;
        OnPropertyChanged(nameof(HasMoreProducts));
        NotifyState();
    }

    private async Task LoadMoreProductsAsync()
    {
        if (_catalogService is null || !HasMoreProducts)
        {
            return;
        }
        var generation = Volatile.Read(ref _productPageSearchVersion);
        var cursor = _nextProductId;
        try
        {
            var categoryId = _snapshot?.Categories.FirstOrDefault(x => x.Name == SelectedCategory)?.Id;
            var isActive = SelectedStatus switch { "Active" => true, "Inactive" => false, _ => (bool?)null };
            var rows = await _catalogService.GetProductsPageAsync(SearchText, isActive, categoryId, ProductPageSize,
                _nextProductName, cursor, _productPageSearchCts?.Token ?? default);
            if (generation != Volatile.Read(ref _productPageSearchVersion) || cursor != _nextProductId)
            {
                return;
            }
            foreach (var product in rows)
            {
                if (!FilteredProducts.Any(x => x.ProductId == product.ProductId))
                {
                    FilteredProducts.Add(product);
                }
            }
            _nextProductId = rows.Count == ProductPageSize ? rows[^1].ProductId : null;
            _nextProductName = rows.Count == ProductPageSize ? rows[^1].Name : null;
            OnPropertyChanged(nameof(HasMoreProducts));
            NotifyState();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _productPageSearchVersion))
            {
                _toastService.Show(DesktopErrorPresentation.ForException(ex, "Catalog page could not be loaded."), ToastTone.Danger);
            }
        }
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
            var message = DesktopErrorPresentation.ForException(
                ex,
                "Product status could not be changed. Refresh and try again.");
            _toastService.Show(message, ToastTone.Danger);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "Catalog status change failed. Check the connection and try again."),
                ToastTone.Danger);
        }
    }
}

public sealed class TaxonomyTreeNodeViewModel : ViewModelBase
{
    public string Code { get; }
    public string Name { get; }
    public string Level { get; }
    public bool IsSellable { get; }
    public bool IsDeprecated { get; }
    public string? RedirectTargetCode { get; }

    public string DisplayText => IsDeprecated
        ? $"{Code} - {Name} [REDIRECT -> {RedirectTargetCode}]"
        : (!IsSellable ? $"{Code} - {Name} [NON-SELLABLE]" : $"{Code} - {Name}");

    public ObservableCollection<TaxonomyTreeNodeViewModel> Children { get; } = new();

    public TaxonomyTreeNodeViewModel(
        string code,
        string name,
        string level,
        bool isSellable = true,
        bool isDeprecated = false,
        string? redirectTargetCode = null)
    {
        Code = code;
        Name = name;
        Level = level;
        IsSellable = isSellable;
        IsDeprecated = isDeprecated;
        RedirectTargetCode = redirectTargetCode;
    }
}
