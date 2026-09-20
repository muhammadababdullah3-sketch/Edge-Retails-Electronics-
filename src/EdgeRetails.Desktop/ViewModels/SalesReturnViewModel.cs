using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public enum ReturnDisposition
{
    Defective,
    Damaged,
    CustomerChangedMind,
    Other
}

public sealed class ReturnReasonOptionViewModel(ReturnDisposition disposition, string title, string description, string stockImpact)
{
    public ReturnDisposition Disposition { get; } = disposition;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string StockImpact { get; } = stockImpact;
}

public sealed class SalesReturnItemViewModel : ViewModelBase
{
    private decimal _returnQuantity;
    private readonly Action _onQuantityChanged;

    public SalesReturnItemViewModel(SaleLineItemViewModel originalItem, Action onQuantityChanged)
    {
        ArgumentNullException.ThrowIfNull(originalItem);
        ArgumentNullException.ThrowIfNull(onQuantityChanged);

        OriginalItem = originalItem;
        _onQuantityChanged = onQuantityChanged;

        IncrementCommand = new RelayCommand(Increment, () => CanIncrement);
        DecrementCommand = new RelayCommand(Decrement, () => CanDecrement);
    }

    public SaleLineItemViewModel OriginalItem { get; }
    public int LineNumber => OriginalItem.LineNumber;
    public string ProductId => OriginalItem.ProductId;
    public string ProductName => OriginalItem.ProductName;
    public string Sku => OriginalItem.Sku;
    public string Brand => OriginalItem.Brand;

    public decimal UnitPrice => OriginalItem.RefundUnitPrice;
    public string UnitPriceDisplay => $"Rs. {UnitPrice:N0}";
    public decimal SoldQuantity => OriginalItem.Quantity;
    public decimal AlreadyReturnedQuantity => OriginalItem.ReturnedQuantity;
    public decimal MaxEligibleQuantity => OriginalItem.EligibleReturnQuantity;
    public string MaxEligibleDisplay => $"Max: {MaxEligibleQuantity:0.##}";
    public bool IsFullyReturned => MaxEligibleQuantity <= 0m;

    public decimal ReturnQuantity
    {
        get => _returnQuantity;
        set
        {
            var clamped = Math.Clamp(value, 0m, MaxEligibleQuantity);
            if (SetProperty(ref _returnQuantity, clamped))
            {
                OnPropertyChanged(nameof(ReturnQuantityText));
                OnPropertyChanged(nameof(ReturnLineTotal));
                OnPropertyChanged(nameof(ReturnLineTotalDisplay));
                OnPropertyChanged(nameof(HasReturnQuantity));
                OnPropertyChanged(nameof(CanIncrement));
                OnPropertyChanged(nameof(CanDecrement));
                ((RelayCommand)IncrementCommand).NotifyCanExecuteChanged();
                ((RelayCommand)DecrementCommand).NotifyCanExecuteChanged();
                _onQuantityChanged();
            }
        }
    }

    public string ReturnQuantityText
    {
        get => _returnQuantity.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
                decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
            {
                ReturnQuantity = parsed;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                ReturnQuantity = 0m;
            }
        }
    }

    public bool HasReturnQuantity => ReturnQuantity > 0m;
    public bool CanIncrement => ReturnQuantity < MaxEligibleQuantity;
    public bool CanDecrement => ReturnQuantity > 0m;
    public decimal ReturnLineTotal => Math.Round(ReturnQuantity * UnitPrice, 2);
    public string ReturnLineTotalDisplay => $"Rs. {ReturnLineTotal:N0}";

    public ICommand IncrementCommand { get; }
    public ICommand DecrementCommand { get; }

    public void Increment()
    {
        if (CanIncrement)
        {
            ReturnQuantity = Math.Min(MaxEligibleQuantity, ReturnQuantity + 1m);
        }
    }

    public void Decrement()
    {
        if (CanDecrement)
        {
            ReturnQuantity = Math.Max(0m, ReturnQuantity - 1m);
        }
    }
}

public sealed class SalesReturnResult
{
    public int InvoiceNumber { get; init; }
    public decimal TotalRefundAmount { get; init; }
    public decimal CumulativeRefundAmount { get; init; }
    public decimal TotalReturnedItemsCount { get; init; }
    public ReturnDisposition Disposition { get; init; }
    public string DispositionText { get; init; } = string.Empty;
    public string RefundMethod { get; init; } = "Cash Refund";
    public string Notes { get; init; } = string.Empty;
    public SaleReturnState ReturnState { get; init; }
    public IReadOnlyList<(string ProductId, decimal ReturnedQty, decimal RefundAmount)> Items { get; init; } = [];
}

public sealed class SalesReturnViewModel : ViewModelBase
{
    private readonly Action<SalesReturnResult>? _onReturnProcessed;
    private readonly Action? _onClose;
    private readonly IToastService? _toastService;
    private readonly ITransactionService _transactionService;
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;

    private ReturnDisposition _selectedDisposition = ReturnDisposition.CustomerChangedMind;
    private string _refundMethod = "Cash Refund";
    private string _returnNotes = string.Empty;
    private bool _isProcessing;
    private string? _validationMessage;

    public SalesReturnViewModel(
        SaleTransactionItemViewModel sale,
        Action<SalesReturnResult>? onReturnProcessed = null,
        Action? onClose = null,
        IToastService? toastService = null,
        IDrawerService? drawerService = null,
        IDialogService? dialogService = null,
        ITransactionService? transactionService = null)
    {
        ArgumentNullException.ThrowIfNull(sale);

        Sale = sale;
        _onReturnProcessed = onReturnProcessed;
        _onClose = onClose;
        _toastService = toastService;
        _transactionService = transactionService ?? DemoTransactionService.Instance;

        ReasonOptions = new ReadOnlyCollection<ReturnReasonOptionViewModel>(
            [
                new(ReturnDisposition.CustomerChangedMind, "Customer Changed Mind", "Sellable / Restock", "Restock to inventory"),
                new(ReturnDisposition.Defective, "Defective", "Non-sellable / Defective stock", "Write-off / Vendor return"),
                new(ReturnDisposition.Damaged, "Damaged", "Non-sellable / Damaged stock", "Loss write-off"),
                new(ReturnDisposition.Other, "Other", "Requires return notes", "Manual disposition")
            ]);

        ReturnItems = new ObservableCollection<SalesReturnItemViewModel>(
            sale.LineItems.Select(line => new SalesReturnItemViewModel(line, RecalculateRefundTotals)));

        ProcessReturnCommand = new RelayCommand(
            async () => await ProcessReturnAsync(),
            () => CanProcessReturn);
        CancelCommand = new RelayCommand(Cancel, () => !IsProcessing);
        SetDispositionCommand = new RelayCommand<ReturnDisposition>(SetDisposition);
        SetRefundMethodCommand = new RelayCommand<string>(SetRefundMethod);

        RecalculateRefundTotals();
    }

    public SaleTransactionItemViewModel Sale { get; }
    public int InvoiceNumber => Sale.InvoiceNumber;
    public string InvoiceDisplay => Sale.InvoiceDisplay;
    public string CustomerName => Sale.CustomerName;
    public IReadOnlyList<ReturnReasonOptionViewModel> ReasonOptions { get; }
    public ObservableCollection<SalesReturnItemViewModel> ReturnItems { get; }

    public ReturnDisposition SelectedDisposition
    {
        get => _selectedDisposition;
        set
        {
            if (SetProperty(ref _selectedDisposition, value))
            {
                OnPropertyChanged(nameof(IsDispositionDefective));
                OnPropertyChanged(nameof(IsDispositionDamaged));
                OnPropertyChanged(nameof(IsDispositionRestock));
                OnPropertyChanged(nameof(IsDispositionOther));
                OnPropertyChanged(nameof(RequiresNotes));
                Validate();
            }
        }
    }

    public bool IsDispositionRestock => SelectedDisposition == ReturnDisposition.CustomerChangedMind;
    public bool IsDispositionDefective => SelectedDisposition == ReturnDisposition.Defective;
    public bool IsDispositionDamaged => SelectedDisposition == ReturnDisposition.Damaged;
    public bool IsDispositionOther => SelectedDisposition == ReturnDisposition.Other;
    public bool RequiresNotes => SelectedDisposition == ReturnDisposition.Other;

    public string RefundMethod
    {
        get => _refundMethod;
        set
        {
            if (SetProperty(ref _refundMethod, value))
            {
                OnPropertyChanged(nameof(IsCashRefund));
                OnPropertyChanged(nameof(IsStoreCreditRefund));
            }
        }
    }

    public bool IsCashRefund => string.Equals(RefundMethod, "Cash Refund", StringComparison.OrdinalIgnoreCase);
    public bool IsStoreCreditRefund => string.Equals(RefundMethod, "Store Credit", StringComparison.OrdinalIgnoreCase);

    public string ReturnNotes
    {
        get => _returnNotes;
        set
        {
            if (SetProperty(ref _returnNotes, value))
            {
                Validate();
            }
        }
    }

    public decimal TotalReturnAmount { get; private set; }
    public string TotalReturnAmountDisplay => $"Rs. {TotalReturnAmount:N0}";
    public decimal TotalReturnQuantity { get; private set; }
    public string TotalReturnQuantityDisplay =>
        $"{TotalReturnQuantity:0.##} {(TotalReturnQuantity == 1m ? "item" : "items")}";

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanProcessReturn));
                ((RelayCommand)ProcessReturnCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
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

    public bool CanProcessReturn =>
        !IsProcessing &&
        !Sale.HasInvoiceLevelDiscount &&
        TotalReturnQuantity > 0m &&
        (!RequiresNotes || !string.IsNullOrWhiteSpace(ReturnNotes));

    public ICommand ProcessReturnCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SetDispositionCommand { get; }
    public ICommand SetRefundMethodCommand { get; }

    public void SetDisposition(ReturnDisposition disposition)
    {
        SelectedDisposition = disposition;
    }

    public void SetRefundMethod(string? method)
    {
        if (!string.IsNullOrWhiteSpace(method))
        {
            RefundMethod = method;
        }
    }

    public void Cancel()
    {
        if (!IsProcessing)
        {
            _onClose?.Invoke();
        }
    }

    public async Task ProcessReturnAsync()
    {
        if (!CanProcessReturn || IsProcessing)
        {
            return;
        }

        IsProcessing = true;
        ValidationMessage = null;

        try
        {
            var selectedItems = ReturnItems
                .Where(item => item.ReturnQuantity > 0m)
                .ToArray();

            var requestItems = selectedItems
                .Select(item => new SaleReturnItemRecord
                {
                    ProductId = item.ProductId,
                    Sku = item.Sku,
                    Quantity = item.ReturnQuantity,
                    RefundAmount = item.ReturnLineTotal
                })
                .ToArray();

            var request = new RecordSaleReturnRequest
            {
                InvoiceNumber = Sale.InvoiceDisplay,
                Disposition = MapDisposition(SelectedDisposition),
                RefundMethod = RefundMethod,
                Notes = ReturnNotes.Trim(),
                TotalRefundAmount = TotalReturnAmount,
                Items = requestItems
            };

            var record = await _transactionService.RecordReturnAsync(request);
            _retailState.ApplySaleReturnStock(record);

            foreach (var selected in selectedItems)
            {
                selected.OriginalItem.ReturnedQuantity += selected.ReturnQuantity;
            }

            var allFullyReturned = Sale.LineItems.All(item => item.EligibleReturnQuantity <= 0m);
            var returnState = allFullyReturned ? SaleReturnState.Refunded : SaleReturnState.Partial;
            var cumulativeRefund = Sale.TotalReturnedAmount + record.TotalRefundAmount;

            var result = new SalesReturnResult
            {
                InvoiceNumber = InvoiceNumber,
                TotalRefundAmount = record.TotalRefundAmount,
                CumulativeRefundAmount = cumulativeRefund,
                TotalReturnedItemsCount = TotalReturnQuantity,
                Disposition = SelectedDisposition,
                DispositionText = SelectedDisposition switch
                {
                    ReturnDisposition.CustomerChangedMind => "Customer Changed Mind / Restock",
                    ReturnDisposition.Defective => "Defective Stock",
                    ReturnDisposition.Damaged => "Damaged Stock",
                    _ => "Other Reason"
                },
                RefundMethod = RefundMethod,
                Notes = ReturnNotes,
                ReturnState = returnState,
                Items = requestItems
                    .Select(item => (item.ProductId, item.Quantity, item.RefundAmount))
                    .ToArray()
            };

            _onReturnProcessed?.Invoke(result);
            _toastService?.Show(
                $"Return recorded. Refund Rs. {record.TotalRefundAmount:N0} via {RefundMethod}.",
                ToastTone.Success);
            _onClose?.Invoke();
        }
        catch (Exception ex)
        {
            ValidationMessage = ex.Message;
        }
        finally
        {
            IsProcessing = false;
        }
    }

    private void RecalculateRefundTotals()
    {
        TotalReturnAmount = Math.Round(ReturnItems.Sum(item => item.ReturnLineTotal), 2);
        TotalReturnQuantity = ReturnItems.Sum(item => item.ReturnQuantity);

        OnPropertyChanged(nameof(TotalReturnAmount));
        OnPropertyChanged(nameof(TotalReturnAmountDisplay));
        OnPropertyChanged(nameof(TotalReturnQuantity));
        OnPropertyChanged(nameof(TotalReturnQuantityDisplay));
        Validate();
    }

    private void Validate()
    {
        if (Sale.HasInvoiceLevelDiscount)
        {
            ValidationMessage =
                "Return processing is locked for invoices with an invoice-level discount until the refund allocation rule is approved.";
        }
        else if (TotalReturnQuantity <= 0m)
        {
            ValidationMessage = null;
        }
        else if (RequiresNotes && string.IsNullOrWhiteSpace(ReturnNotes))
        {
            ValidationMessage = "A note is required for the Other return reason.";
        }
        else
        {
            ValidationMessage = null;
        }

        OnPropertyChanged(nameof(CanProcessReturn));
        ((RelayCommand)ProcessReturnCommand).NotifyCanExecuteChanged();
    }

    private static SaleReturnDisposition MapDisposition(ReturnDisposition disposition)
    {
        return disposition switch
        {
            ReturnDisposition.CustomerChangedMind => SaleReturnDisposition.RestockSellable,
            ReturnDisposition.Defective => SaleReturnDisposition.Defective,
            ReturnDisposition.Damaged => SaleReturnDisposition.Damaged,
            _ => SaleReturnDisposition.Other
        };
    }
}
