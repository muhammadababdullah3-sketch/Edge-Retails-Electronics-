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
    public string Name => $"{Supplier.DisplayName} ({Supplier.Id:D})";

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
    private BackendCatalogCompany? _selectedCompany;
    private BackendCatalogUnit? _selectedBaseUnit;
    private TrackingMode _selectedTrackingMode;
    private bool _serialTrackingEnabled;
    private bool _imeiTrackingEnabled;
    private bool _isSaving;
    private string? _validationMessage;
    private string _modelCode = string.Empty;
    private bool _isModelCodeManuallyEdited;
    private bool _isAutoSuggestingModelCode;
    private string _supplierSearchText = string.Empty;
    private CancellationTokenSource? _supplierSearchCancellation;
    private long _supplierGeneration;
    private string? _nextSupplierName;
    private Guid? _nextSupplierId;

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

        _name = product?.Name ?? string.Empty;
        _sku = product?.Sku ?? string.Empty;
        _brand = product?.Brand ?? string.Empty;
        _model = product?.Model ?? string.Empty;
        _modelCode = product?.ModelCode ?? string.Empty;
        _selectedCompany = product?.CompanyId is Guid companyId
            ? snapshot.Companies.FirstOrDefault(x => x.Id == companyId)
            : null;
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

        Companies = snapshot.Companies
            .Where(x => x.IsActive || x.Id == product?.CompanyId)
            .OrderBy(x => x.Name)
            .ToArray();
        Categories = snapshot.Categories
            .Where(x => x.IsActive || x.Id == product?.CategoryId)
            .OrderBy(x => x.Name)
            .ToArray();
        Units = snapshot.Units
            .Where(x => x.IsActive || x.Id == product?.BaseUnitId)
            .OrderBy(x => x.Name)
            .ToArray();
        TrackingModes = new[]
        {
            TrackingMode.Quantity,
            TrackingMode.Length,
            TrackingMode.IndividualPiece,
            TrackingMode.Container,
            TrackingMode.Serialized
        };

        UnitConfigurations = [];
        BuildUnitConfigurationRows();

        SupplierLinks = new System.Collections.ObjectModel.ObservableCollection<SupplierLinkEditorItemViewModel>(snapshot.Suppliers
            .OrderBy(x => x.Name)
            .Select(supplier => new SupplierLinkEditorItemViewModel(
                supplier,
                product?.SupplierProducts.Any(
                    x => x.SupplierId == supplier.Id && x.IsActive) == true))
            .ToList());
        // Existing links are authoritative even when their suppliers are inactive/off-page.
        foreach (var link in product?.SupplierProducts ?? [])
        {
            if (!SupplierLinks.Any(x => x.Supplier.Id == link.SupplierId))
            {
                SupplierLinks.Add(new(new BackendSupplierOption(link.SupplierId, link.SupplierName), link.IsActive));
            }
        }

        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => !IsSaving);
        CancelCommand = new RelayCommand(close);
        LoadMoreSuppliersCommand = new RelayCommand(() => _ = SearchSuppliersAsync(true), () => HasMoreSuppliers);
        if (_service is IBackendSupplierLookupService)
        {
            _ = SearchSuppliersAsync(false);
        }
    }

    public bool IsEdit => _product is not null;
    public string Title => IsEdit ? "Edit Product" : "Add Product";
    public bool IsBaseUnitEditable => !IsEdit;
    public bool IsSkuReadOnly => IsEdit;

    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                AutoSuggestSkuIfApplicable();
            }
        }
    }

    private string _sku = string.Empty;
    public string Sku
    {
        get => _sku;
        set
        {
            if (SetProperty(ref _sku, value))
            {
                if (!_isAutoSuggesting)
                {
                    _isSkuManuallyEdited = true;
                }
            }
        }
    }

    private string _brand = string.Empty;
    public string Brand
    {
        get => _brand;
        set
        {
            if (SetProperty(ref _brand, value))
            {
                AutoSuggestSkuIfApplicable();
            }
        }
    }

    private string _model = string.Empty;
    public string Model
    {
        get => _model;
        set
        {
            if (SetProperty(ref _model, value))
            {
                if (!IsEdit && !_isModelCodeManuallyEdited)
                {
                    _isAutoSuggestingModelCode = true;
                    try
                    {
                        ModelCode = !string.IsNullOrWhiteSpace(_model)
                            ? TraceabilityCodeRules.SuggestModelCode(_model)
                            : string.Empty;
                    }
                    finally
                    {
                        _isAutoSuggestingModelCode = false;
                    }
                }
                AutoSuggestSkuIfApplicable();
            }
        }
    }

    public string ModelCode
    {
        get => _modelCode;
        set
        {
            if (SetProperty(ref _modelCode, value))
            {
                if (!_isAutoSuggestingModelCode)
                {
                    _isModelCodeManuallyEdited = true;
                }
                AutoSuggestSkuIfApplicable();
            }
        }
    }

    private bool _isSkuManuallyEdited;
    private bool _isAutoSuggesting;

    private void AutoSuggestSkuIfApplicable()
    {
        if (!IsEdit && !_isSkuManuallyEdited)
        {
            _isAutoSuggesting = true;
            try
            {
                var compCode = SelectedCompany?.Code;
                var catSymbol = SelectedCategory?.IdentitySymbol;
                var effectiveModelCode = !string.IsNullOrWhiteSpace(ModelCode)
                    ? ModelCode
                    : (!string.IsNullOrWhiteSpace(Model) ? TraceabilityCodeRules.SuggestModelCode(Model) : null);

                if (!string.IsNullOrWhiteSpace(compCode) && !string.IsNullOrWhiteSpace(catSymbol) && !string.IsNullOrWhiteSpace(effectiveModelCode))
                {
                    Sku = TraceabilityCodeRules.BuildProductCode(compCode, catSymbol, effectiveModelCode);
                }
                else
                {
                    Sku = TraceabilityCodeRules.SuggestProductCode(Brand, Name, Model, SelectedCategory?.Name);
                }
            }
            finally
            {
                _isAutoSuggesting = false;
            }
        }
    }

    public IReadOnlyList<BackendCatalogCompany> Companies { get; }
    public IReadOnlyList<BackendCatalogCategory> Categories { get; }
    public IReadOnlyList<BackendCatalogUnit> Units { get; }
    public IReadOnlyList<TrackingMode> TrackingModes { get; }
    public List<ProductUnitEditorItemViewModel> UnitConfigurations { get; }
    public System.Collections.ObjectModel.ObservableCollection<SupplierLinkEditorItemViewModel> SupplierLinks { get; }
    public ICommand LoadMoreSuppliersCommand { get; }
    public bool HasMoreSuppliers => _nextSupplierId.HasValue;
    public string SupplierSearchText
    {
        get => _supplierSearchText;
        set { if (SetProperty(ref _supplierSearchText, value ?? string.Empty))
            {
                _ = SearchSuppliersAsync(false);
            }
        }
    }

    public async Task SearchSuppliersAsync(bool append)
    {
        if (_service is not IBackendSupplierLookupService lookup)
        {
            return;
        }

        if (append && !HasMoreSuppliers)
        {
            return;
        }

        if (!append)
        {
            _nextSupplierName = null;
            _nextSupplierId = null;
            OnPropertyChanged(nameof(HasMoreSuppliers));
            ((RelayCommand)LoadMoreSuppliersCommand).NotifyCanExecuteChanged();
        }
        _supplierSearchCancellation?.Cancel();
        var source = new CancellationTokenSource();
        _supplierSearchCancellation = source;
        var generation = ++_supplierGeneration;
        try
        {
            if (!append)
            {
                await Task.Delay(250, source.Token);
            }

            var page = await lookup.GetSupplierPageAsync(SupplierSearchText, 50,
                append ? _nextSupplierName : null, append ? _nextSupplierId : null, source.Token);
            if (generation != _supplierGeneration || source.IsCancellationRequested)
            {
                return;
            }

            if (!append)
            {
                foreach (var row in SupplierLinks.Where(x => !x.IsLinked).ToArray())
                {
                    SupplierLinks.Remove(row);
                }
            }

            foreach (var supplier in page.Items)
            {
                if (!SupplierLinks.Any(x => x.Supplier.Id == supplier.Id))
                {
                    SupplierLinks.Add(new(supplier, false));
                }
            }

            _nextSupplierName = page.NextName;
            _nextSupplierId = page.NextSupplierId;
            OnPropertyChanged(nameof(HasMoreSuppliers));
            ((RelayCommand)LoadMoreSuppliersCommand).NotifyCanExecuteChanged();
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == _supplierGeneration)
            {
                ValidationMessage = DesktopErrorPresentation.ForException(ex, "Supplier search could not be loaded.");
            }
        }
        finally { source.Dispose(); if (ReferenceEquals(_supplierSearchCancellation, source))
            {
                _supplierSearchCancellation = null;
            }
        }
    }
    public string ReferencePurchaseCostText { get; set; }
    public string SalePriceText { get; set; }
    public string MinimumStockText { get; set; }
    public string WarrantyMonthsText { get; set; }
    public string AttributesJson { get; set; }
    public string AttributesSchemaVersionText { get; set; }

    public BackendCatalogCompany? SelectedCompany
    {
        get => _selectedCompany;
        set
        {
            if (SetProperty(ref _selectedCompany, value))
            {
                if (string.IsNullOrWhiteSpace(Brand) && value is not null)
                {
                    Brand = value.Name;
                }
                AutoSuggestSkuIfApplicable();
            }
        }
    }

    public BackendCatalogCategory? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                AutoSuggestSkuIfApplicable();
            }
        }
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
                if (value == TrackingMode.Serialized)
                {
                    if (!SerialTrackingEnabled && !ImeiTrackingEnabled)
                    {
                        SerialTrackingEnabled = true;
                    }
                }
                else if (value is TrackingMode.Quantity or TrackingMode.Length)
                {
                    SerialTrackingEnabled = false;
                    ImeiTrackingEnabled = false;
                }
            }
        }
    }

    public bool IsSerialized => SelectedTrackingMode is TrackingMode.Serialized or TrackingMode.IndividualPiece or TrackingMode.Container;

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
            SelectedBaseUnit is null ||
            (!IsEdit && SelectedCategory is null))
        {
            ValidationMessage = "Product name, category and base unit are required.";
            return;
        }

        if (string.IsNullOrWhiteSpace(Sku))
        {
            var compCode = SelectedCompany?.Code;
            var catSymbol = SelectedCategory?.IdentitySymbol;
            var effectiveModelCode = !string.IsNullOrWhiteSpace(ModelCode)
                ? ModelCode
                : (!string.IsNullOrWhiteSpace(Model) ? TraceabilityCodeRules.SuggestModelCode(Model) : null);

            if (!string.IsNullOrWhiteSpace(compCode) && !string.IsNullOrWhiteSpace(catSymbol) && !string.IsNullOrWhiteSpace(effectiveModelCode))
            {
                Sku = TraceabilityCodeRules.BuildProductCode(compCode, catSymbol, effectiveModelCode);
            }
            else
            {
                Sku = TraceabilityCodeRules.SuggestProductCode(Brand, Name, Model, SelectedCategory?.Name);
            }
        }

        if (!string.IsNullOrWhiteSpace(Sku))
        {
            var existing = await _service.GetProductBySkuAsync(Sku.Trim());
            if (existing is not null && existing.ProductId != _product?.ProductId)
            {
                ValidationMessage = "Another product already uses this SKU.";
                return;
            }
        }

        if (SelectedTrackingMode == TrackingMode.Serialized && !SerialTrackingEnabled && !ImeiTrackingEnabled)
        {
            ValidationMessage = "Serialized products require serial and/or IMEI tracking enabled.";
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
            attributesSchemaVersion,
            SelectedCompany?.Id,
            string.IsNullOrWhiteSpace(ModelCode) ? null : ModelCode);

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

            // The mutation has committed. Refresh failure must never invite another save.
            try { await _saved(); }
            catch (Exception) { _toastService.Show("Product saved. Refresh the catalog to display the latest details.", ToastTone.Warning); }
            _close();
        }
        catch (BackendCatalogOperationException ex)
        {
            ValidationMessage = ex.Code switch
            {
                "catalog.sku_duplicate" => "This SKU is already assigned to another product.",
                "catalog.sku_immutable" => "Product SKU is permanent and cannot be modified once stock or inventory history exists.",
                "catalog.model_code_immutable" => "Product ModelCode is permanent and cannot be modified once stock or inventory history exists.",
                "catalog.company_immutable" => "Product company cannot be modified once stock or inventory history exists.",
                "catalog.category_immutable" => "Product category cannot be modified once stock or inventory history exists.",
                "catalog.category_required" => "Category is required for product creation.",
                "catalog.category_unavailable" => "Selected category is unavailable.",
                "catalog.company_unavailable" => "Selected company is unavailable.",
                "catalog.base_unit_unavailable" => "Selected base unit is unavailable.",
                "concurrency.stale_product" => "This product changed elsewhere. Refresh and reopen it before saving.",
                "catalog.tracking_policy_locked" => "Tracking policy cannot be changed after stock or inventory history exists.",
                "concurrency.stale_supplier_product" => "A supplier link changed elsewhere. Refresh and reopen the product.",
                "authorization.denied" => "You do not have permission to manage products.",
                _ => DesktopErrorPresentation.ForException(
                    ex,
                    "Catalog save was rejected.")
            };
            _toastService.Show(ValidationMessage, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            ValidationMessage = DesktopErrorPresentation.ForException(
                ex,
                "Catalog save failed. Check the connection and try again.");
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
