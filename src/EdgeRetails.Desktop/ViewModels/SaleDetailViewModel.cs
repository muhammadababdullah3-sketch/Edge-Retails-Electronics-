using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Controls;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class SaleLineItemViewModel : ViewModelBase
{
    private decimal _quantity;
    private decimal _unitPrice;
    private decimal _discount;
    private decimal _returnedQuantity;

    public SaleLineItemViewModel(
        int lineNumber,
        string productId,
        string productName,
        string sku,
        string brand,
        decimal unitPrice,
        decimal quantity,
        decimal discount = 0m,
        decimal returnedQuantity = 0m)
    {
        LineNumber = lineNumber;
        ProductId = productId;
        ProductName = productName;
        Sku = sku;
        Brand = brand;
        _unitPrice = unitPrice;
        _quantity = quantity;
        _discount = discount;
        _returnedQuantity = returnedQuantity;
    }

    public int LineNumber { get; set; }
    public string ProductId { get; }
    public string ProductName { get; }
    public string Sku { get; }
    public string Brand { get; }

    public string ProductDisplay => string.IsNullOrWhiteSpace(Brand)
        ? ProductName
        : $"{ProductName} · {Brand}";

    public string SkuDisplay => string.IsNullOrWhiteSpace(Sku) ? "—" : Sku;

    public decimal UnitPrice
    {
        get => _unitPrice;
        set
        {
            if (SetProperty(ref _unitPrice, value))
            {
                NotifyFinancialsChanged();
            }
        }
    }

    public string UnitPriceDisplay => $"Rs. {UnitPrice:N0}";

    public decimal Quantity
    {
        get => _quantity;
        set
        {
            if (SetProperty(ref _quantity, value))
            {
                OnPropertyChanged(nameof(QuantityDisplay));
                NotifyFinancialsChanged();
                NotifyReturnEligibilityChanged();
            }
        }
    }

    public string QuantityDisplay => Quantity.ToString("0.##", CultureInfo.InvariantCulture);

    public decimal Discount
    {
        get => _discount;
        set
        {
            if (SetProperty(ref _discount, value))
            {
                OnPropertyChanged(nameof(DiscountDisplay));
                NotifyFinancialsChanged();
            }
        }
    }

    public string DiscountDisplay => $"Rs. {Discount:N0}";
    public decimal LineTotal => Math.Max(0m, Math.Round((UnitPrice * Quantity) - Discount, 2));
    public string LineTotalDisplay => $"Rs. {LineTotal:N0}";
    public decimal RefundUnitPrice => Quantity > 0m ? Math.Round(LineTotal / Quantity, 2) : 0m;

    public decimal ReturnedQuantity
    {
        get => _returnedQuantity;
        set
        {
            var clamped = Math.Clamp(value, 0m, Quantity);
            if (SetProperty(ref _returnedQuantity, clamped))
            {
                OnPropertyChanged(nameof(ReturnedQuantityDisplay));
                NotifyReturnEligibilityChanged();
            }
        }
    }

    public string ReturnedQuantityDisplay => ReturnedQuantity.ToString("0.##", CultureInfo.InvariantCulture);
    public decimal EligibleReturnQuantity => Math.Max(0m, Quantity - ReturnedQuantity);
    public string EligibleReturnQuantityDisplay => EligibleReturnQuantity.ToString("0.##", CultureInfo.InvariantCulture);
    public bool IsFullyReturned => EligibleReturnQuantity <= 0m;

    private void NotifyFinancialsChanged()
    {
        OnPropertyChanged(nameof(UnitPriceDisplay));
        OnPropertyChanged(nameof(LineTotal));
        OnPropertyChanged(nameof(LineTotalDisplay));
        OnPropertyChanged(nameof(RefundUnitPrice));
    }

    private void NotifyReturnEligibilityChanged()
    {
        OnPropertyChanged(nameof(EligibleReturnQuantity));
        OnPropertyChanged(nameof(EligibleReturnQuantityDisplay));
        OnPropertyChanged(nameof(IsFullyReturned));
    }
}

public sealed class SaleTransactionItemViewModel : ViewModelBase
{
    private PaymentState _paymentState;
    private SaleReturnState _returnState;
    private decimal _discountAmount;
    private decimal _paymentReceived;
    private decimal _totalReturnedAmount;

    public SaleTransactionItemViewModel(
        int invoiceNumber,
        DateTime transactionDate,
        string customerName,
        string customerPhone,
        string cashierName,
        string paymentMethod,
        IEnumerable<SaleLineItemViewModel> lineItems,
        decimal discountAmount = 0m,
        decimal? paymentReceived = null,
        PaymentState paymentState = PaymentState.Paid,
        SaleReturnState returnState = SaleReturnState.None,
        decimal totalReturnedAmount = 0m)
    {
        InvoiceNumber = invoiceNumber;
        TransactionDate = transactionDate;
        CustomerName = customerName;
        CustomerPhone = customerPhone;
        CashierName = cashierName;
        PaymentMethod = paymentMethod;
        _discountAmount = Math.Max(0m, discountAmount);
        _paymentState = paymentState;
        _returnState = returnState;
        _totalReturnedAmount = Math.Max(0m, totalReturnedAmount);

        LineItems = new ObservableCollection<SaleLineItemViewModel>(lineItems);
        _paymentReceived = paymentReceived ?? TotalAmount;
    }

    public int InvoiceNumber { get; }
    public string InvoiceDisplay => $"#{InvoiceNumber}";
    public DateTime TransactionDate { get; }
    public string DateDisplay => TransactionDate.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    public string TimeDisplay => TransactionDate.ToString("hh:mm tt", CultureInfo.InvariantCulture);
    public string DateTimeDisplay => $"{DateDisplay}, {TimeDisplay}";

    public string CustomerName { get; set; }
    public string CustomerPhone { get; set; }
    public string CustomerDisplay => string.IsNullOrWhiteSpace(CustomerPhone)
        ? CustomerName
        : $"{CustomerName} ({CustomerPhone})";
    public string CashierName { get; set; }
    public string PaymentMethod { get; set; }

    public ObservableCollection<SaleLineItemViewModel> LineItems { get; }
    public int ItemsCount => LineItems.Sum(i => (int)Math.Ceiling(i.Quantity));
    public string ItemsCountDisplay => $"{ItemsCount} {(ItemsCount == 1 ? "item" : "items")}";

    public decimal Subtotal => LineItems.Sum(i => i.LineTotal);
    public string SubtotalDisplay => $"Rs. {Subtotal:N0}";

    public decimal DiscountAmount
    {
        get => _discountAmount;
        set
        {
            if (SetProperty(ref _discountAmount, Math.Max(0m, value)))
            {
                OnPropertyChanged(nameof(DiscountDisplay));
                NotifyInvoiceTotalsChanged();
            }
        }
    }

    public string DiscountDisplay => $"Rs. {DiscountAmount:N0}";
    public decimal TotalAmount => Math.Max(0m, Math.Round(Subtotal - DiscountAmount, 2));
    public string TotalAmountDisplay => $"Rs. {TotalAmount:N0}";

    public decimal PaymentReceived
    {
        get => _paymentReceived;
        set
        {
            if (SetProperty(ref _paymentReceived, Math.Max(0m, value)))
            {
                OnPropertyChanged(nameof(PaymentReceivedDisplay));
                OnPropertyChanged(nameof(ChangeReturned));
                OnPropertyChanged(nameof(ChangeReturnedDisplay));
            }
        }
    }

    public string PaymentReceivedDisplay => $"Rs. {PaymentReceived:N0}";
    public decimal ChangeReturned => Math.Max(0m, Math.Round(PaymentReceived - TotalAmount, 2));
    public string ChangeReturnedDisplay => $"Rs. {ChangeReturned:N0}";

    public PaymentState PaymentState
    {
        get => _paymentState;
        private set
        {
            if (SetProperty(ref _paymentState, value))
            {
                NotifyStatusChanged();
            }
        }
    }

    public SaleReturnState ReturnState
    {
        get => _returnState;
        private set
        {
            if (SetProperty(ref _returnState, value))
            {
                NotifyStatusChanged();
            }
        }
    }

    public string Status => ReturnState switch
    {
        SaleReturnState.Refunded => "REFUNDED",
        SaleReturnState.Partial => "PARTIAL RETURN",
        _ when PaymentState == PaymentState.Partial => "PARTIAL PAYMENT",
        _ => "PAID"
    };

    public BadgeTone StatusTone => ReturnState switch
    {
        SaleReturnState.Refunded => BadgeTone.Danger,
        SaleReturnState.Partial => BadgeTone.Warning,
        _ when PaymentState == PaymentState.Partial => BadgeTone.Warning,
        _ => BadgeTone.Success
    };

    public bool IsPaid => PaymentState == PaymentState.Paid && ReturnState == SaleReturnState.None;
    public bool IsPartialPayment => PaymentState == PaymentState.Partial;
    public bool IsPartiallyReturned => ReturnState == SaleReturnState.Partial;
    public bool IsRefunded => ReturnState == SaleReturnState.Refunded;

    public decimal TotalReturnedAmount
    {
        get => _totalReturnedAmount;
        private set
        {
            if (SetProperty(ref _totalReturnedAmount, Math.Max(0m, value)))
            {
                OnPropertyChanged(nameof(TotalReturnedAmountDisplay));
                OnPropertyChanged(nameof(HasReturns));
            }
        }
    }

    public string TotalReturnedAmountDisplay => $"Rs. {TotalReturnedAmount:N0}";
    public bool HasReturns => TotalReturnedAmount > 0m;
    public bool HasInvoiceLevelDiscount => DiscountAmount > 0m;

    public void ApplyReturnProjection(decimal cumulativeRefund, SaleReturnState returnState)
    {
        TotalReturnedAmount = cumulativeRefund;
        ReturnState = returnState;
        NotifyTotalsChanged();
    }

    public void NotifyTotalsChanged()
    {
        OnPropertyChanged(nameof(ItemsCount));
        OnPropertyChanged(nameof(ItemsCountDisplay));
        OnPropertyChanged(nameof(Subtotal));
        OnPropertyChanged(nameof(SubtotalDisplay));
        NotifyInvoiceTotalsChanged();
        NotifyStatusChanged();
        OnPropertyChanged(nameof(TotalReturnedAmount));
        OnPropertyChanged(nameof(TotalReturnedAmountDisplay));
        OnPropertyChanged(nameof(HasReturns));
    }

    private void NotifyInvoiceTotalsChanged()
    {
        OnPropertyChanged(nameof(TotalAmount));
        OnPropertyChanged(nameof(TotalAmountDisplay));
        OnPropertyChanged(nameof(ChangeReturned));
        OnPropertyChanged(nameof(ChangeReturnedDisplay));
        OnPropertyChanged(nameof(HasInvoiceLevelDiscount));
    }

    private void NotifyStatusChanged()
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(IsPaid));
        OnPropertyChanged(nameof(IsPartialPayment));
        OnPropertyChanged(nameof(IsPartiallyReturned));
        OnPropertyChanged(nameof(IsRefunded));
    }
}

public sealed class SaleDetailViewModel : ViewModelBase
{
    private readonly INavigationService? _navigationService;
    private readonly IToastService? _toastService;
    private readonly IDialogService? _dialogService;
    private readonly ITransactionService _transactionService;
    private readonly Action? _onBack;
    private readonly Action? _onSaleUpdated;

    private SalesReturnViewModel? _activeReturnViewModel;
    private bool _isReturnDrawerOpen;

    public SaleDetailViewModel(
        SaleTransactionItemViewModel sale,
        INavigationService? navigationService = null,
        IToastService? toastService = null,
        IDrawerService? drawerService = null,
        IDialogService? dialogService = null,
        Action? onBack = null,
        Action? onSaleUpdated = null,
        ITransactionService? transactionService = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        Sale = sale;
        _navigationService = navigationService;
        _toastService = toastService;
        _dialogService = dialogService;
        _transactionService = transactionService ?? DemoTransactionService.Instance;
        _onBack = onBack;
        _onSaleUpdated = onSaleUpdated;

        BackCommand = new RelayCommand(GoBack);
        PrintReceiptCommand = new RelayCommand(PrintReceipt);
        ReturnItemsCommand = new RelayCommand(OpenReturnDrawer, () => CanReturnItems);
        CloseReturnDrawerCommand = new RelayCommand(CloseReturnDrawer);
    }

    public SaleTransactionItemViewModel Sale { get; }
    public int InvoiceNumber => Sale.InvoiceNumber;
    public string InvoiceDisplay => Sale.InvoiceDisplay;
    public string BreadcrumbText => $"Sales History  /  Invoice #{InvoiceNumber}";
    public string Status => Sale.Status;
    public BadgeTone StatusTone => Sale.StatusTone;
    public string DateDisplay => Sale.DateDisplay;
    public string TimeDisplay => Sale.TimeDisplay;
    public string CashierName => Sale.CashierName;
    public string CustomerName => Sale.CustomerName;
    public string CustomerPhone => Sale.CustomerPhone;
    public string CustomerDisplay => Sale.CustomerDisplay;
    public string PaymentMethod => Sale.PaymentMethod;
    public ObservableCollection<SaleLineItemViewModel> LineItems => Sale.LineItems;
    public string SubtotalDisplay => Sale.SubtotalDisplay;
    public string DiscountDisplay => Sale.DiscountDisplay;
    public string GrandTotalDisplay => Sale.TotalAmountDisplay;
    public string PaymentReceivedDisplay => $"{Sale.PaymentReceivedDisplay} ({Sale.PaymentMethod})";
    public string ChangeReturnedDisplay => Sale.ChangeReturnedDisplay;
    public bool HasReturns => Sale.HasReturns;
    public string TotalReturnedDisplay => Sale.TotalReturnedAmountDisplay;
    public bool CanReturnItems => Sale.LineItems.Any(i => i.EligibleReturnQuantity > 0);

    public SalesReturnViewModel? ActiveReturnViewModel
    {
        get => _activeReturnViewModel;
        private set => SetProperty(ref _activeReturnViewModel, value);
    }

    public bool IsReturnDrawerOpen
    {
        get => _isReturnDrawerOpen;
        private set => SetProperty(ref _isReturnDrawerOpen, value);
    }

    public ICommand BackCommand { get; }
    public ICommand PrintReceiptCommand { get; }
    public ICommand ReturnItemsCommand { get; }
    public ICommand CloseReturnDrawerCommand { get; }

    public void GoBack()
    {
        if (_onBack != null)
        {
            _onBack.Invoke();
            return;
        }

        _navigationService?.Navigate(NavigationTarget.SalesHistory);
    }

    public void PrintReceipt()
    {
        _toastService?.Show(
            $"Receipt print for Invoice #{InvoiceNumber} is a UI preview. Printer integration is deferred to Sprint 8.",
            ToastTone.Info);
    }

    public void OpenReturnDrawer()
    {
        if (!CanReturnItems)
        {
            _toastService?.Show(
                $"All items in Invoice #{InvoiceNumber} have already been returned.",
                ToastTone.Warning);
            return;
        }

        ActiveReturnViewModel = new SalesReturnViewModel(
            Sale,
            onReturnProcessed: OnReturnProcessed,
            onClose: CloseReturnDrawer,
            toastService: _toastService,
            dialogService: _dialogService,
            transactionService: _transactionService);

        IsReturnDrawerOpen = true;
    }

    public void CloseReturnDrawer()
    {
        IsReturnDrawerOpen = false;
        ActiveReturnViewModel = null;
    }

    private void OnReturnProcessed(SalesReturnResult result)
    {
        Sale.ApplyReturnProjection(result.CumulativeRefundAmount, result.ReturnState);

        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusTone));
        OnPropertyChanged(nameof(CanReturnItems));
        OnPropertyChanged(nameof(HasReturns));
        OnPropertyChanged(nameof(TotalReturnedDisplay));

        ((RelayCommand)ReturnItemsCommand).NotifyCanExecuteChanged();
        _onSaleUpdated?.Invoke();
    }
}
