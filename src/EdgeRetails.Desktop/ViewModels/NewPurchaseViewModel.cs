using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class NewPurchaseLineViewModel : ViewModelBase
{
    private decimal _quantity = 1m;
    private decimal _cost;
    private decimal _salePrice;
    private readonly Action _changed;
    private IReadOnlyList<BackendSerializedIdentityInput> _serializedIdentities = [];

    public NewPurchaseLineViewModel(PosProductItemViewModel product, Action changed)
    {
        Product = product;
        _changed = changed;
        _cost = product.Cost;
        _salePrice = product.Price;
    }

    public PosProductItemViewModel Product { get; }
    public string ProductName => Product.Name;
    public string ProductMeta => $"{Product.Brand} · {Product.Sku}";
    public string Unit => Product.Unit;
    public bool IsSerialized => Product.IsSerialized;

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            var normalized = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _quantity, normalized))
            {
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(LineTotalDisplay));
                OnPropertyChanged(nameof(RequiredSerializedUnitCount));
                OnPropertyChanged(nameof(SerializedIdentityStatusDisplay));
                OnPropertyChanged(nameof(HasValidSerializedIntake));
                _changed();
            }
        }
    }

    public decimal Cost
    {
        get => _cost;
        set
        {
            var normalized = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _cost, normalized))
            {
                OnPropertyChanged(nameof(LineTotal));
                OnPropertyChanged(nameof(LineTotalDisplay));
                _changed();
            }
        }
    }

    public decimal SalePrice
    {
        get => _salePrice;
        set => SetProperty(ref _salePrice, Math.Max(0m, Math.Round(value, 2)));
    }

    public IReadOnlyList<BackendSerializedIdentityInput> SerializedIdentities =>
        _serializedIdentities;

    public int RequiredSerializedUnitCount
    {
        get
        {
            if (!IsSerialized)
            {
                return 0;
            }

            var baseQuantity = Quantity * Product.FactorToBaseUnit;
            if (baseQuantity <= 0m ||
                baseQuantity != decimal.Truncate(baseQuantity) ||
                baseQuantity > int.MaxValue)
            {
                return -1;
            }

            return decimal.ToInt32(baseQuantity);
        }
    }

    public bool CanConfigureSerializedIntake =>
        IsSerialized && RequiredSerializedUnitCount > 0;

    public bool HasValidSerializedIntake =>
        !IsSerialized ||
        (RequiredSerializedUnitCount > 0 &&
         _serializedIdentities.Count == RequiredSerializedUnitCount);

    public string SerializedIdentityStatusDisplay => !IsSerialized
        ? "Quantity"
        : RequiredSerializedUnitCount <= 0
            ? "Invalid base quantity"
            : $"{_serializedIdentities.Count}/{RequiredSerializedUnitCount} IDs";

    public decimal LineTotal => Math.Round(Quantity * Cost, 2);
    public string LineTotalDisplay => $"Rs. {LineTotal:N0}";

    public void SetSerializedIdentities(
        IReadOnlyList<BackendSerializedIdentityInput> identities)
    {
        _serializedIdentities = identities.ToArray();
        OnPropertyChanged(nameof(SerializedIdentities));
        OnPropertyChanged(nameof(SerializedIdentityStatusDisplay));
        OnPropertyChanged(nameof(HasValidSerializedIntake));
        _changed();
    }
}

public sealed class NewPurchaseViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService? _previewService;
    private readonly IBackendPurchasingInventoryService? _backendService;
    private readonly Dictionary<string, Guid> _backendSupplierIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IToastService? _toastService;
    private readonly IDialogService? _dialogService;
    private readonly Action? _cancel;
    private readonly Action<PurchaseRecord>? _saved;
    private string _selectedSupplier = string.Empty;
    private string _invoiceNumber = string.Empty;
    private DateTime _purchaseDate = DateTime.Today;
    private string _note = string.Empty;
    private string _searchText = string.Empty;
    private PosProductItemViewModel? _selectedProduct;
    private decimal _otherCharges;
    private readonly Guid _clientOperationId = Guid.CreateVersion7();

    public NewPurchaseViewModel(
        IToastService? toastService = null,
        Action? cancel = null,
        Action<PurchaseRecord>? saved = null,
        IBackendPurchasingInventoryService? backendService = null,
        IDialogService? dialogService = null)
    {
        _backendService = backendService;
        _previewService = ResolvePreviewService(backendService);
        _toastService = toastService;
        _dialogService = dialogService;
        _cancel = cancel;
        _saved = saved;

        Suppliers = backendService is null && _previewService is not null
            ? new ObservableCollection<string>(_previewService.Suppliers)
            : [];
        Products = backendService is null
            ? new ObservableCollection<PosProductItemViewModel>(ResolvePreviewProducts())
            : [];
        FilteredProducts = new ObservableCollection<PosProductItemViewModel>(Products);
        Lines = [];

        AddSelectedProductCommand = new RelayCommand(AddSelectedProduct, () => SelectedProduct != null);
        RemoveLineCommand = new RelayCommand<NewPurchaseLineViewModel>(RemoveLine);
        ConfigureSerializedUnitsCommand = new RelayCommand<NewPurchaseLineViewModel>(
            ConfigureSerializedUnits,
            line => line?.CanConfigureSerializedIntake == true);
        SavePurchaseCommand = new RelayCommand(
            async () => await SavePurchaseAsync(),
            () => CanSave);
        CancelCommand = new RelayCommand(() => _cancel?.Invoke());

        if (_backendService is not null)
        {
            _ = LoadBackendDataAsync();
        }
    }

    public ObservableCollection<string> Suppliers { get; }
    public ObservableCollection<PosProductItemViewModel> Products { get; }
    public ObservableCollection<PosProductItemViewModel> FilteredProducts { get; }
    public ObservableCollection<NewPurchaseLineViewModel> Lines { get; }

    public string SelectedSupplier
    {
        get => _selectedSupplier;
        set
        {
            if (SetProperty(ref _selectedSupplier, value ?? string.Empty))
            {
                RefreshCanSave();
            }
        }
    }

    public string InvoiceNumber
    {
        get => _invoiceNumber;
        set
        {
            if (SetProperty(ref _invoiceNumber, value ?? string.Empty))
            {
                RefreshCanSave();
            }
        }
    }
    public DateTime PurchaseDate
    {
        get => _purchaseDate;
        set => SetProperty(ref _purchaseDate, value);
    }

    public string Note
    {
        get => _note;
        set => SetProperty(ref _note, value ?? string.Empty);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyProductFilter();
            }
        }
    }

    public PosProductItemViewModel? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (SetProperty(ref _selectedProduct, value))
            {
                ((RelayCommand)AddSelectedProductCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public decimal OtherCharges
    {
        get => _otherCharges;
        set
        {
            if (SetProperty(ref _otherCharges, Math.Max(0m, Math.Round(value, 2))))
            {
                Recalculate();
            }
        }
    }
    public string OtherChargesText
    {
        get => OtherCharges.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                OtherCharges = parsed;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                OtherCharges = 0m;
            }
        }
    }

    public decimal Subtotal => Lines.Sum(line => line.LineTotal);
    public decimal Total => Subtotal + OtherCharges;
    public string SubtotalDisplay => $"Rs. {Subtotal:N0}";
    public string TotalDisplay => $"Rs. {Total:N0}";
    public bool CanSave =>
        !string.IsNullOrWhiteSpace(SelectedSupplier) &&
        !string.IsNullOrWhiteSpace(InvoiceNumber) &&
        Lines.Count > 0 &&
        Lines.All(line =>
            line.Quantity > 0m &&
            line.HasValidSerializedIntake);

    public ICommand AddSelectedProductCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand ConfigureSerializedUnitsCommand { get; }
    public ICommand SavePurchaseCommand { get; }
    public ICommand CancelCommand { get; }

    private void AddSelectedProduct()
    {
        if (SelectedProduct is null)
        {
            return;
        }

        var existing = Lines.FirstOrDefault(line => line.Product.Id == SelectedProduct.Id);
        if (existing is not null)
        {
            existing.Quantity += 1m;
        }
        else
        {
            Lines.Add(new NewPurchaseLineViewModel(SelectedProduct, Recalculate));
        }

        Recalculate();
    }

    private void ConfigureSerializedUnits(NewPurchaseLineViewModel? line)
    {
        if (line is null || !line.CanConfigureSerializedIntake)
        {
            _toastService?.Show(
                "Serialized intake requires a positive whole base quantity.",
                ToastTone.Warning);
            return;
        }

        if (_dialogService is null)
        {
            _toastService?.Show(
                "Serialized identity intake dialog is unavailable.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new SerializedPurchaseIntakeViewModel(
            line,
            _dialogService,
            identities =>
            {
                line.SetSerializedIdentities(identities);
                RefreshCanSave();
            }));
    }

    private void RemoveLine(NewPurchaseLineViewModel line)
    {
        if (Lines.Remove(line))
        {
            Recalculate();
        }
    }

    private async Task SavePurchaseAsync()
    {
        if (!CanSave)
        {
            return;
        }

        try
        {
            var lines = Lines.Select(line => new PurchaseDraftLine
            {
                Product = line.Product,
                Quantity = line.Quantity,
                Cost = line.Cost,
                SalePrice = line.SalePrice,
                SerializedIdentities = line.SerializedIdentities
            }).ToArray();

            PurchaseRecord record;
            if (_backendService is null)
            {
                if (_previewService is null)
                {
                    throw new InvalidOperationException(
                        "Production purchase creation requires an authoritative backend service.");
                }

                record = _previewService.SavePurchase(
                    SelectedSupplier,
                    InvoiceNumber,
                    PurchaseDate,
                    Note,
                    OtherCharges,
                    lines);
            }
            else
            {
                if (!_backendSupplierIds.TryGetValue(
                        SelectedSupplier,
                        out var supplierId))
                {
                    throw new InvalidOperationException(
                        "Selected supplier is not attached uniquely to the backend.");
                }

                record = await _backendService.CreatePurchaseAsync(
                    supplierId,
                    InvoiceNumber,
                    PurchaseDate,
                    Note,
                    OtherCharges,
                    lines,
                    _clientOperationId);
            }

            _toastService?.Show(
                $"{record.PurchaseNumber} saved. Stock updated for {record.ItemCount} products.",
                ToastTone.Success);
            _saved?.Invoke(record);
        }
        catch (Exception ex)
        {
            _toastService?.Show(ex.Message, ToastTone.Danger);
        }
    }

    private static DemoPurchaseInventoryService? ResolvePreviewService(
        IBackendPurchasingInventoryService? backendService)
    {
#if DEBUG
        return backendService is null ? DemoPurchaseInventoryService.Instance : null;
#else
        return null;
#endif
    }

    private static IEnumerable<PosProductItemViewModel> ResolvePreviewProducts()
    {
#if DEBUG
        return DemoRetailState.Instance.Products;
#else
        return [];
#endif
    }

    private async Task LoadBackendDataAsync()
    {
        if (_backendService is null)
        {
            return;
        }

        try
        {
            var suppliers = await _backendService.GetSuppliersAsync();
            var catalog = await _backendService.GetCatalogAsync();

            _backendSupplierIds.Clear();
            Suppliers.Clear();

            foreach (var group in suppliers
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                var matches = group.ToArray();
                if (matches.Length != 1)
                {
                    _toastService?.Show(
                        $"Supplier name '{group.Key}' is duplicated in backend and cannot be selected until disambiguated.",
                        ToastTone.Warning);
                    continue;
                }

                Suppliers.Add(matches[0].Name);
                _backendSupplierIds[matches[0].Name] = matches[0].Id;
            }

            Products.Clear();
            foreach (var item in catalog)
            {
                Products.Add(new PosProductItemViewModel(
                    id: item.ProductId.ToString("D"),
                    name: item.Name,
                    sku: item.Sku,
                    brand: "—",
                    category: item.Category,
                    stock: item.SellableStock,
                    price: item.DefaultSalePrice,
                    unit: string.IsNullOrWhiteSpace(item.UnitSymbol)
                        ? "Pcs"
                        : item.UnitSymbol,
                    cost: item.ReferenceCost,
                    minimumStock: 0m,
                    backendProductId: item.ProductId,
                    backendProductUnitId: item.ProductUnitId,
                    isSerialized: item.IsSerialized,
                    serialTrackingEnabled: item.SerialTrackingEnabled,
                    imeiTrackingEnabled: item.ImeiTrackingEnabled,
                    factorToBaseUnit: item.FactorToBaseUnit));
            }

            if (Suppliers.Count == 1)
            {
                SelectedSupplier = Suppliers[0];
            }

            ApplyProductFilter();
            RefreshCanSave();
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                $"Purchase backend data could not be loaded: {ex.Message}",
                ToastTone.Danger);
        }
    }

    private void ApplyProductFilter()
    {
        var term = SearchText.Trim();
        var results = string.IsNullOrWhiteSpace(term)
            ? Products
            : Products.Where(product =>
                product.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                product.Sku.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                product.Brand.Contains(term, StringComparison.OrdinalIgnoreCase));

        FilteredProducts.Clear();
        foreach (var product in results)
        {
            FilteredProducts.Add(product);
        }
    }

    private void Recalculate()
    {
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(SubtotalDisplay));
        OnPropertyChanged(nameof(TotalDisplay));
        RefreshCanSave();
    }

    private void RefreshCanSave()
    {
        OnPropertyChanged(nameof(CanSave));
        ((RelayCommand)SavePurchaseCommand).NotifyCanExecuteChanged();
    }
}
