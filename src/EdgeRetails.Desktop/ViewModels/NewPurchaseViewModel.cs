using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Application.Features.Purchasing;
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

            var baseQuantity = Product.TrackingMode == EdgeRetails.Domain.Catalog.TrackingMode.Container
                ? Quantity : Quantity * Product.FactorToBaseUnit;
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
        !IsSerialized || (!Product.SerialTrackingEnabled && !Product.ImeiTrackingEnabled && _serializedIdentities.Count == 0) ||
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

public sealed class NewPurchaseViewModel : ViewModelBase, IDisposable
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
    private readonly IBackendPurchaseLookupService? _lookup;
    private CancellationTokenSource? _productLookupCts;
    private CancellationTokenSource? _supplierLookupCts;
    private long _productLookupGeneration;
    private long _supplierLookupGeneration;
    private string? _nextProductName;
    private Guid? _nextProductId;
    private string? _nextSupplierName;
    private Guid? _nextSupplierId;
    private string _supplierSearchText = string.Empty;
    private bool _applyingLookup;
    public Guid? SelectedSupplierId { get; private set; }
    private readonly Guid _clientOperationId = Guid.CreateVersion7();
    private int _submissionGate;
    private bool _isPurchaseCommitted;
    private string _commitStatusMessage = string.Empty;
    public Guid? ConfirmedPurchaseId { get; private set; }
    public bool IsPurchaseCommitted => _isPurchaseCommitted;
    public string CommitStatusMessage
    {
        get => _commitStatusMessage;
        private set => SetProperty(ref _commitStatusMessage, value);
    }

    public NewPurchaseViewModel(
        IToastService? toastService = null,
        Action? cancel = null,
        Action<PurchaseRecord>? saved = null,
        IBackendPurchasingInventoryService? backendService = null,
        IDialogService? dialogService = null)
    {
        _backendService = backendService;
        _lookup = backendService as IBackendPurchaseLookupService;
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
        RefreshCommittedPurchaseCommand = new RelayCommand(
            async () => await RefreshCommittedPurchaseAsync(),
            () => ConfirmedPurchaseId.HasValue && _submissionGate == 0);
        MoreProductsCommand = new RelayCommand(() => _ = SearchProductsAsync(true));
        MoreSuppliersCommand = new RelayCommand(() => _ = SearchSuppliersAsync(true));
        CancelCommand = new RelayCommand(() => { Dispose(); _cancel?.Invoke(); });

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
            if (_applyingLookup && string.IsNullOrEmpty(value))
            {
                return;
            }
            if (SetProperty(ref _selectedSupplier, value ?? string.Empty))
            {
                SelectedSupplierId = _backendSupplierIds.TryGetValue(_selectedSupplier, out var id) ? id : null;
                OnPropertyChanged(nameof(SelectedSupplierId));
                RefreshCanSave();
            }
        }
    }

    public string SupplierSearchText
    {
        get => _supplierSearchText;
        set { if (SetProperty(ref _supplierSearchText, value ?? string.Empty) && _lookup is not null)
              {
                  _ = SearchSuppliersAsync(false);
              } }
    }
    public bool HasMoreProducts => _nextProductId.HasValue;
    public bool HasMoreSuppliers => _nextSupplierId.HasValue;
    public ICommand MoreProductsCommand { get; }
    public ICommand MoreSuppliersCommand { get; }

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
                if (_lookup is not null)
                {
                    _ = SearchProductsAsync(false);
                }
                else
                {
                    ApplyProductFilter();
                }
            }
        }
    }

    public PosProductItemViewModel? SelectedProduct
    {
        get => _selectedProduct;
        set
        {
            if (_applyingLookup && value is null)
            {
                return;
            }
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
    public bool RequiresSerializedIntake =>
        _backendService is null || _backendService.ReceivesStockImmediately;

    public bool CanSave =>
        !_isPurchaseCommitted && _submissionGate == 0 &&
        !string.IsNullOrWhiteSpace(SelectedSupplier) &&
        !string.IsNullOrWhiteSpace(InvoiceNumber) &&
        Lines.Count > 0 &&
        Lines.All(line =>
            line.Quantity > 0m &&
            (!RequiresSerializedIntake || line.HasValidSerializedIntake));

    public ICommand AddSelectedProductCommand { get; }
    public ICommand RemoveLineCommand { get; }
    public ICommand ConfigureSerializedUnitsCommand { get; }
    public ICommand SavePurchaseCommand { get; }
    public ICommand RefreshCommittedPurchaseCommand { get; }
    public ICommand CancelCommand { get; }

    private void AddSelectedProduct()
    {
        if (SelectedProduct is null)
        {
            return;
        }

        var selectedProductId = SelectedProduct.BackendProductId ?? (Guid.TryParse(SelectedProduct.Id, out var spid) ? spid : Guid.Empty);
        var existing = Lines.FirstOrDefault(line =>
        {
            var lineProductId = line.Product.BackendProductId ?? (Guid.TryParse(line.Product.Id, out var lpid) ? lpid : Guid.Empty);
            return selectedProductId != Guid.Empty && lineProductId != Guid.Empty
                ? lineProductId == selectedProductId
                : string.Equals(line.Product.Sku, SelectedProduct.Sku, StringComparison.OrdinalIgnoreCase);
        });

        if (existing is not null)
        {
            var sameUnit = SelectedProduct.BackendProductUnitId.HasValue && existing.Product.BackendProductUnitId.HasValue
                ? SelectedProduct.BackendProductUnitId.Value == existing.Product.BackendProductUnitId.Value
                : string.Equals(existing.Product.Unit, SelectedProduct.Unit, StringComparison.OrdinalIgnoreCase);

            if (sameUnit)
            {
                existing.Quantity += 1m;
            }
            else
            {
                _toastService?.Show(
                    $"Product '{SelectedProduct.Name}' is already in this purchase with unit '{existing.Product.Unit}'. Alternate units cannot be merged into the same purchase line.",
                    ToastTone.Warning);
                return;
            }
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
        if (Interlocked.CompareExchange(ref _submissionGate, 1, 0) != 0)
        {
            return;
        }
        RefreshSubmissionCommands();

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
                if (SelectedSupplierId is not Guid supplierId)
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

            SetConfirmedPurchase(record.BackendPurchaseId, record.PurchaseNumber);
            if (record.StockReceivedImmediately)
            {
                _toastService?.Show(
                    $"{record.PurchaseNumber} saved. Stock updated for {record.ItemCount} products.",
                    ToastTone.Success);
            }
            else
            {
                _toastService?.Show(
                    $"Purchase order {record.PurchaseNumber} saved. Receive stock from Purchase Detail.",
                    ToastTone.Success);
            }
            Dispose();
            _saved?.Invoke(record);
        }
        catch (PurchaseCommittedReadbackException ex)
        {
            SetConfirmedPurchase(ex.Result.PurchaseId, ex.Result.PurchaseNumber);
            _toastService?.Show(CommitStatusMessage, ToastTone.Warning);
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                _isPurchaseCommitted ? CommitStatusMessage :
                    DesktopErrorPresentation.ForException(ex, "Purchase outcome is not confirmed. Keep this form open and preserve the original operation identity."),
                ToastTone.Danger);
        }
        finally
        {
            Interlocked.Exchange(ref _submissionGate, 0);
            RefreshSubmissionCommands();
        }
    }

    private void SetConfirmedPurchase(Guid? purchaseId, string number)
    {
        _isPurchaseCommitted = true;
        ConfirmedPurchaseId = purchaseId;
        CommitStatusMessage = $"Purchase {number} is saved (ID: {purchaseId}). Refresh details without saving again.";
        OnPropertyChanged(nameof(IsPurchaseCommitted));
        OnPropertyChanged(nameof(ConfirmedPurchaseId));
    }

    private async Task RefreshCommittedPurchaseAsync()
    {
        if (_backendService is null || ConfirmedPurchaseId is not Guid purchaseId ||
            Interlocked.CompareExchange(ref _submissionGate, 1, 0) != 0)
        {
            return;
        }
        RefreshSubmissionCommands();
        try
        {
            var record = await _backendService.GetPurchaseAsync(purchaseId)
                ?? throw new InvalidOperationException("Saved purchase details remain unavailable.");
            record.StockReceivedImmediately = false;
            Dispose();
            _saved?.Invoke(record);
        }
        catch (Exception)
        {
            _toastService?.Show(CommitStatusMessage + " Details remain unavailable.", ToastTone.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _submissionGate, 0);
            RefreshSubmissionCommands();
        }
    }

    private void RefreshSubmissionCommands()
    {
        RefreshCanSave();
        ((RelayCommand)RefreshCommittedPurchaseCommand).NotifyCanExecuteChanged();
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
            if (_lookup is not null)
            {
                await Task.WhenAll(SearchSuppliersAsync(false), SearchProductsAsync(false));
                return;
            }
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
                    foreach (var s in matches)
                    {
                        var disambiguated = !string.IsNullOrWhiteSpace(s.City)
                            ? $"{s.Name} — {s.City}"
                            : $"{s.Name} ({s.Id.ToString()[..6]})";
                        if (_backendSupplierIds.ContainsKey(disambiguated))
                        {
                            disambiguated = $"{s.Name} ({s.Id.ToString()[..6]})";
                        }
                        Suppliers.Add(disambiguated);
                        _backendSupplierIds[disambiguated] = s.Id;
                    }
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
                    factorToBaseUnit: item.FactorToBaseUnit,
                    trackingMode: item.TrackingMode));
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
                DesktopErrorPresentation.ForException(
                    ex,
                    "Purchase data could not be loaded. Check the connection and try again."),
                ToastTone.Danger);
        }
    }

    public async Task SearchSuppliersAsync(bool more = false)
    {
        if (_lookup is null || (more && !HasMoreSuppliers))
        {
            return;
        }
        if (!more)
        {
            _nextSupplierName = null;
            _nextSupplierId = null;
            OnPropertyChanged(nameof(HasMoreSuppliers));
        }
        var generation = Interlocked.Increment(ref _supplierLookupGeneration);
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _supplierLookupCts, cts);
        previous?.Cancel();
        try
        {
            if (!more)
            {
                await Task.Delay(250, cts.Token);
            }
            var page = await _lookup.GetSupplierPageAsync(SupplierSearchText, 50,
                more ? _nextSupplierName : null, more ? _nextSupplierId : null, cts.Token);
            if (cts.IsCancellationRequested || generation != Volatile.Read(ref _supplierLookupGeneration))
            {
                return;
            }
            _applyingLookup = true;
            try
            {
                var selected = SelectedSupplier;
                if (!more)
                {
                    Suppliers.Clear();
                }
                if (SelectedSupplierId.HasValue && !Suppliers.Contains(selected))
                {
                    Suppliers.Add(selected);
                }
                foreach (var supplier in page.Items)
                {
                    // Full ID makes duplicate names/cities unambiguous across separately fetched pages.
                    var label = $"{supplier.DisplayName} ({supplier.Id:D})";
                    _backendSupplierIds[label] = supplier.Id;
                    if (!Suppliers.Contains(label))
                    {
                        Suppliers.Add(label);
                    }
                }
                _nextSupplierName = page.NextName;
                _nextSupplierId = page.NextSupplierId;
                OnPropertyChanged(nameof(HasMoreSuppliers));
                OnPropertyChanged(nameof(SelectedSupplier));
                RefreshCanSave();
            }
            finally { _applyingLookup = false; }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _supplierLookupGeneration))
            {
                _toastService?.Show(DesktopErrorPresentation.ForException(ex, "Supplier search failed."), ToastTone.Danger);
            }
        }
        finally { Interlocked.CompareExchange(ref _supplierLookupCts, null, cts); cts.Dispose(); }
    }

    public async Task SearchProductsAsync(bool more = false)
    {
        if (_lookup is null || (more && !HasMoreProducts))
        {
            return;
        }
        if (!more)
        {
            _nextProductName = null;
            _nextProductId = null;
            OnPropertyChanged(nameof(HasMoreProducts));
        }
        var generation = Interlocked.Increment(ref _productLookupGeneration);
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _productLookupCts, cts);
        previous?.Cancel();
        try
        {
            if (!more)
            {
                await Task.Delay(250, cts.Token);
            }
            var page = await _lookup.GetCatalogPageAsync(new PurchaseCatalogPageQuery(SearchText, 50,
                more ? _nextProductName : null, more ? _nextProductId : null), cts.Token);
            // Off-page selections remain visible; refresh their stock from their exact Product ID.
            var selectedProductId = SelectedProduct?.BackendProductId;
            PurchaseCatalogPageDto? selectedPage = null;
            if (selectedProductId is Guid exactId && !page.Items.Any(x => x.ProductId == exactId))
            {
                selectedPage = await _lookup.GetCatalogPageAsync(
                    new PurchaseCatalogPageQuery(ProductId: exactId, IncludeInactive: true), cts.Token);
            }
            if (cts.IsCancellationRequested || generation != Volatile.Read(ref _productLookupGeneration))
            {
                return;
            }
            _applyingLookup = true;
            try
            {
                foreach (var stock in page.Items.Concat(selectedPage?.Items ?? []).GroupBy(x => x.ProductId).Select(x => x.First()))
                {
                    if (SelectedProduct?.BackendProductId == stock.ProductId)
                    {
                        SelectedProduct.Stock = stock.SellableStock;
                    }
                    foreach (var line in Lines.Where(x => x.Product.BackendProductId == stock.ProductId))
                    {
                        line.Product.Stock = stock.SellableStock;
                    }
                    foreach (var existing in Products.Where(x => x.BackendProductId == stock.ProductId))
                    {
                        existing.Stock = stock.SellableStock;
                    }
                }
                if (!more) { Products.Clear(); FilteredProducts.Clear(); }
                if (SelectedProduct is { } selected && !FilteredProducts.Any(x => x.BackendProductUnitId == selected.BackendProductUnitId))
                {
                    FilteredProducts.Add(selected);
                }
                foreach (var item in page.Items)
                {
                    if (Products.Any(x => x.BackendProductUnitId == item.ProductUnitId))
                    {
                        continue;
                    }
                    var product = new PosProductItemViewModel(item.ProductId.ToString("D"), item.Name,
                        item.Sku ?? string.Empty, "—", item.Category, item.SellableStock, item.DefaultSalePrice,
                        unit: item.UnitSymbol, cost: item.ReferenceCost, backendProductId: item.ProductId,
                        backendProductUnitId: item.ProductUnitId, isSerialized: item.IsSerialized,
                        serialTrackingEnabled: item.SerialTrackingEnabled, imeiTrackingEnabled: item.ImeiTrackingEnabled,
                        factorToBaseUnit: item.FactorToBaseUnit, trackingMode: item.TrackingMode);
                    Products.Add(product);
                    if (!FilteredProducts.Any(x => x.BackendProductUnitId == item.ProductUnitId))
                    {
                        FilteredProducts.Add(product);
                    }
                }
                _nextProductName = page.NextName;
                _nextProductId = page.NextProductId;
                OnPropertyChanged(nameof(HasMoreProducts));
                OnPropertyChanged(nameof(SelectedProduct));
            }
            finally { _applyingLookup = false; }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (generation == Volatile.Read(ref _productLookupGeneration))
            {
                _toastService?.Show(DesktopErrorPresentation.ForException(ex, "Product search failed."), ToastTone.Danger);
            }
        }
        finally { Interlocked.CompareExchange(ref _productLookupCts, null, cts); cts.Dispose(); }
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _productLookupGeneration);
        Interlocked.Increment(ref _supplierLookupGeneration);
        Interlocked.Exchange(ref _productLookupCts, null)?.Cancel();
        Interlocked.Exchange(ref _supplierLookupCts, null)?.Cancel();
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
