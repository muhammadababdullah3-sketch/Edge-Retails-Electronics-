using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Controls;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public enum PosSaleMode
{
    NormalSale,
    ThakaMaterialIssue
}

/// <summary>
/// ViewModel for POS / New Sale screen supporting both Normal Sale and Thaka Material Issue modes.
/// </summary>
public sealed class NewSaleViewModel : ViewModelBase
{
    private readonly IToastService? _toastService;
    private readonly IDialogService? _dialogService;
    private readonly ITransactionService _transactionService;
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;
    private readonly string _cashierName;

    private PosSaleMode _mode = PosSaleMode.NormalSale;
    private PosSaleMode? _pendingTargetMode;
    private bool _isModeSwitchConfirmationOpen;

    private string _searchText = string.Empty;
    private string _selectedCategory = "All";
    private string _selectedBrand = "All";
    private string _customerName = "Walk-in Customer";
    private decimal _discountAmount;

    private ThakaProjectListItemViewModel? _selectedThakaProject;
    private int _invoiceCounter = 1293;

    public event EventHandler? FocusSearchRequested;

    public NewSaleViewModel()
        : this(toastService: null, dialogService: null, transactionService: null, sessionContext: null)
    {
    }

    public NewSaleViewModel(
        IToastService? toastService = null,
        IDialogService? dialogService = null,
        ITransactionService? transactionService = null,
        ISessionContext? sessionContext = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _transactionService = transactionService ?? DemoTransactionService.Instance;
        _cashierName = sessionContext != null
            ? $"{sessionContext.DisplayName}, {sessionContext.RoleName}"
            : "Abdullah, Owner";

        // Categories filter
        Categories = new ReadOnlyCollection<string>(
            ["All", "Lighting", "Switches", "Breakers", "Cables", "Accessories"]);

        // Brands filter
        Brands = new ReadOnlyCollection<string>(
            ["All", "Philips", "Legrand", "Schneider", "Pak Cables", "MK", "Hager", "Havells", "Commscope"]);
        ThakaProjects = new ReadOnlyCollection<ThakaProjectListItemViewModel>([.. _retailState.GetActiveProjects()]);

        AllProducts = _retailState.Products;
        FilteredProducts = new ObservableCollection<PosProductItemViewModel>(AllProducts);
        CartItems = [];

        // Commands
        SwitchToNormalSaleCommand = new RelayCommand(() => RequestModeChange(PosSaleMode.NormalSale));
        SwitchToThakaCommand = new RelayCommand(() => RequestModeChange(PosSaleMode.ThakaMaterialIssue));
        ConfirmModeSwitchCommand = new RelayCommand(ConfirmModeSwitch);
        CancelModeSwitchCommand = new RelayCommand(CancelModeSwitch);

        AddToCartCommand = new RelayCommand<PosProductItemViewModel>(AddToCart);
        IncrementCartItemCommand = new RelayCommand<PosCartItemViewModel>(item => item.Increment());
        DecrementCartItemCommand = new RelayCommand<PosCartItemViewModel>(item => item.Decrement());
        RemoveCartItemCommand = new RelayCommand<PosCartItemViewModel>(RemoveCartItem);
        ClearCartCommand = new RelayCommand(ClearCart);

        CompleteSaleCommand = new RelayCommand(CompleteSale, () => IsNormalSaleMode && CartItems.Count > 0 && Total > 0m);
        IssueMaterialToThakaCommand = new RelayCommand(
            IssueMaterialToThaka,
            () => IsThakaMode && SelectedThakaProject != null && CartItems.Count > 0);

        ChangeCustomerCommand = new RelayCommand(ChangeCustomer);
        FocusSearchCommand = new RelayCommand(RequestFocusSearch);

        // Sales terminal starts with a clean, empty cart ready for new transactions
        CartItems.Clear();
        RecalculateTotals();
    }

    public PosSaleMode Mode
    {
        get => _mode;
        private set
        {
            if (SetProperty(ref _mode, value))
            {
                OnPropertyChanged(nameof(IsNormalSaleMode));
                OnPropertyChanged(nameof(IsThakaMode));
                OnPropertyChanged(nameof(CanIssueThakaMaterial));
                ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
                ((RelayCommand)IssueMaterialToThakaCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsNormalSaleMode => Mode == PosSaleMode.NormalSale;

    public bool IsThakaMode => Mode == PosSaleMode.ThakaMaterialIssue;

    public bool IsModeSwitchConfirmationOpen
    {
        get => _isModeSwitchConfirmationOpen;
        private set => SetProperty(ref _isModeSwitchConfirmationOpen, value);
    }

    public PosSaleMode? PendingTargetMode
    {
        get => _pendingTargetMode;
        private set => SetProperty(ref _pendingTargetMode, value);
    }

    public string ModeSwitchTargetName =>
        PendingTargetMode == PosSaleMode.ThakaMaterialIssue ? "Thaka Material Issue" : "Normal Sale";

    public IReadOnlyList<string> Categories { get; }

    public IReadOnlyList<string> Brands { get; }

    public IReadOnlyList<ThakaProjectListItemViewModel> ThakaProjects { get; }

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
                ApplyFilters();
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
            }
        }
    }

    public string FilteredProductCountText =>
        $"{FilteredProducts.Count} {(FilteredProducts.Count == 1 ? "product" : "products")}";

    public bool HasNoMatchingProducts => FilteredProducts.Count == 0;

    public string CartItemCountText =>
        CartItems.Count == 0 ? "Empty" : $"{CartItems.Count} {(CartItems.Count == 1 ? "item" : "items")}";

    public bool IsCartEmpty => CartItems.Count == 0;

    public bool HasCartItems => CartItems.Count > 0;

    public string CustomerName
    {
        get => _customerName;
        set => SetProperty(ref _customerName, value);
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

    public string SubtotalDisplay => $"Rs. {Subtotal:N0}";

    public decimal Total { get; private set; }

    public string TotalDisplay => $"Rs. {Total:N0}";

    public ThakaProjectListItemViewModel? SelectedThakaProject
    {
        get => _selectedThakaProject;
        set
        {
            if (SetProperty(ref _selectedThakaProject, value))
            {
                OnPropertyChanged(nameof(HasSelectedThakaProject));
                OnPropertyChanged(nameof(CanIssueThakaMaterial));
                ((RelayCommand)IssueMaterialToThakaCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasSelectedThakaProject => SelectedThakaProject != null;

    public decimal MaterialSubtotal => Subtotal;

    public string MaterialSubtotalDisplay => $"Rs. {MaterialSubtotal:N0}";

    public decimal MaterialValue => MaterialSubtotal;

    public string MaterialValueDisplay => $"Rs. {MaterialValue:N0}";

    public bool CanIssueThakaMaterial =>
        IsThakaMode && HasSelectedThakaProject && CartItems.Count > 0;

    public int InvoiceNumber => _invoiceCounter;

    public string InvoiceDisplay => $"Invoice #{_invoiceCounter}";

    // Commands
    public ICommand SwitchToNormalSaleCommand { get; }

    public ICommand SwitchToThakaCommand { get; }

    public ICommand ConfirmModeSwitchCommand { get; }

    public ICommand CancelModeSwitchCommand { get; }

    public ICommand AddToCartCommand { get; }

    public ICommand IncrementCartItemCommand { get; }

    public ICommand DecrementCartItemCommand { get; }

    public ICommand RemoveCartItemCommand { get; }

    public ICommand ClearCartCommand { get; }

    public ICommand CompleteSaleCommand { get; }

    public ICommand IssueMaterialToThakaCommand { get; }

    public ICommand ChangeCustomerCommand { get; }

    public ICommand FocusSearchCommand { get; }

    public void RequestFocusSearch()
    {
        FocusSearchRequested?.Invoke(this, EventArgs.Empty);
    }

    public void RequestModeChange(PosSaleMode targetMode)
    {
        if (Mode == targetMode)
        {
            return;
        }

        // Safety rule: if cart has items, require confirmation before context switch
        if (CartItems.Count > 0)
        {
            PendingTargetMode = targetMode;
            IsModeSwitchConfirmationOpen = true;
            return;
        }

        Mode = targetMode;
    }

    private void ConfirmModeSwitch()
    {
        if (PendingTargetMode.HasValue)
        {
            CartItems.Clear();
            RecalculateTotals();
            Mode = PendingTargetMode.Value;
            PendingTargetMode = null;
            IsModeSwitchConfirmationOpen = false;

            _toastService?.Show(
                Mode == PosSaleMode.ThakaMaterialIssue
                    ? "Switched to Thaka Material Issue mode. Cart cleared."
                    : "Switched to Normal Sale mode. Cart cleared.",
                ToastTone.Info);
        }
    }

    private void CancelModeSwitch()
    {
        PendingTargetMode = null;
        IsModeSwitchConfirmationOpen = false;
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

        var existing = CartItems.FirstOrDefault(c => c.ProductId == product.Id);
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
            var newItem = new PosCartItemViewModel(
                product,
                quantity: initialQuantity,
                onChanged: _ => RecalculateTotals(),
                onRemove: RemoveCartItem);

            CartItems.Add(newItem);
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
                ProductName = item.Name,
                Sku = item.Sku,
                Brand = item.Brand,
                UnitPrice = item.UnitPrice,
                Quantity = item.Quantity,
                Discount = 0m,
                LineTotal = item.LineTotal,
                UnitCostSnapshot = item.Product.Cost
            })
            .ToArray();

        var checkout = new CompleteSaleViewModel(
            totalToPay: Total,
            transactionService: _transactionService,
            dialogService: _dialogService,
            toastService: _toastService,
            customerName: CustomerName,
            customerPhone: string.Empty,
            cashierName: _cashierName,
            items: items,
            subtotal: Subtotal,
            discountAmount: DiscountAmount,
            preCommitValidation: ValidateCartStockForCommit)
        {
            OnSaleCompleted = OnSaleCompleted
        };

        _dialogService.Show(checkout);
    }

    private string? ValidateCartStockForCommit()
    {
        if (CartItems.Count == 0)
        {
            return "Cannot complete sale: Cart is empty.";
        }

        foreach (var item in CartItems)
        {
            if (item.Quantity <= 0m)
            {
                return $"{item.Name} has an invalid sale quantity.";
            }

            if (item.Quantity > item.Product.Stock)
            {
                return $"Stock changed for {item.Name}. Available: {item.Product.Stock:0.##} {item.Product.Unit}; requested: {item.Quantity:0.##}.";
            }
        }

        return null;
    }

    private void OnSaleCompleted(SaleTransactionRecord record)
    {
        _retailState.ApplyLocalSaleStock(
            CartItems.Select(item => (item.Product, item.Quantity)),
            record.InvoiceNumber);

        CartItems.Clear();
        DiscountAmount = 0m;

        if (int.TryParse(record.InvoiceNumber.TrimStart('#'), out var invoiceNumber))
        {
            _invoiceCounter = invoiceNumber + 1;
            OnPropertyChanged(nameof(InvoiceNumber));
            OnPropertyChanged(nameof(InvoiceDisplay));
        }

        RecalculateTotals();
        RequestFocusSearch();
    }

    public void IssueMaterialToThaka()
    {
        if (!IsThakaMode)
        {
            return;
        }

        if (SelectedThakaProject == null)
        {
            _toastService?.Show("Please select an active Thaka project first.", ToastTone.Warning);
            return;
        }

        if (CartItems.Count == 0)
        {
            _toastService?.Show("Material cart is empty.", ToastTone.Warning);
            return;
        }

        var project = SelectedThakaProject;
        var valueFormatted = MaterialValueDisplay;
        var itemCount = CartItems.Count;

        try
        {
            _retailState.IssueMaterialBatch(
                project,
                CartItems.Select(item => (item.Product, item.Quantity)));

            CartItems.Clear();
            RecalculateTotals();

            _toastService?.Show(
                $"Successfully issued {itemCount} items ({valueFormatted}) to project '{project.ProjectName}'.",
                ToastTone.Success);
        }
        catch (Exception ex)
        {
            _toastService?.Show(ex.Message, ToastTone.Danger);
        }
    }

    public void ChangeCustomer()
    {
        _toastService?.Show("Customer change dialog (F4). Default: Walk-in Customer.", ToastTone.Neutral);
    }

    private void RecalculateTotals()
    {
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
        OnPropertyChanged(nameof(MaterialSubtotal));
        OnPropertyChanged(nameof(MaterialSubtotalDisplay));
        OnPropertyChanged(nameof(MaterialValue));
        OnPropertyChanged(nameof(MaterialValueDisplay));
        OnPropertyChanged(nameof(IsCartEmpty));
        OnPropertyChanged(nameof(HasCartItems));
        OnPropertyChanged(nameof(CartItemCountText));
        OnPropertyChanged(nameof(CanIssueThakaMaterial));

        ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
        ((RelayCommand)IssueMaterialToThakaCommand).NotifyCanExecuteChanged();
    }

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

        if (!string.Equals(SelectedCategory, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p =>
                string.Equals(p.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(SelectedBrand, "All", StringComparison.OrdinalIgnoreCase))
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

    private void PreloadDemoCartItems()
    {
        // Add the 6 items matching Figma default nodes 14:1070 & 14:2326:
        // 1) LED Bulb 12W (Rs. 500)
        // 2) Switch 16A (Rs. 300)
        // 3) Breaker 32A (Rs. 1,450)
        // 4) Wire 2.5mm (Rs. 6,500)
        // 5) Fan Regulator (Rs. 750)
        // 6) MCB 20A (Rs. 850)
        // Total = Rs. 10,350
        var demoProductIds = new[] { "P1", "P2", "P3", "P4", "P7", "P6" };
        foreach (var id in demoProductIds)
        {
            var prod = AllProducts.FirstOrDefault(p => p.Id == id);
            if (prod != null)
            {
                CartItems.Add(new PosCartItemViewModel(
                    prod,
                    quantity: 1m,
                    onChanged: _ => RecalculateTotals(),
                    onRemove: RemoveCartItem));
            }
        }
    }
}
