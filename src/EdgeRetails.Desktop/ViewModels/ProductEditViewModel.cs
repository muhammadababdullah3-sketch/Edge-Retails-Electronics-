using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Catalog;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ProductUnitEditorItemViewModel : ViewModelBase
{
    private bool _isEnabled;
    private string _factorText;
    private bool _canPurchase;
    private bool _canSell;
    private bool _canUseInThaka;
    private bool _isDefaultPurchaseUnit;
    private bool _isDefaultSaleUnit;

    public ProductUnitEditorItemViewModel(
        BackendCatalogUnit unit,
        BackendProductUnitConfiguration? current,
        bool isBaseUnit)
    {
        Unit = unit;
        IsBaseUnit = isBaseUnit;
        _isEnabled = isBaseUnit || current?.IsActive == true;
        _factorText = (isBaseUnit ? 1m : current?.FactorToBaseUnit ?? 1m)
            .ToString("0.#########", CultureInfo.InvariantCulture);
        _canPurchase = current?.CanPurchase ?? isBaseUnit;
        _canSell = current?.CanSell ?? isBaseUnit;
        _canUseInThaka = current?.CanUseInThaka ?? isBaseUnit;
        _isDefaultPurchaseUnit = current?.IsDefaultPurchaseUnit ?? isBaseUnit;
        _isDefaultSaleUnit = current?.IsDefaultSaleUnit ?? isBaseUnit;
    }

    public BackendCatalogUnit Unit { get; }
    public bool IsBaseUnit { get; }
    public string DisplayName => Unit.DisplayName;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, IsBaseUnit || value);
    }

    public string FactorText
    {
        get => _factorText;
        set => SetProperty(ref _factorText, value ?? string.Empty);
    }

    public bool CanPurchase
    {
        get => _canPurchase;
        set => SetProperty(ref _canPurchase, value);
    }

    public bool CanSell
    {
        get => _canSell;
        set => SetProperty(ref _canSell, value);
    }

    public bool CanUseInThaka
    {
        get => _canUseInThaka;
        set => SetProperty(ref _canUseInThaka, value);
    }

    public bool IsDefaultPurchaseUnit
    {
        get => _isDefaultPurchaseUnit;
        set => SetProperty(ref _isDefaultPurchaseUnit, value);
    }

    public bool IsDefaultSaleUnit
    {
        get => _isDefaultSaleUnit;
        set => SetProperty(ref _isDefaultSaleUnit, value);
    }
}

public sealed class SupplierLinkEditorItemViewModel : ViewModelBase
{
    private bool _isLinked;

    public SupplierLinkEditorItemViewModel(
        BackendSupplierOption supplier,
        bool isLinked)
    {
        Supplier = supplier;
        _isLinked = isLinked;
    }

    public BackendSupplierOption Supplier { get; }
    public string Name => Supplier.Name;

    public bool IsLinked
    {
        get => _isLinked;
        set => SetProperty(ref _isLinked, value);
    }
}

public sealed class ProductEditViewModel : ViewModelBase
{
    private readonly BackendProductManagementItem? _product;
    private readonly BackendProductManagementSnapshot _snapshot;
    private readonly IBackendProductManagementService _service;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly Func<Task> _saved;
    private BackendCatalogCategory? _selectedCategory;
    private BackendCatalogUnit? _selectedBaseUnit;
    private TrackingMode _selectedTrackingMode;
    private bool _serialTrackingEnabled;
    private bool _imeiTrackingEnabled;
    private bool _isSaving;
    private string? _validationMessage;

    public ProductEditViewModel(
        BackendProductManagementItem? product,
        BackendProductManagementSnapshot snapshot,
        IBackendProductManagementService service,
        IToastService toastService,
        Action close,
        Func<Task> saved)
    {
        _product = product;
        _snapshot = snapshot;
        _service = service;
        _toastService = toastService;
        _close = close;
        _saved = saved;

        Name = product?.Name ?? string.Empty;
        Sku = product?.Sku ?? string.Empty;
        Brand = product?.Brand ?? string.Empty;
        Model = product?.Model ?? string.Empty;
        _selectedCategory = product?.CategoryId is Guid categoryId
            ? snapshot.Categories.FirstOrDefault(x => x.Id == categoryId)
            : null;
        _selectedBaseUnit = product is null
            ? snapshot.Units.FirstOrDefault(x => x.IsActive)
            : snapshot.Units.FirstOrDefault(x => x.Id == product.BaseUnitId);
        _selectedTrackingMode = product?.TrackingMode ?? TrackingMode.Quantity;
        _serialTrackingEnabled = product?.SerialTrackingEnabled ?? false;
        _imeiTrackingEnabled = product?.ImeiTrackingEnabled ?? false;

        ReferencePurchaseCostText = product?.ReferencePurchaseCost?.ToString(
            "0.######",
            CultureInfo.InvariantCulture) ?? string.Empty;
        SalePriceText = (product?.DefaultSalePrice ?? 0m)
            .ToString("0.##", CultureInfo.InvariantCulture);
        MinimumStockText = (product?.MinimumStockLevel ?? 0m)
            .ToString("0.##", CultureInfo.InvariantCulture);
        WarrantyMonthsText = (product?.DefaultWarrantyMonths ?? 0)
            .ToString(CultureInfo.InvariantCulture);
        AttributesJson = product?.AttributesJson ?? string.Empty;
        AttributesSchemaVersionText = (product?.AttributesSchemaVersion ?? 1)
            .ToString(CultureInfo.InvariantCulture);

        Categories = snapshot.Categories
            .Where(x => x.IsActive || x.Id == product?.CategoryId)
            .OrderBy(x => x.Name)
            .ToArray();
        Units = snapshot.Units
            .Where(x => x.IsActive || x.Id == product?.BaseUnitId)
            .OrderBy(x => x.Name)
            .ToArray();
        TrackingModes = Enum.GetValues<TrackingMode>();

        UnitConfigurations = [];
        BuildUnitConfigurationRows();

        SupplierLinks = snapshot.Suppliers
            .OrderBy(x => x.Name)
            .Select(supplier => new SupplierLinkEditorItemViewModel(
                supplier,
                product?.SupplierProducts.Any(
                    x => x.SupplierId == supplier.Id && x.IsActive) == true))
            .ToList();

        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsSaving);
        CancelCommand = new RelayCommand(close);
    }

    public bool IsEdit => _product is not null;
    public string Title => IsEdit ? "Edit Product" : "Add Product";
    public bool IsBaseUnitEditable => !IsEdit;
    public string Name { get; set; }
    public string Sku { get; set; }
    public string Brand { get; set; }
    public string Model { get; set; }
    public IReadOnlyList<BackendCatalogCategory> Categories { get; }
    public IReadOnlyList<BackendCatalogUnit> Units { get; }
    public IReadOnlyList<TrackingMode> TrackingModes { get; }
    public List<ProductUnitEditorItemViewModel> UnitConfigurations { get; }
    public List<SupplierLinkEditorItemViewModel> SupplierLinks { get; }
    public string ReferencePurchaseCostText { get; set; }
    public string SalePriceText { get; set; }
    public string MinimumStockText { get; set; }
    public string WarrantyMonthsText { get; set; }
    public string AttributesJson { get; set; }
    public string AttributesSchemaVersionText { get; set; }

    public BackendCatalogCategory? SelectedCategory
    {
        get => _selectedCategory;
        set => SetProperty(ref _selectedCategory, value);
    }

    public BackendCatalogUnit? SelectedBaseUnit
    {
        get => _selectedBaseUnit;
        set
        {
            if (SetProperty(ref _selectedBaseUnit, value))
            {
                BuildUnitConfigurationRows();
            }
        }
    }

    public TrackingMode SelectedTrackingMode
    {
        get => _selectedTrackingMode;
        set
        {
            if (SetProperty(ref _selectedTrackingMode, value))
            {
                OnPropertyChanged(nameof(IsSerialized));
                if (!IsSerialized)
                {
                    SerialTrackingEnabled = false;
                    ImeiTrackingEnabled = false;
                }
            }
        }
    }

    public bool IsSerialized => SelectedTrackingMode == TrackingMode.Serialized;

    public bool SerialTrackingEnabled
    {
        get => _serialTrackingEnabled;
        set => SetProperty(ref _serialTrackingEnabled, value);
    }

    public bool ImeiTrackingEnabled
    {
        get => _imeiTrackingEnabled;
        set => SetProperty(ref _imeiTrackingEnabled, value);
    }

    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (SetProperty(ref _isSaving, value) &&
                SaveCommand is RelayCommand command)
            {
                command.NotifyCanExecuteChanged();
            }
        }
    }

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private void BuildUnitConfigurationRows()
    {
        if (_selectedBaseUnit is null)
        {
            UnitConfigurations.Clear();
            OnPropertyChanged(nameof(UnitConfigurations));
            return;
        }

        var existing = _product?.ProductUnits.ToDictionary(x => x.UnitId)
            ?? new Dictionary<Guid, BackendProductUnitConfiguration>();

        UnitConfigurations.Clear();
        foreach (var unit in _snapshot.Units.Where(x => x.IsActive || existing.ContainsKey(x.Id)))
        {
            existing.TryGetValue(unit.Id, out var current);
            UnitConfigurations.Add(new ProductUnitEditorItemViewModel(
                unit,
                current,
                unit.Id == _selectedBaseUnit.Id));
        }

        OnPropertyChanged(nameof(UnitConfigurations));
    }

    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        ValidationMessage = null;

        if (string.IsNullOrWhiteSpace(Name) ||
            string.IsNullOrWhiteSpace(Sku) ||
            SelectedBaseUnit is null)
        {
            ValidationMessage = "Product name, SKU and base unit are required.";
            return;
        }

        var existing = await _service.GetProductBySkuAsync(Sku.Trim());
        if (existing is not null && existing.ProductId != _product?.ProductId)
        {
            ValidationMessage = "Another product already uses this SKU.";
            return;
        }

        if (!TryParseOptionalDecimal(ReferencePurchaseCostText, out var referenceCost) ||
            !decimal.TryParse(
                SalePriceText,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var salePrice) ||
            !decimal.TryParse(
                MinimumStockText,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var minimumStock) ||
            !int.TryParse(WarrantyMonthsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var warrantyMonths) ||
            !int.TryParse(AttributesSchemaVersionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attributesSchemaVersion))
        {
            ValidationMessage = "Price, stock threshold, warranty and schema values must be valid numbers.";
            return;
        }

        var unitConfigurations = new List<BackendProductUnitConfiguration>();
        foreach (var row in UnitConfigurations.Where(x => x.IsEnabled))
        {
            if (!decimal.TryParse(
                    row.FactorText,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out var factor) ||
                factor <= 0m)
            {
                ValidationMessage = $"Conversion factor for {row.DisplayName} must be greater than zero.";
                return;
            }

            unitConfigurations.Add(new BackendProductUnitConfiguration(
                null,
                row.Unit.Id,
                row.Unit.Name,
                row.Unit.Symbol,
                row.IsBaseUnit ? 1m : factor,
                row.CanPurchase,
                row.CanSell,
                row.CanUseInThaka,
                row.IsDefaultPurchaseUnit,
                row.IsDefaultSaleUnit,
                true));
        }

        var linkedSupplierIds = SupplierLinks
            .Where(x => x.IsLinked)
            .Select(x => x.Supplier.Id)
            .ToArray();

        var request = new BackendProductCatalogRequest(
            Name,
            Sku,
            string.IsNullOrWhiteSpace(Brand) ? null : Brand,
            string.IsNullOrWhiteSpace(Model) ? null : Model,
            SelectedCategory?.Id,
            SelectedBaseUnit.Id,
            SelectedTrackingMode,
            IsSerialized && SerialTrackingEnabled,
            IsSerialized && ImeiTrackingEnabled,
            referenceCost,
            salePrice,
            minimumStock,
            warrantyMonths,
            string.IsNullOrWhiteSpace(AttributesJson) ? null : AttributesJson,
            attributesSchemaVersion);

        IsSaving = true;
        try
        {
            if (_product is null)
            {
                await _service.CreateProductAsync(
                    request,
                    unitConfigurations,
                    linkedSupplierIds);
                _toastService.Show("Product created.", ToastTone.Success);
            }
            else
            {
                await _service.UpdateProductAsync(
                    _product.ProductId,
                    _product.Version,
                    request,
                    unitConfigurations,
                    linkedSupplierIds);
                _toastService.Show("Product updated.", ToastTone.Success);
            }

            await _saved();
            _close();
        }
        catch (BackendCatalogOperationException ex)
        {
            ValidationMessage = ex.Code switch
            {
                "catalog.sku_duplicate" => "This SKU is already assigned to another product.",
                "concurrency.stale_product" => "This product changed elsewhere. Refresh and reopen it before saving.",
                "catalog.tracking_policy_locked" => "Tracking policy cannot be changed after stock or inventory history exists.",
                "concurrency.stale_supplier_product" => "A supplier link changed elsewhere. Refresh and reopen the product.",
                "authorization.denied" => "You do not have permission to manage products.",
                _ => ex.Message
            };
            _toastService.Show(ValidationMessage, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            ValidationMessage = $"Catalog save failed: {ex.Message}";
            _toastService.Show(ValidationMessage, ToastTone.Danger);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private static bool TryParseOptionalDecimal(string text, out decimal? value)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            value = null;
            return true;
        }

        if (decimal.TryParse(
            text,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var parsed))
        {
            value = parsed;
            return true;
        }

        value = null;
        return false;
    }
}
