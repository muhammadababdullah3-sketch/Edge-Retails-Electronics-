using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Controls;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Application.Features.Identity;
using EdgeRetails.Application.Production.Printing;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// ViewModel for the canonical POS local-sale workspace.
/// Thaka material issuance belongs exclusively to Thaka Workspace.
/// </summary>
public sealed class PosViewModel : ViewModelBase
{
    private const string PendingSaleIntentCheckStatus =
        "Checking the previous sale outcome before enabling checkout.";
    private readonly IToastService? _toastService;
    private readonly IDialogService? _dialogService;
    private readonly ITransactionService _transactionService;
    private readonly IClientOperationIntentStore? _operationIntents;
    private readonly IPosCatalogGateway? _posCatalogGateway;
    private readonly IBackendBusinessOperationsService? _businessOperationsService;
    private readonly IBackendWorkflowReadService? _workflowService;
    private readonly IProductionDocumentPrintService? _documentPrintService;
    private readonly IWorkstationPrinterSettings? _printerSettings;
    private readonly List<CustomerDirectoryRecord> _customers = [];
    private readonly bool _isBackendCatalog;
    private readonly string _cashierName;
    private readonly bool _canOverridePrice;
#if DEBUG
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;
    private readonly DemoBusinessDirectoryService _businessDirectory = DemoBusinessDirectoryService.Instance;
#endif

    private string _searchText = string.Empty;
    private CancellationTokenSource? _catalogSearchCts;
    private long _catalogSearchVersion;
    private string? _afterCatalogName;
    private Guid? _afterCatalogId;
    private bool _hasMoreCatalogProducts;
    private int _isLoadingMoreCatalog;
    private string _selectedCategory = "All";
    private string _selectedBrand = "All";
    private CustomerDirectoryRecord? _selectedCustomer;
    private string _customerName = "Walk-in Customer";
    private string _customerPhone = string.Empty;
    private decimal _discountAmount;
    private string? _scannerStatusMessage;
    private Guid? _currentDraftId;
    private long? _currentDraftVersion;
    private string? _currentDraftNumber;
    private Guid? _pendingSaleOperationId;
    private Guid? _pendingDraftOperationId;
    private bool _saleOperationIntentReady;
    private int _scanInFlight;
    private int _checkoutInFlight;
    private int _draftSaveInFlight;
    private int _pendingSaleIntentReadInFlight;
    private int _pendingSaleIntentReadRequested;
    private bool _draftSaveOutcomeUncertain;

    public event EventHandler? FocusSearchRequested;

    public PosViewModel()
        : this(toastService: null, dialogService: null, transactionService: null, sessionContext: null)
    {
    }

    public PosViewModel(
        IToastService? toastService = null,
        IDialogService? dialogService = null,
        ITransactionService? transactionService = null,
        ISessionContext? sessionContext = null,
        IPosCatalogGateway? posCatalogGateway = null,
        IBackendBusinessOperationsService? businessOperationsService = null,
        IBackendWorkflowReadService? workflowService = null,
        IProductionDocumentPrintService? documentPrintService = null,
        IWorkstationPrinterSettings? printerSettings = null,
        IClientOperationIntentStore? operationIntents = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _transactionService = ResolveTransactionService(transactionService);
        _operationIntents = operationIntents;
        _saleOperationIntentReady = operationIntents is null;
        _posCatalogGateway = posCatalogGateway;
        _businessOperationsService = businessOperationsService;
        _workflowService = workflowService;
        _documentPrintService = documentPrintService;
        _printerSettings = printerSettings;
        _isBackendCatalog = posCatalogGateway is not null;
        _canOverridePrice = sessionContext?.PermissionKeys.Contains(PermissionKeys.SalesPriceOverride) == true;
        _cashierName = sessionContext != null
            ? $"{sessionContext.DisplayName}, {sessionContext.RoleName}"
            : "Abdullah, Owner";

        Categories = new ObservableCollection<string>(["All"]);
        Brands = new ObservableCollection<string>(["All"]);

#if DEBUG
        if (_posCatalogGateway is null)
        {
            ReplaceFilterValues(
                Categories,
                AllDemoCategories());
            ReplaceFilterValues(
                Brands,
                AllDemoBrands());
        }
#endif

        AllProducts = CreateInitialProducts(_posCatalogGateway);
        FilteredProducts = new ObservableCollection<PosProductItemViewModel>(AllProducts);
        CartItems = [];

        // Commands
        AddToCartCommand = new RelayCommand<PosProductItemViewModel>(AddToCart);
        IncrementCartItemCommand = new RelayCommand<PosCartItemViewModel>(item => item.Increment());
        DecrementCartItemCommand = new RelayCommand<PosCartItemViewModel>(item => item.Decrement());
        RemoveCartItemCommand = new RelayCommand<PosCartItemViewModel>(RemoveCartItem);
        ClearCartCommand = new RelayCommand(ClearCart);

        CompleteSaleCommand = new RelayCommand(
            CompleteSale,
            () => CartItems.Count > 0 && Total > 0m && _saleOperationIntentReady);

        ChangeCustomerCommand = new RelayCommand(ChangeCustomer);
        FocusSearchCommand = new RelayCommand(RequestFocusSearch);
        ScanCommand = new RelayCommand(async () => await ScanAsync());
        PriceCheckCommand = new RelayCommand(async () => await PriceCheckAsync());
        SaveDraftCommand = new RelayCommand(async () => await SaveDraftAsync(holdAfterSave: false),
            () => CartItems.Count > 0 && !_draftSaveOutcomeUncertain && Volatile.Read(ref _draftSaveInFlight) == 0);
        HoldDraftCommand = new RelayCommand(async () => await SaveDraftAsync(holdAfterSave: true),
            () => CartItems.Count > 0 && !_draftSaveOutcomeUncertain && Volatile.Read(ref _draftSaveInFlight) == 0);
        RecentDraftsCommand = new RelayCommand(OpenRecentDrafts);
        LoadMoreCatalogCommand = new RelayCommand(async () => await LoadMoreCatalogAsync(),
            () => HasMoreCatalogProducts && _isLoadingMoreCatalog == 0);

        // Sales terminal starts with a clean, empty cart ready for new transactions
        CartItems.Clear();
        RecalculateTotals();
        if (_operationIntents is not null)
        {
            _ = RefreshPendingSaleOperationIdAsync();
        }

        if (_posCatalogGateway is not null)
        {
            _ = LoadBackendCatalogAsync();
        }

        if (_businessOperationsService is not null)
        {
            _ = LoadBackendCustomersAsync();
        }
#if DEBUG
        else
        {
            _customers.AddRange(_businessDirectory.Customers);
        }
#endif
    }

    public ObservableCollection<string> Categories { get; }

    public ObservableCollection<string> Brands { get; }

    public List<PosProductItemViewModel> AllProducts { get; }

    public ObservableCollection<PosProductItemViewModel> FilteredProducts { get; }

    public ObservableCollection<PosCartItemViewModel> CartItems { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                if (_posCatalogGateway is null)
                {
                    ApplyFilters();
                }
                else
                {
                    _ = ScheduleCatalogSearchAsync(value);
                }
            }
        }
    }

    public string SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (SetProperty(ref _selectedCategory, value))
            {
                ApplyFilters();
                if (_posCatalogGateway is not null)
                {
                    _ = ScheduleCatalogSearchAsync(SearchText, immediate: true);
                }
            }
        }
    }

    public string SelectedBrand
    {
        get => _selectedBrand;
        set
        {
            if (SetProperty(ref _selectedBrand, value))
            {
                ApplyFilters();
                if (_posCatalogGateway is not null)
                {
                    _ = ScheduleCatalogSearchAsync(SearchText, immediate: true);
                }
            }
        }
    }

    public ICommand LoadMoreCatalogCommand { get; }

    public bool HasMoreCatalogProducts
    {
        get => _hasMoreCatalogProducts;
        private set
        {
            if (SetProperty(ref _hasMoreCatalogProducts, value))
            {
                OnPropertyChanged(nameof(FilteredProductCountText));
                ((RelayCommand)LoadMoreCatalogCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingMoreCatalog
    {
        get => _isLoadingMoreCatalog != 0;
        private set
        {
            OnPropertyChanged();
            ((RelayCommand)LoadMoreCatalogCommand).NotifyCanExecuteChanged();
        }
    }

    public string FilteredProductCountText =>
        _isBackendCatalog && !_hasAuthoritativeCatalogFilters ? $"{FilteredProducts.Count} search results (limit 200)" :
        HasMoreCatalogProducts ? $"{FilteredProducts.Count}+ products" :
        $"{FilteredProducts.Count} {(FilteredProducts.Count == 1 ? "product" : "products")}";

    private bool _hasAuthoritativeCatalogFilters;

    public bool SupportsCatalogFilters => !_isBackendCatalog || _hasAuthoritativeCatalogFilters;
    public string CatalogReadLimitNotice => _isBackendCatalog && !_hasAuthoritativeCatalogFilters
        ? "Search by name or SKU. Results are limited to 200; refine your search. Brand and category browsing are unavailable."
        : "Choose All filters and use Load More Products to discover additional brands and categories.";

    public bool HasNoMatchingProducts => FilteredProducts.Count == 0;

    public string CartItemCountText =>
        CartItems.Count == 0 ? "Empty" : $"{CartItems.Count} {(CartItems.Count == 1 ? "item" : "items")}";

    public bool IsCartEmpty => CartItems.Count == 0;

    public bool HasCartItems => CartItems.Count > 0;

    public string CustomerName
    {
        get => _customerName;
        private set => SetProperty(ref _customerName, value);
    }

    public string CustomerPhone
    {
        get => _customerPhone;
        private set
        {
            if (SetProperty(ref _customerPhone, value))
            {
                OnPropertyChanged(nameof(CustomerSubtitle));
            }
        }
    }

    public string CustomerSubtitle =>
        string.IsNullOrWhiteSpace(CustomerPhone)
            ? "Walk-in sale"
            : CustomerPhone;

    public bool IsWalkInCustomer => SelectedCustomer is null;

    public CustomerDirectoryRecord? SelectedCustomer
    {
        get => _selectedCustomer;
        private set
        {
            if (SetProperty(ref _selectedCustomer, value))
            {
                OnPropertyChanged(nameof(IsWalkInCustomer));
            }
        }
    }

    private PosProductItemViewModel? _selectedCatalogProduct;

    public PosProductItemViewModel? SelectedCatalogProduct
    {
        get => _selectedCatalogProduct;
        set => SetProperty(ref _selectedCatalogProduct, value);
    }

    public decimal DiscountAmount
    {
        get => _discountAmount;
        set
        {
            var clamped = Math.Max(0m, value);
            if (SetProperty(ref _discountAmount, clamped))
            {
                RecalculateTotals();
            }
        }
    }

    public string DiscountAmountText
    {
        get => _discountAmount.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
            {
                DiscountAmount = parsed;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                DiscountAmount = 0m;
            }
        }
    }

    public decimal Subtotal { get; private set; }

    public string SubtotalDisplay => $"Rs. {Subtotal:N2}";

    public decimal Total { get; private set; }

    public string TotalDisplay => $"Rs. {Total:N2}";

    public string InvoiceDisplay => "Invoice assigned on completion";

    // Commands
    public ICommand AddToCartCommand { get; }

    public ICommand IncrementCartItemCommand { get; }

    public ICommand DecrementCartItemCommand { get; }

    public ICommand RemoveCartItemCommand { get; }

    public ICommand ClearCartCommand { get; }

    public ICommand CompleteSaleCommand { get; }

    public ICommand ChangeCustomerCommand { get; }

    public ICommand FocusSearchCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand PriceCheckCommand { get; }
    public ICommand SaveDraftCommand { get; }
    public ICommand HoldDraftCommand { get; }
    public ICommand RecentDraftsCommand { get; }

    public string? ScannerStatusMessage
    {
        get => _scannerStatusMessage;
        private set => SetProperty(ref _scannerStatusMessage, value);
    }

    public bool IsResumedDraft => _currentDraftId is not null;
    public string DraftStatusDisplay => _currentDraftId is null
        ? "New Sale"
        : $"Draft {_currentDraftNumber ?? _currentDraftId.Value.ToString("N")[..8]}";

    public void RequestFocusSearch()
    {
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    public string Mode { get; private set; } = "Normal";

    public void RequestModeChange(string targetMode)
    {
        Mode = targetMode;
    }

    public void AddToCart(PosProductItemViewModel? product)
    {
        if (product == null)
        {
            return;
        }

        if (!product.IsInStock)
        {
            _toastService?.Show(
                $"{product.Name} is out of stock and cannot be added to cart.",
                ToastTone.Warning);
            return;
        }

        if (_isBackendCatalog && product.IsSerialized)
        {
            OpenExactUnitPicker(product);
            return;
        }

        var existing = CartItems.FirstOrDefault(c =>
            c.ProductId == product.Id && !c.HasExactUnit);
        if (existing != null)
        {
            if (!existing.CanIncrement)
            {
                _toastService?.Show(
                    $"Only {product.Stock:0.##} units of {product.Name} are available.",
                    ToastTone.Warning);
                return;
            }

            existing.Increment();
        }
        else
        {
            var initialQuantity = Math.Min(1m, product.Stock);
            CartItems.Add(new PosCartItemViewModel(
                product,
                quantity: initialQuantity,
                onChanged: _ => RecalculateTotals(),
                onRemove: RemoveCartItem,
                canOverridePrice: _canOverridePrice));
        }

        RecalculateTotals();
    }

    public void RemoveCartItem(PosCartItemViewModel? item)
    {
        if (item != null && CartItems.Remove(item))
        {
            RecalculateTotals();
        }
    }

    public void ClearCart()
    {
        CartItems.Clear();
        RecalculateTotals();
    }

    public void CompleteSale()
    {
        if (!_saleOperationIntentReady)
        {
            ScannerStatusMessage = "The previous sale outcome is still being checked. Checkout will be available when it is verified.";
            return;
        }

        _ = BeginCheckoutAsync();
    }

    private async Task BeginCheckoutAsync()
    {
        if (Interlocked.Exchange(ref _checkoutInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            var freshStateError = await RevalidateCartAsync();
            if (!string.IsNullOrWhiteSpace(freshStateError))
            {
                ScannerStatusMessage = freshStateError;
                _toastService?.Show(freshStateError, ToastTone.Warning);
                return;
            }

            OpenCheckoutDialog();
        }
        catch (BackendOperationException ex)
        {
            var safeMessage = DesktopErrorPresentation.ForException(
                ex,
                "The scan could not be added. Refresh the product or unit list and try again.");
            ScannerStatusMessage = safeMessage;
            _toastService?.Show(safeMessage, ToastTone.Danger);
        }
        catch (Exception)
        {
            ScannerStatusMessage = "Could not check current price and stock. Try again when the server is available.";
            _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
        }
        finally
        {
            Volatile.Write(ref _checkoutInFlight, 0);
        }
    }

    private async Task<string?> RevalidateCartAsync()
    {
        var localError = ValidateCartStockForCommit();
        if (localError is not null || !_isBackendCatalog || _posCatalogGateway is null)
        {
            return localError;
        }

        foreach (var group in CartItems.GroupBy(item => item.Product.BackendProductUnitId))
        {
            var product = group.First().Product;
            if (product.BackendProductId is not Guid productId ||
                product.BackendProductUnitId is not Guid productUnitId)
            {
                return $"{product.Name} is missing its authoritative product identity.";
            }

            var search = string.IsNullOrWhiteSpace(product.Sku) ? product.Name : product.Sku;
            var latest = (await _posCatalogGateway.LoadAsync(search, 200))
                .FirstOrDefault(row =>
                    row.ProductId == productId && row.ProductUnitId == productUnitId);
            if (latest is null)
            {
                return $"{product.Name} is no longer in the sellable catalog. Remove it from the cart.";
            }

            if (latest.UnitPrice != product.Price)
            {
                product.Price = latest.UnitPrice;
                foreach (var line in group)
                {
                    line.RefreshFromProduct();
                }
                return $"Price changed for {product.Name}. Review the updated cart before completing the sale.";
            }

            product.Stock = latest.SellableStock;
            foreach (var line in group)
            {
                line.RefreshFromProduct();
            }
            if (group.Sum(item => item.Quantity) > latest.SellableStock)
            {
                return $"Stock changed for {product.Name}. Available: {latest.SellableStock:0.##} {product.Unit}.";
            }

            if (product.IsSerialized)
            {
                if (_workflowService is null)
                {
                    return "Exact-unit authority is unavailable. Reconnect before checkout.";
                }

                var currentUnits = await _workflowService.GetExactUnitsAsync(productId, status: null);
                foreach (var line in group)
                {
                    if (line.ExactUnit is null)
                    {
                        return $"Select an exact physical unit for {product.Name}.";
                    }

                    var current = currentUnits.FirstOrDefault(x =>
                        x.InventoryUnitId == line.ExactUnit.InventoryUnitId);
                    if (current is null ||
                        current.Status != EdgeRetails.Domain.Inventory.InventoryUnitStatus.InStock ||
                        current.Version != line.ExactUnit.Version)
                    {
                        return $"Physical unit {line.ExactIdentityDisplay} changed. Remove it and select an available unit again.";
                    }
                }
            }
        }

        return null;
    }

    private void OpenCheckoutDialog()
    {
        if (CartItems.Count == 0 || Total <= 0m)
        {
            _toastService?.Show("Cannot complete sale: Cart is empty or total is invalid.", ToastTone.Warning);
            return;
        }

        if (_dialogService == null)
        {
            _toastService?.Show("Complete Sale dialog is unavailable.", ToastTone.Warning);
            return;
        }

        var items = CartItems
            .Select(item => new SaleTransactionItem
            {
                ProductId = item.ProductId,
                BackendProductId = item.Product.BackendProductId,
                BackendProductUnitId = item.Product.BackendProductUnitId,
                InventoryUnitIds = item.InventoryUnitIds,
                ProductName = item.Name,
                Sku = item.Sku,
                Brand = item.Brand,
                UnitPrice = item.UnitPrice,
                ListUnitPrice = item.ListUnitPrice,
                PriceOverrideUnitPrice = item.PriceOverrideUnitPrice,
                PriceOverrideReason = item.PriceOverrideReason,
                Quantity = item.Quantity,
                Discount = 0m,
                LineTotal = item.LineTotal,
                UnitCostSnapshot = item.Product.Cost
            })
            .ToArray();

        _pendingSaleOperationId ??= Guid.CreateVersion7();

        var checkout = new CompleteSaleViewModel(
            totalToPay: Total,
            transactionService: _transactionService,
            dialogService: _dialogService,
            toastService: _toastService,
            customerName: CustomerName,
            customerPhone: CustomerPhone,
            customerId: SelectedCustomer?.BackendId,
            cashierName: _cashierName,
            items: items,
            subtotal: Subtotal,
            discountAmount: DiscountAmount,
            preCommitValidation: ValidateCartStockForCommit,
            preCommitAsyncValidation: RevalidateCartAsync,
            clientOperationId: _pendingSaleOperationId,
            draftId: _currentDraftId,
            draftVersion: _currentDraftVersion)
        {
            OnCheckoutCancelled = attempted =>
            {
                if (attempted)
                {
                    ResetPendingSaleOperationWhenResolved();
                }
            }
        };
        checkout.OnSaleCompleted = record =>
        {
            OnSaleCompleted(record);
            if (checkout.PrintReceipt)
            {
                _ = PrintCompletedReceiptAsync(record);
            }
        };

        _dialogService.Show(checkout);
    }

    private async Task PrintCompletedReceiptAsync(SaleTransactionRecord sale)
    {
        if (_documentPrintService is null || _printerSettings is null ||
            sale.BackendSaleId is not Guid saleId || saleId == Guid.Empty)
        {
            _toastService?.Show(
                "This completed sale does not have a canonical Server receipt available for printing.",
                ToastTone.Warning);
            return;
        }

        try
        {
            var profile = await _printerSettings.GetReceiptProfileAsync();
            if (profile is null)
            {
                _toastService?.Show(
                    "Sale completed. Choose and save a receipt printer in Settings before printing.",
                    ToastTone.Warning);
                return;
            }

            profile = profile with { ShowPreviewBeforePrint = true };
            var result = await _documentPrintService.PrintAsync(
                ProductionDocumentKind.PosSaleReceipt,
                saleId,
                profile,
                isReprint: false);
            if (result.Succeeded)
            {
                _toastService?.Show("Canonical sale receipt sent to the workstation preview/print flow.", ToastTone.Success);
            }
            else
            {
                var message = result.ErrorCode == "print.outcome_unknown"
                    ? "Receipt submission is unconfirmed. Check the printer before choosing Reprint in Sales History."
                    : result.ErrorCode is string errorCode
                        ? DesktopErrorPresentation.ForCode(errorCode, "Sale completed, but the receipt could not be printed.")
                        : result.ErrorMessage ?? "Sale completed, but the receipt could not be printed.";
                _toastService?.Show(message, ToastTone.Warning);
            }
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Sale completed, but the receipt could not be printed."),
                ToastTone.Warning);
        }
    }

    private string? ValidateCartStockForCommit()
    {
        if (CartItems.Count == 0)
        {
            return "Cannot complete sale: Cart is empty.";
        }

        if (_isBackendCatalog && SelectedCustomer is not null && SelectedCustomer.BackendId is null)
        {
            return "Selected customer is not attached to the production backend yet.";
        }

        foreach (var item in CartItems)
        {
            if (item.Quantity <= 0m)
            {
                return $"{item.Name} has an invalid sale quantity.";
            }

            if (_isBackendCatalog && item.Product.IsSerialized && !item.HasExactUnit)
            {
                return $"Serialized unit selection is required for {item.Name} before sale.";
            }

            if (item.HasExactUnit && item.Quantity != 1m)
            {
                return $"Exact-unit line {item.ExactIdentityDisplay} must have quantity 1.";
            }

            if (item.Quantity > item.Product.Stock)
            {
                return $"Stock changed for {item.Name}. Available: {item.Product.Stock:0.##} {item.Product.Unit}; requested: {item.Quantity:0.##}.";
            }
        }

        var duplicateUnit = CartItems
            .Where(item => item.InventoryUnitId is not null)
            .GroupBy(item => item.InventoryUnitId)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateUnit is not null)
        {
            return "The same physical unit appears more than once in the cart.";
        }

        return null;
    }

    private void OnSaleCompleted(SaleTransactionRecord record)
    {
#if DEBUG
        if (!_isBackendCatalog)
        {
            _retailState.ApplyLocalSaleStock(
                CartItems.Select(item => (item.Product, item.Quantity)),
                record.InvoiceNumber);
            // Production Thaka material issuance is owned by ThakaWorkspaceViewModel and its backend service.
        }
#endif

        _pendingSaleOperationId = null;
        CartItems.Clear();
        DiscountAmount = 0m;
        ApplyCustomerSelection(null);
        ResetDraftIdentity();

        RecalculateTotals();

        if (_posCatalogGateway is not null)
        {
            _ = LoadBackendCatalogAsync();
        }

        RequestFocusSearch();
    }

    private void OpenExactUnitPicker(PosProductItemViewModel product)
    {
        if (_workflowService is null || _dialogService is null ||
            product.BackendProductId is not Guid productId)
        {
            _toastService?.Show(
                "Exact-unit backend authority is unavailable.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new ExactUnitPickerViewModel(
            "Select Physical Unit",
            $"{product.Name} · {product.Sku}",
            productId,
            _workflowService,
            _dialogService,
            selected =>
            {
                if (selected.Count == 1)
                {
                    AddExactUnitToCart(product, selected[0]);
                }
            }));
    }

    private void AddExactUnitToCart(
        PosProductItemViewModel product,
        BackendExactUnit exactUnit)
    {
        if (exactUnit.ProductId != product.BackendProductId)
        {
            ScannerStatusMessage = "Scanned physical unit belongs to a different product.";
            _toastService?.Show(ScannerStatusMessage, ToastTone.Danger);
            return;
        }

        if (exactUnit.Status != EdgeRetails.Domain.Inventory.InventoryUnitStatus.InStock)
        {
            ScannerStatusMessage =
                $"{exactUnit.PrimaryIdentity} is not sellable. Current status: {exactUnit.Status}.";
            _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
            return;
        }

        if (CartItems.Any(x =>
                x.ExactUnit?.InventoryUnitId == exactUnit.InventoryUnitId))
        {
            ScannerStatusMessage =
                $"{exactUnit.PrimaryIdentity} is already in the cart.";
            _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
            return;
        }

        CartItems.Add(new PosCartItemViewModel(
            product,
            quantity: 1m,
            exactUnit: exactUnit,
            onChanged: _ => RecalculateTotals(),
            onRemove: RemoveCartItem,
            canOverridePrice: _canOverridePrice));
        ScannerStatusMessage = $"Added exact unit {exactUnit.PrimaryIdentity}.";
        RecalculateTotals();
    }

    private async Task ScanAsync()
    {
        if (_workflowService is null)
        {
            if (FilteredProducts.Count == 1)
            {
                AddToCart(FilteredProducts[0]);
            }
            return;
        }

        var input = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            ScannerStatusMessage = "Enter or scan an identity first.";
            return;
        }

        if (Interlocked.Exchange(ref _scanInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            var matches = await _workflowService.ResolveScannerAsync(input);
            if (matches.Count == 0)
            {
                ScannerStatusMessage = $"No authoritative match for '{input}'.";
                _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
                return;
            }

            if (matches.Count > 1)
            {
                ScannerStatusMessage =
                    $"Ambiguous scan: {matches.Count} authoritative matches. Refine the identity.";
                _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
                return;
            }

            var match = matches[0];
            var product = FindOrCreateProduct(match);
            if (match.InventoryUnitId is Guid exactId)
            {
                if (match.UnitStatus != EdgeRetails.Domain.Inventory.InventoryUnitStatus.InStock)
                {
                    ScannerStatusMessage =
                        $"Physical unit is unavailable ({match.UnitStatus}).";
                    _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
                    return;
                }

                var exact = (await _workflowService.GetExactUnitsAsync(
                        match.ProductId,
                        status: null))
                    .FirstOrDefault(x => x.InventoryUnitId == exactId);
                if (exact is null)
                {
                    ScannerStatusMessage =
                        "Exact unit resolved but authoritative detail is no longer available.";
                    _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
                    return;
                }

                AddExactUnitToCart(product, exact);
            }
            else if (match.IsSerialized)
            {
                OpenExactUnitPicker(product);
            }
            else
            {
                AddToCart(product);
                ScannerStatusMessage =
                    $"Resolved {match.Namespace}: {match.ProductName}.";
            }

            SearchText = string.Empty;
            RequestFocusSearch();
        }
        catch (BackendOperationException ex)
        {
            var safeMessage = DesktopErrorPresentation.ForException(
                ex,
                "The selected product could not be added. Refresh and try again.");
            ScannerStatusMessage = safeMessage;
            _toastService?.Show(safeMessage, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            var safeMessage = DesktopErrorPresentation.ForException(
                ex,
                "The selected product could not be added. Refresh and try again.");
            ScannerStatusMessage = safeMessage;
            _toastService?.Show(safeMessage, ToastTone.Danger);
        }
        finally
        {
            Volatile.Write(ref _scanInFlight, 0);
        }
    }

    private async Task PriceCheckAsync()
    {
        if (_dialogService is null) { return; }
        var input = SearchText.Trim();
        if (string.IsNullOrWhiteSpace(input) && SelectedCatalogProduct is not null)
        {
            input = SelectedCatalogProduct.Sku;
        }
        var priceCheck = new PriceCheckViewModel(
            input => _workflowService is null
                ? Task.FromException<IReadOnlyList<BackendScannerMatch>>(new InvalidOperationException("Authoritative Price Check is unavailable."))
                : _workflowService.ResolveScannerAsync(input),
            _dialogService, input);
        _dialogService.Show(priceCheck);
        if (!string.IsNullOrWhiteSpace(input)) { await priceCheck.CheckAsync(); }
    }

    private async Task<bool> SaveDraftAsync(bool holdAfterSave)
    {
        if (_draftSaveOutcomeUncertain)
        {
            _toastService?.Show(
                "Draft save result is unknown. Open Recent Drafts and resolve it before saving again.",
                ToastTone.Warning);
            return false;
        }

        if (_workflowService is null)
        {
            _toastService?.Show(
                "Backend POS Draft authority is unavailable.",
                ToastTone.Warning);
            return false;
        }

        if (CartItems.Count == 0)
        {
            _toastService?.Show("Cannot save an empty draft.", ToastTone.Warning);
            return false;
        }

        if (Interlocked.Exchange(ref _draftSaveInFlight, 1) != 0)
        {
            return false;
        }

        NotifyDraftSaveCommands();

        try
        {
            var inputs = CartItems.Select(item =>
            {
                var productId = item.Product.BackendProductId
                    ?? throw new BackendOperationException(
                        "sales.draft_product_invalid",
                        $"{item.Name} is not attached to backend.");
                var productUnitId = item.Product.BackendProductUnitId
                    ?? throw new BackendOperationException(
                        "sales.draft_product_unit_invalid",
                        $"{item.Name} has no backend selling unit.");

                return new BackendPosDraftItemInput(
                    productId,
                    productUnitId,
                    item.Quantity,
                    item.ExactUnit?.InventoryUnitId);
            }).ToArray();

            _pendingDraftOperationId ??= Guid.CreateVersion7();

            var saved = await _workflowService.SaveDraftAsync(
                _currentDraftId,
                _currentDraftVersion,
                SelectedCustomer?.BackendId,
                null,
                inputs,
                _pendingDraftOperationId.Value);

            _currentDraftId = saved.DraftId;
            _currentDraftVersion = saved.Version;
            _currentDraftNumber = saved.DraftNumber;
            _pendingDraftOperationId = null;
            _pendingSaleOperationId = null;
            OnPropertyChanged(nameof(IsResumedDraft));
            OnPropertyChanged(nameof(DraftStatusDisplay));
            _toastService?.Show(
                holdAfterSave
                    ? $"Draft {saved.DraftNumber} held."
                    : $"Draft {saved.DraftNumber} saved.",
                ToastTone.Success);

            if (holdAfterSave)
            {
                CartItems.Clear();
                DiscountAmount = 0m;
                ApplyCustomerSelection(null);
                ResetDraftIdentity();
                RecalculateTotals();
            }

            return true;
        }
        catch (BackendOperationException ex)
        {
            _pendingDraftOperationId = null;
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "The draft could not be saved."),
                ToastTone.Danger);
            return false;
        }
        catch (DesktopApiException ex) when (ex.Code.StartsWith("network.", StringComparison.OrdinalIgnoreCase))
        {
            _draftSaveOutcomeUncertain = true;
            ScannerStatusMessage = "Draft save result is unknown. Check Recent Drafts before taking another action.";
            ((RelayCommand)SaveDraftCommand).NotifyCanExecuteChanged();
            ((RelayCommand)HoldDraftCommand).NotifyCanExecuteChanged();
            _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
            return false;
        }
        catch (Exception)
        {
            _draftSaveOutcomeUncertain = true;
            ScannerStatusMessage = "Draft save result could not be verified. Check Recent Drafts before taking another action.";
            ((RelayCommand)SaveDraftCommand).NotifyCanExecuteChanged();
            ((RelayCommand)HoldDraftCommand).NotifyCanExecuteChanged();
            _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
            return false;
        }
        finally
        {
            Volatile.Write(ref _draftSaveInFlight, 0);
            NotifyDraftSaveCommands();
        }
    }

    private void NotifyDraftSaveCommands()
    {
        ((RelayCommand)SaveDraftCommand).NotifyCanExecuteChanged();
        ((RelayCommand)HoldDraftCommand).NotifyCanExecuteChanged();
    }

    private void OpenRecentDrafts()
    {
        if (_workflowService is null || _dialogService is null)
        {
            _toastService?.Show(
                "Backend POS Draft authority is unavailable.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new PosDraftsViewModel(
            _workflowService,
            _dialogService,
            draftId => _ = ResumeDraftAsync(draftId),
            _toastService));
    }

    private async Task ResumeDraftAsync(Guid draftId)
    {
        if (_workflowService is null)
        {
            return;
        }

        try
        {
            var detail = await _workflowService.GetDraftAsync(draftId)
                ?? throw new BackendOperationException(
                    "sales.draft_not_found",
                    "POS draft is no longer open.");

            if (_posCatalogGateway is not null && AllProducts.Count == 0)
            {
                await LoadBackendCatalogAsync();
            }

            var rebuilt = new List<PosCartItemViewModel>();
            foreach (var line in detail.Items)
            {
                PosProductItemViewModel? product;
                if (_posCatalogGateway is not null)
                {
                    var search = string.IsNullOrWhiteSpace(line.Sku)
                        ? line.ProductName
                        : line.Sku;
                    var row = (await _posCatalogGateway.LoadAsync(search, 200))
                        .FirstOrDefault(x =>
                            x.ProductId == line.ProductId &&
                            x.ProductUnitId == line.ProductUnitId);
                    product = row is null ? null : new PosProductItemViewModel(
                        id: row.ProductId.ToString("D"),
                        name: row.Name,
                        sku: row.Sku,
                        brand: "—",
                        category: row.Category,
                        stock: row.SellableStock,
                        price: row.UnitPrice,
                        unit: row.UnitSymbol,
                        backendProductId: row.ProductId,
                        backendProductUnitId: row.ProductUnitId,
                        isSerialized: row.IsSerialized);
                }
                else
                {
                    product = AllProducts.FirstOrDefault(x =>
                        x.BackendProductId == line.ProductId &&
                        x.BackendProductUnitId == line.ProductUnitId);
                }

                if (product is null)
                {
                    throw new BackendOperationException(
                        "sales.draft_product_unavailable",
                        $"Draft product '{line.ProductName}' is not available in the sellable catalog.");
                }

                if (line.Quantity > product.Stock)
                {
                    throw new BackendOperationException(
                        "sales.draft_stale_stock",
                        $"{product.Name} now has only {product.Stock:0.##} {product.Unit} available. Review the draft before sale.");
                }

                if (product.IsSerialized &&
                    (line.ExactUnit is null ||
                     line.ExactUnit.Status != EdgeRetails.Domain.Inventory.InventoryUnitStatus.InStock))
                {
                    throw new BackendOperationException(
                        "sales.draft_stale_unit",
                        $"Draft physical unit for {product.Name} is no longer available.");
                }

                rebuilt.Add(new PosCartItemViewModel(
                    product,
                    line.Quantity,
                    line.ExactUnit,
                    _ => RecalculateTotals(),
                    RemoveCartItem,
                    _canOverridePrice));
            }

            CartItems.Clear();
            foreach (var item in rebuilt)
            {
                CartItems.Add(item);
            }

            _currentDraftId = detail.Draft.DraftId;
            _currentDraftVersion = detail.Draft.Version;
            _currentDraftNumber = detail.Draft.DraftNumber;
            _pendingDraftOperationId = null;
            _draftSaveOutcomeUncertain = false;

            var customer = detail.Draft.CustomerId is Guid customerId
                ? _customers.FirstOrDefault(x => x.BackendId == customerId)
                : null;
            ApplyCustomerSelection(customer);

            OnPropertyChanged(nameof(IsResumedDraft));
            OnPropertyChanged(nameof(DraftStatusDisplay));
            RecalculateTotals();
            _toastService?.Show(
                $"Draft {detail.Draft.DraftNumber} resumed.",
                ToastTone.Success);
        }
        catch (BackendOperationException ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "The draft could not be cancelled."),
                ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "The draft could not be cancelled."),
                ToastTone.Danger);
        }
    }

    private PosProductItemViewModel FindOrCreateProduct(BackendScannerMatch match)
    {
        var product = AllProducts.FirstOrDefault(x =>
            x.BackendProductId == match.ProductId &&
            x.BackendProductUnitId == match.ProductUnitId);
        if (product is not null)
        {
            return product;
        }

        product = new PosProductItemViewModel(
            id: match.ProductId.ToString("D"),
            name: match.ProductName,
            sku: match.Sku,
            brand: match.Brand,
            category: match.Category,
            stock: match.SellableStock,
            price: match.UnitPrice,
            unit: match.UnitSymbol,
            backendProductId: match.ProductId,
            backendProductUnitId: match.ProductUnitId,
            isSerialized: match.IsSerialized);
        AllProducts.Add(product);
        ApplyFilters();
        return product;
    }

    private void ResetDraftIdentity()
    {
        _currentDraftId = null;
        _currentDraftVersion = null;
        _currentDraftNumber = null;
        OnPropertyChanged(nameof(IsResumedDraft));
        OnPropertyChanged(nameof(DraftStatusDisplay));
    }

    public void ChangeCustomer()
    {
        if (_dialogService is null)
        {
            _toastService?.Show("Customer picker is unavailable.", ToastTone.Warning);
            return;
        }

        if (_customers.Count == 0)
        {
            _toastService?.Show(
                "Authoritative customer directory is unavailable.",
                ToastTone.Warning);
            return;
        }

        _dialogService.Show(new SaleCustomerPickerViewModel(
            _customers,
            SelectedCustomer,
            ApplyCustomerSelection,
            _dialogService.Close));
    }

    private void ApplyCustomerSelection(CustomerDirectoryRecord? customer)
    {
        if (SelectedCustomer?.BackendId != customer?.BackendId)
        {
            ResetPendingSaleOperationWhenResolved();
        }
        SelectedCustomer = customer;
        CustomerName = customer?.Name ?? "Walk-in Customer";
        CustomerPhone = customer?.Phone ?? string.Empty;
    }

    private void RecalculateTotals()
    {
        // A changed cart/discount begins a new checkout intent after a cancelled dialog.
        ResetPendingSaleOperationWhenResolved();
        decimal sub = 0m;
        foreach (var item in CartItems)
        {
            sub += item.LineTotal;
        }

        Subtotal = Math.Round(sub, 2);
        Total = Math.Max(0m, Math.Round(Subtotal - DiscountAmount, 2));

        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(SubtotalDisplay));
        OnPropertyChanged(nameof(Total));
        OnPropertyChanged(nameof(TotalDisplay));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(HasCartItems));
        OnPropertyChanged(nameof(CartItemCountText));

        ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
        ((RelayCommand)SaveDraftCommand).NotifyCanExecuteChanged();
        ((RelayCommand)HoldDraftCommand).NotifyCanExecuteChanged();
    }

    private void ResetPendingSaleOperationWhenResolved()
    {
        if (_operationIntents is not null && _pendingSaleOperationId is not null)
        {
            _ = RefreshPendingSaleOperationIdAsync();
        }
    }

    private async Task RefreshPendingSaleOperationIdAsync()
    {
        if (_operationIntents is null)
        {
            _pendingSaleOperationId = null;
            _saleOperationIntentReady = true;
            ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
            return;
        }

        if (Interlocked.Exchange(ref _pendingSaleIntentReadInFlight, 1) != 0)
        {
            Volatile.Write(ref _pendingSaleIntentReadRequested, 1);
            _saleOperationIntentReady = false;
            ScannerStatusMessage = PendingSaleIntentCheckStatus;
            ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
            return;
        }

        _saleOperationIntentReady = false;
        ScannerStatusMessage = PendingSaleIntentCheckStatus;
        ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
        try
        {
            do
            {
                Volatile.Write(ref _pendingSaleIntentReadRequested, 0);
                var pending = await _operationIntents.FindPendingByPrefixAsync(
                    BackendTransactionService.SaleCheckoutIntentKey);
                _pendingSaleOperationId = pending.SingleOrDefault()?.OperationId;
            }
            while (Interlocked.Exchange(ref _pendingSaleIntentReadRequested, 0) != 0);

            _saleOperationIntentReady = true;
            if (string.Equals(ScannerStatusMessage, PendingSaleIntentCheckStatus, StringComparison.Ordinal))
            {
                ScannerStatusMessage = null;
            }
        }
        catch (Exception)
        {
            _saleOperationIntentReady = false;
            ScannerStatusMessage = "A previous sale outcome could not be verified. Checkout remains disabled until the local operation record can be read.";
            _toastService?.Show(ScannerStatusMessage, ToastTone.Warning);
        }
        finally
        {
            Interlocked.Exchange(ref _pendingSaleIntentReadInFlight, 0);
            ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
            if (Interlocked.Exchange(ref _pendingSaleIntentReadRequested, 0) != 0)
            {
                _ = RefreshPendingSaleOperationIdAsync();
            }
        }
    }

    private static ITransactionService ResolveTransactionService(
        ITransactionService? transactionService)
    {
        if (transactionService is not null)
        {
            return transactionService;
        }

#if DEBUG
        return DemoTransactionService.Instance;
#else
        throw new InvalidOperationException(
            "Production POS requires an authoritative transaction service.");
#endif
    }

    private static List<PosProductItemViewModel> CreateInitialProducts(
        IPosCatalogGateway? gateway)
    {
        if (gateway is not null)
        {
            return [];
        }

#if DEBUG
        // Initial catalog from _retailState.Products
        return DemoRetailState.Instance.Products;
#else
        return [];
#endif
    }

    private async Task LoadBackendCustomersAsync()
    {
        if (_businessOperationsService is null)
        {
            return;
        }

        try
        {
            var rows = await _businessOperationsService.GetCustomersAsync(
                null,
                200,
                CancellationToken.None);
            _customers.Clear();
            _customers.AddRange(rows);
        }
        catch (Exception ex)
        {
            _customers.Clear();
            _toastService?.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "Customer directory is unavailable. Check the connection and try again."),
                ToastTone.Warning);
        }
    }

    private async Task ScheduleCatalogSearchAsync(string search, bool immediate = false)
    {
        _afterCatalogName = null;
        _afterCatalogId = null;
        HasMoreCatalogProducts = false;

        var version = Interlocked.Increment(ref _catalogSearchVersion);
        var previous = Interlocked.Exchange(
            ref _catalogSearchCts,
            new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();

        var cts = _catalogSearchCts!;
        try
        {
            if (!immediate)
            {
                await Task.Delay(250, cts.Token);
            }
            await LoadBackendCatalogAsync(
                search,
                SelectedCategory,
                SelectedBrand,
                afterName: null,
                afterId: null,
                append: false,
                cancellationToken: cts.Token,
                expectedVersion: version);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _catalogSearchVersion))
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "Product search failed. Check the connection and try again."),
                ToastTone.Danger);
        }
    }

    private async Task LoadBackendCatalogAsync(
        string? search = null,
        string? category = null,
        string? brand = null,
        string? afterName = null,
        Guid? afterId = null,
        bool append = false,
        CancellationToken cancellationToken = default,
        long? expectedVersion = null)
    {
        if (_posCatalogGateway is null)
        {
            return;
        }

        try
        {
            var rows = await _posCatalogGateway.LoadAsync(
                string.IsNullOrWhiteSpace(search) ? null : search.Trim(),
                category,
                brand,
                200,
                afterName,
                afterId,
                cancellationToken);

            if (expectedVersion is long version &&
                (version != Volatile.Read(ref _catalogSearchVersion) ||
                 cancellationToken.IsCancellationRequested))
            {
                return;
            }

            if (!append)
            {
                AllProducts.Clear();
            }

            foreach (var row in rows)
            {
                if (append && AllProducts.Any(p => p.BackendProductId == row.ProductId))
                {
                    continue;
                }

                AllProducts.Add(new PosProductItemViewModel(
                    id: row.ProductId.ToString("D"),
                    name: row.Name,
                    sku: row.Sku,
                    brand: string.IsNullOrWhiteSpace(row.Brand) ? "—" : row.Brand,
                    category: row.Category,
                    stock: row.SellableStock,
                    price: row.UnitPrice,
                    unit: string.IsNullOrWhiteSpace(row.UnitSymbol)
                        ? "Pcs"
                        : row.UnitSymbol,
                    cost: row.ReferenceCost,
                    minimumStock: 0m,
                    backendProductId: row.ProductId,
                    backendProductUnitId: row.ProductUnitId,
                    isSerialized: row.IsSerialized));
            }

            if (rows.Count == 200)
            {
                _afterCatalogName = rows[^1].Name;
                _afterCatalogId = rows[^1].ProductId;
                HasMoreCatalogProducts = true;
            }
            else
            {
                _afterCatalogName = null;
                _afterCatalogId = null;
                HasMoreCatalogProducts = false;
            }

            var distinctBrands = rows.Select(row => row.Brand).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b!).Distinct().ToArray();
            MergeFilterValues(Categories, rows.Select(row => row.Category));
            MergeFilterValues(Brands, distinctBrands);
            _hasAuthoritativeCatalogFilters = true;
            OnPropertyChanged(nameof(SupportsCatalogFilters));
            OnPropertyChanged(nameof(CatalogReadLimitNotice));
            OnPropertyChanged(nameof(FilteredProductCountText));

            ApplyFilters();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (expectedVersion is null ||
                                   expectedVersion.Value == Volatile.Read(ref _catalogSearchVersion))
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "The product catalog is unavailable. Check the connection and try again."),
                ToastTone.Danger);
        }
    }

    private async Task LoadMoreCatalogAsync()
    {
        if (_posCatalogGateway is null || !HasMoreCatalogProducts || Interlocked.CompareExchange(ref _isLoadingMoreCatalog, 1, 0) != 0)
        {
            return;
        }

        IsLoadingMoreCatalog = true;
        try
        {
            var version = Volatile.Read(ref _catalogSearchVersion);
            var cts = _catalogSearchCts;
            var ct = cts?.Token ?? CancellationToken.None;

            await LoadBackendCatalogAsync(
                search: SearchText,
                category: SelectedCategory,
                brand: SelectedBrand,
                afterName: _afterCatalogName,
                afterId: _afterCatalogId,
                append: true,
                cancellationToken: ct,
                expectedVersion: version);
        }
        finally
        {
            Interlocked.Exchange(ref _isLoadingMoreCatalog, 0);
            IsLoadingMoreCatalog = false;
        }
    }

    private static void MergeFilterValues(
        ObservableCollection<string> target,
        IEnumerable<string?> values)
    {
        foreach (var value in values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            if (!target.Any(t => string.Equals(t, value, StringComparison.OrdinalIgnoreCase)))
            {
                target.Add(value);
            }
        }
    }

    private static void ReplaceFilterValues(
        ObservableCollection<string> target,
        IEnumerable<string> values)
    {
        target.Clear();
        target.Add("All");

        foreach (var value in values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            target.Add(value);
        }
    }

#if DEBUG
    private static IEnumerable<string> AllDemoCategories() =>
        DemoRetailState.Instance.Products.Select(product => product.Category);

    private static IEnumerable<string> AllDemoBrands() =>
        DemoRetailState.Instance.Products.Select(product => product.Brand);
#endif

    private void ApplyFilters()
    {
        var query = AllProducts.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(p =>
                p.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Sku.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        if (SupportsCatalogFilters && !string.Equals(SelectedCategory, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p =>
                string.Equals(p.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (SupportsCatalogFilters && !string.Equals(SelectedBrand, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p =>
                string.Equals(p.Brand, SelectedBrand, StringComparison.OrdinalIgnoreCase));
        }

        FilteredProducts.Clear();
        foreach (var prod in query)
        {
            FilteredProducts.Add(prod);
        }

        OnPropertyChanged(nameof(FilteredProductCountText));
        OnPropertyChanged(nameof(HasNoMatchingProducts));
    }

}
