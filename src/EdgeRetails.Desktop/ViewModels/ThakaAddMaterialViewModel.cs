using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ThakaMaterialProductOption
{
    public ThakaMaterialProductOption(PosProductItemViewModel product)
    {
        Product = product ?? throw new ArgumentNullException(nameof(product));
    }

    public PosProductItemViewModel Product { get; }
    public string Id => Product.Id;
    public string Name => Product.Name;
    public string Sku => Product.Sku;
    public string Unit => Product.Unit;
    public decimal AvailableStock => Product.Stock;
    public decimal UnitPrice => Product.Price;
    public string DisplayName => $"{Name} · {Sku}";
    public string AvailableStockDisplay => $"{AvailableStock:0.##} {Unit}";
    public string UnitPriceDisplay => $"Rs. {UnitPrice:N0}";
}

public sealed class ThakaAddMaterialViewModel : ViewModelBase
{
    private readonly ThakaProjectListItemViewModel _project;
    private readonly Action<MaterialLedgerEntry>? _onMaterialAdded;
    private readonly Action? _onClose;
    private readonly IToastService? _toastService;
    private readonly IBackendThakaService? _backendService;
    private readonly IBackendWorkflowReadService? _workflowService;
    private readonly IDialogService? _dialogService;
    private readonly DemoRetailState? _retailState;
    private string _searchText = string.Empty;
    private ThakaMaterialProductOption? _selectedProduct;
    private decimal _quantity = 1m;
    private string _quantityText = "1";
    private bool _isProcessing;
    private string? _validationMessage;
    private IReadOnlyList<BackendExactUnit> _selectedExactUnits = [];
    private readonly Guid _clientOperationId = Guid.CreateVersion7();

    public ThakaAddMaterialViewModel(
        ThakaProjectListItemViewModel project,
        Action<MaterialLedgerEntry>? onMaterialAdded = null,
        Action? onClose = null,
        IToastService? toastService = null,
        IBackendThakaService? backendService = null,
        IBackendWorkflowReadService? workflowService = null,
        IDialogService? dialogService = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _onMaterialAdded = onMaterialAdded;
        _onClose = onClose;
        _toastService = toastService;
        _backendService = backendService;
        _workflowService = workflowService;
        _dialogService = dialogService;
        _retailState = ResolvePreviewRetailState(backendService);

        Products = [];
        if (_retailState is not null)
        {
            foreach (var product in _retailState.Products)
            {
                Products.Add(new ThakaMaterialProductOption(product));
            }
        }

        FilteredProducts = new ObservableCollection<ThakaMaterialProductOption>(Products);
        SelectedProduct = Products.FirstOrDefault();

        IssueMaterialCommand = new RelayCommand(
            async () => await IssueMaterialAsync(),
            () => CanIssueMaterial);
        SelectExactUnitsCommand = new RelayCommand(
            SelectExactUnits,
            () => CanSelectExactUnits);
        CancelCommand = new RelayCommand(Cancel, () => !IsProcessing);

        if (_backendService is not null)
        {
            _ = LoadBackendCatalogAsync();
        }
    }

    public string ProjectName => _project.ProjectName;
    public decimal CurrentMaterialValue => _project.MaterialValue;
    public string CurrentMaterialValueDisplay => $"Rs. {CurrentMaterialValue:N0}";
    public ObservableCollection<ThakaMaterialProductOption> Products { get; }
    public ObservableCollection<ThakaMaterialProductOption> FilteredProducts { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyFilter();
            }
        }
    }

    public ThakaMaterialProductOption? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (SetProperty(ref _selectedProduct, value))
            {
                _selectedExactUnits = [];
                OnPropertyChanged(nameof(AvailableStockDisplay));
                OnPropertyChanged(nameof(UnitPriceDisplay));
                OnPropertyChanged(nameof(RequiredExactUnitCount));
                OnPropertyChanged(nameof(ExactUnitSelectionDisplay));
                OnPropertyChanged(nameof(CanSelectExactUnits));
                Recalculate();
            }
        }
    }

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var rounded = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _quantity, rounded))
            {
                _quantityText = rounded.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(QuantityText));
                OnPropertyChanged(nameof(RequiredExactUnitCount));
                OnPropertyChanged(nameof(ExactUnitSelectionDisplay));
                OnPropertyChanged(nameof(CanSelectExactUnits));
                Recalculate();
            }
        }
    }

    public string QuantityText
    {
        get => _quantityText;
        set
        {
            if (SetProperty(ref _quantityText, value ?? string.Empty))
            {
                if (decimal.TryParse(_quantityText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
                    decimal.TryParse(_quantityText, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
                {
                    _quantity = Math.Max(0m, Math.Round(parsed, 2));
                    OnPropertyChanged(nameof(Quantity));
                    OnPropertyChanged(nameof(RequiredExactUnitCount));
                    OnPropertyChanged(nameof(ExactUnitSelectionDisplay));
                    OnPropertyChanged(nameof(CanSelectExactUnits));
                }
                else if (string.IsNullOrWhiteSpace(_quantityText))
                {
                    _quantity = 0m;
                    OnPropertyChanged(nameof(Quantity));
                    OnPropertyChanged(nameof(RequiredExactUnitCount));
                    OnPropertyChanged(nameof(ExactUnitSelectionDisplay));
                    OnPropertyChanged(nameof(CanSelectExactUnits));
                }

                Recalculate();
            }
        }
    }

    public string AvailableStockDisplay => SelectedProduct == null ? "Select a product" : $"Available Stock: {SelectedProduct.AvailableStockDisplay}";
    public string UnitPriceDisplay => SelectedProduct?.UnitPriceDisplay ?? "Rs. 0";
    public decimal ThisIssueValue => SelectedProduct == null ? 0m : Math.Round(SelectedProduct.UnitPrice * Quantity, 2);
    public string ThisIssueValueDisplay => $"Rs. {ThisIssueValue:N0}";
    public decimal NewMaterialValue => CurrentMaterialValue + ThisIssueValue;
    public string NewMaterialValueDisplay => $"Rs. {NewMaterialValue:N0}";

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

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanIssueMaterial));
                ((RelayCommand)IssueMaterialCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public int RequiredExactUnitCount
    {
        get
        {
            if (SelectedProduct?.Product.IsSerialized != true || Quantity <= 0m)
            {
                return 0;
            }

            var baseQuantity = Quantity * SelectedProduct.Product.FactorToBaseUnit;
            if (baseQuantity != decimal.Truncate(baseQuantity) ||
                baseQuantity > int.MaxValue)
            {
                return -1;
            }

            return decimal.ToInt32(baseQuantity);
        }
    }

    public string ExactUnitSelectionDisplay =>
        SelectedProduct?.Product.IsSerialized == true
            ? RequiredExactUnitCount <= 0
                ? "Enter a valid serialized quantity."
                : $"{_selectedExactUnits.Count}/{RequiredExactUnitCount} exact unit(s) selected"
            : "Quantity-tracked material";

    public bool CanSelectExactUnits =>
        !IsProcessing &&
        SelectedProduct?.Product.IsSerialized == true &&
        RequiredExactUnitCount > 0;

    public bool CanIssueMaterial =>
        !IsProcessing &&
        SelectedProduct != null &&
        Quantity > 0m &&
        Quantity <= SelectedProduct.AvailableStock &&
        (!SelectedProduct.Product.IsSerialized ||
         (RequiredExactUnitCount > 0 &&
          _selectedExactUnits.Count == RequiredExactUnitCount));

    public ICommand IssueMaterialCommand { get; }
    public ICommand SelectExactUnitsCommand { get; }
    public ICommand CancelCommand { get; }

    private void SelectExactUnits()
    {
        if (!CanSelectExactUnits ||
            SelectedProduct?.Product.BackendProductId is not Guid productId)
        {
            return;
        }

        if (_workflowService is null || _dialogService is null)
        {
            ValidationMessage = "Exact-unit picker is unavailable.";
            return;
        }

        _dialogService.ShowNested(new ExactUnitPickerViewModel(
            "Select Thaka Material Units",
            $"{SelectedProduct.Name} · exact physical provenance",
            productId,
            _workflowService,
            _dialogService,
            units =>
            {
                _selectedExactUnits = units.ToArray();
                Recalculate();
            },
            requiredCount: RequiredExactUnitCount,
            status: EdgeRetails.Domain.Inventory.InventoryUnitStatus.InStock));
    }

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        IEnumerable<ThakaMaterialProductOption> query = string.IsNullOrWhiteSpace(term)
            ? Products
            : Products.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Sku.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();

        FilteredProducts.Clear();
        foreach (var product in query)
        {
            FilteredProducts.Add(product);
        }
    }

    private void Recalculate()
    {
        if (SelectedProduct == null)
        {
            ValidationMessage = "Select a product.";
        }
        else if (Quantity <= 0m)
        {
            ValidationMessage = "Quantity must be greater than zero.";
        }
        else if (Quantity > SelectedProduct.AvailableStock)
        {
            ValidationMessage = $"Only {SelectedProduct.AvailableStock:0.##} {SelectedProduct.Unit} are available.";
        }
        else if (SelectedProduct.Product.IsSerialized &&
                 (RequiredExactUnitCount <= 0 ||
                  _selectedExactUnits.Count != RequiredExactUnitCount))
        {
            ValidationMessage = "Select the required exact physical unit(s) before issue.";
        }
        else
        {
            ValidationMessage = null;
        }

        OnPropertyChanged(nameof(ThisIssueValue));
        OnPropertyChanged(nameof(ThisIssueValueDisplay));
        OnPropertyChanged(nameof(NewMaterialValue));
        OnPropertyChanged(nameof(NewMaterialValueDisplay));
        OnPropertyChanged(nameof(CanIssueMaterial));
        OnPropertyChanged(nameof(CanSelectExactUnits));
        OnPropertyChanged(nameof(ExactUnitSelectionDisplay));
        ((RelayCommand)IssueMaterialCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SelectExactUnitsCommand).NotifyCanExecuteChanged();
    }

    private async Task IssueMaterialAsync()
    {
        if (!CanIssueMaterial || SelectedProduct == null)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            MaterialLedgerEntry entry;
            if (_backendService is null)
            {
                if (_retailState is null)
                {
                    throw new InvalidOperationException(
                        "Authoritative Thaka material service is unavailable.");
                }

                entry = _retailState.IssueMaterial(
                    _project,
                    SelectedProduct.Product,
                    Quantity);
            }
            else
            {
                var result = await _backendService.IssueMaterialAsync(
                    _project,
                    SelectedProduct.Product,
                    Quantity,
                    _selectedExactUnits.Select(x => x.InventoryUnitId).ToArray(),
                    _clientOperationId);

                entry = new MaterialLedgerEntry
                {
                    Date = DateTime.Now,
                    ChallanNumber = result.ChallanNumber,
                    ProductName = SelectedProduct.Name,
                    Quantity = Quantity,
                    Unit = SelectedProduct.Unit,
                    Rate = SelectedProduct.UnitPrice,
                    TotalValue = result.TotalCharge
                };
            }

            _onMaterialAdded?.Invoke(entry);
            _toastService?.Show(
                $"{Quantity:0.##} {SelectedProduct.Unit} of {SelectedProduct.Name} issued to {ProjectName}.",
                ToastTone.Success);
            _onClose?.Invoke();
        }
        catch (BackendOperationException ex)
        {
            ValidationMessage = $"{ex.Code}: {ex.Message}";
            _toastService?.Show(ValidationMessage, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
            _toastService?.Show(ex.Message, ToastTone.Danger);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private async Task LoadBackendCatalogAsync()
    {
        if (_backendService is null)
        {
            return;
        }

        try
        {
            var rows = await _backendService.GetMaterialCatalogAsync();

            Products.Clear();
            foreach (var row in rows)
            {
                Products.Add(new ThakaMaterialProductOption(row.Product));
            }

            SelectedProduct = Products.FirstOrDefault();
            ApplyFilter();
            Recalculate();
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
            _toastService?.Show(
                $"Thaka material catalog could not be loaded: {ex.Message}",
                ToastTone.Danger);
        }
    }

    private static DemoRetailState? ResolvePreviewRetailState(
        IBackendThakaService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoRetailState.Instance : null;
#else
        return null;
#endif
    }

    private void Cancel()
    {
        if (!IsProcessing)
        {
            _onClose?.Invoke();
        }
    }
}
