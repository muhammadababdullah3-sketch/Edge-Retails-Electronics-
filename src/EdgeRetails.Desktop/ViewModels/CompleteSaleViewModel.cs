using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// ViewModel for Complete Sale workflow and modal (Figma node 16:17947).
/// Manages payment methods, received amount validation, change calculation,
/// double-submit prevention, processing animation states, and transaction recording.
/// </summary>
public sealed class CompleteSaleViewModel : ViewModelBase
{
    private readonly ITransactionService _transactionService;
    private readonly IDialogService? _dialogService;
    private readonly IToastService? _toastService;
    private readonly IReadOnlyList<SaleTransactionItem>? _items;
    private readonly decimal _subtotal;
    private readonly decimal _discountAmount;
    private readonly Func<string?>? _preCommitValidation;
    private readonly Guid _clientOperationId;
    private readonly Guid? _draftId;
    private readonly long? _draftVersion;

    private decimal _totalToPay;
    private PaymentMethod _paymentMethod = PaymentMethod.Cash;
    private decimal _amountReceived;
    private string _amountReceivedText = string.Empty;
    private decimal _changeReturned;
    private bool _printReceipt = true;
    private bool _isProcessing;
    private string? _validationMessage;
    private readonly Guid? _customerId;
    private string _customerName = "Walk-in Customer";
    private string _customerPhone = string.Empty;
    private string _cashierName = "Abdullah, Owner";
    private string _paymentReference = string.Empty;
    private string? _notes;

    public event Action? RequestCloseRequested;
    public Action<SaleTransactionRecord>? OnSaleCompleted { get; set; }

    public CompleteSaleViewModel(
        decimal totalToPay = 0m,
        ITransactionService? transactionService = null,
        IDialogService? dialogService = null,
        IToastService? toastService = null,
        string customerName = "Walk-in Customer",
        string customerPhone = "",
        Guid? customerId = null,
        string cashierName = "Abdullah, Owner",
        IReadOnlyList<SaleTransactionItem>? items = null,
        decimal subtotal = 0m,
        decimal discountAmount = 0m,
        Func<string?>? preCommitValidation = null,
        Guid? clientOperationId = null,
        Guid? draftId = null,
        long? draftVersion = null)
    {
        _transactionService = transactionService ?? DemoTransactionService.Instance;
        _dialogService = dialogService;
        _toastService = toastService;
        _customerId = customerId;
        _customerName = string.IsNullOrWhiteSpace(customerName) ? "Walk-in Customer" : customerName;
        _customerPhone = customerPhone;
        _cashierName = string.IsNullOrWhiteSpace(cashierName) ? "Abdullah, Owner" : cashierName;
        _items = items;
        _subtotal = subtotal > 0m ? subtotal : totalToPay;
        _discountAmount = Math.Max(0m, discountAmount);
        _preCommitValidation = preCommitValidation;
        _clientOperationId = clientOperationId ?? Guid.CreateVersion7();
        _draftId = draftId;
        _draftVersion = draftVersion;

        // Initialize commands
        SelectCashCommand = new RelayCommand(() => PaymentMethod = PaymentMethod.Cash);
        SelectBankCommand = new RelayCommand(() => PaymentMethod = PaymentMethod.Bank);
        SelectOtherCommand = new RelayCommand(() => PaymentMethod = PaymentMethod.Other);

        SelectPaymentMethodCommand = new RelayCommand<string>(methodStr =>
        {
            if (Enum.TryParse<PaymentMethod>(methodStr, true, out var method))
            {
                PaymentMethod = method;
            }
        });

        SetExactAmountCommand = new RelayCommand(SetExactAmount);
        SetPresetCashCommand = new RelayCommand<object?>(SetPresetCash);
        AddCashCommand = new RelayCommand<object?>(AddCash);

        CompleteSaleCommand = new RelayCommand(async () => await CompleteSaleAsync(), () => CanComplete);
        CancelCommand = new RelayCommand(Cancel, () => !IsProcessing);

        // Set initial total
        TotalToPay = totalToPay;
        // Default received amount to exact total for smooth checkout
        SetExactAmount();
    }

    public decimal TotalToPay
    {
        get => _totalToPay;
        set
        {
            if (SetProperty(ref _totalToPay, Math.Max(0m, value)))
            {
                OnPropertyChanged(nameof(TotalToPayDisplay));
                if (PaymentMethod == PaymentMethod.Bank || PaymentMethod == PaymentMethod.Other)
                {
                    SetExactAmount();
                }
                else
                {
                    Recalculate();
                }
            }
        }
    }

    public string TotalToPayDisplay => $"Rs. {TotalToPay:N0}";

    public PaymentMethod PaymentMethod
    {
        get => _paymentMethod;
        set
        {
            if (SetProperty(ref _paymentMethod, value))
            {
                OnPropertyChanged(nameof(IsCash));
                OnPropertyChanged(nameof(IsBank));
                OnPropertyChanged(nameof(IsOther));
                OnPropertyChanged(nameof(PaymentMethodDisplay));
                OnPropertyChanged(nameof(PaymentReferenceLabel));
                OnPropertyChanged(nameof(PaymentGuidance));

                // When switching to Bank or Other, automatically set received to exact amount
                if (value == PaymentMethod.Bank || value == PaymentMethod.Other)
                {
                    SetExactAmount();
                }
                else
                {
                    Recalculate();
                }
            }
        }
    }

    public bool IsCash => PaymentMethod == PaymentMethod.Cash;
    public bool IsBank => PaymentMethod == PaymentMethod.Bank;
    public bool IsOther => PaymentMethod == PaymentMethod.Other;

    public string PaymentMethodDisplay => PaymentMethod switch
    {
        PaymentMethod.Cash => "Cash",
        PaymentMethod.Bank => "Bank Transfer",
        PaymentMethod.Other => "Other",
        _ => PaymentMethod.ToString()
    };

    public string PaymentReferenceLabel => PaymentMethod switch
    {
        PaymentMethod.Bank => "Bank Reference / Transaction ID (optional)",
        PaymentMethod.Other => "Payment Note / Reference (optional)",
        _ => string.Empty
    };

    public string PaymentGuidance => PaymentMethod switch
    {
        PaymentMethod.Bank => "Record the exact bank payment. No cash change is calculated.",
        PaymentMethod.Other => "Record the exact non-cash payment and an optional reference.",
        _ => "Enter cash received. Change is calculated automatically."
    };

    public decimal AmountReceived
    {
        get => _amountReceived;
        set
        {
            var rounded = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _amountReceived, rounded))
            {
                var formatted = rounded == 0m ? string.Empty : rounded.ToString("G29", CultureInfo.InvariantCulture);
                if (_amountReceivedText != formatted)
                {
                    _amountReceivedText = formatted;
                    OnPropertyChanged(nameof(AmountReceivedText));
                }
                Recalculate();
            }
        }
    }

    public string AmountReceivedText
    {
        get => _amountReceivedText;
        set
        {
            if (SetProperty(ref _amountReceivedText, value ?? string.Empty))
            {
                var clean = (_amountReceivedText ?? string.Empty).Trim();
                if (string.IsNullOrEmpty(clean))
                {
                    _amountReceived = 0m;
                    OnPropertyChanged(nameof(AmountReceived));
                    Recalculate();
                }
                else if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ||
                         decimal.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out parsed))
                {
                    _amountReceived = Math.Max(0m, Math.Round(parsed, 2));
                    OnPropertyChanged(nameof(AmountReceived));
                    Recalculate();
                }
                else
                {
                    ValidationMessage = "Please enter a valid numeric payment amount.";
                    ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
                }
            }
        }
    }

    public decimal ChangeReturned
    {
        get => _changeReturned;
        private set
        {
            if (SetProperty(ref _changeReturned, value))
            {
                OnPropertyChanged(nameof(ChangeReturnedDisplay));
                OnPropertyChanged(nameof(HasChangeReturned));
            }
        }
    }

    public string ChangeReturnedDisplay => $"Rs. {ChangeReturned:N0}";
    public bool HasChangeReturned => ChangeReturned > 0m;

    public decimal RemainingDue => Math.Max(0m, TotalToPay - AmountReceived);
    public string RemainingDueDisplay => $"Rs. {RemainingDue:N0}";
    public bool HasRemainingDue => RemainingDue > 0m;

    public bool PrintReceipt
    {
        get => _printReceipt;
        set => SetProperty(ref _printReceipt, value);
    }

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(IsNotProcessing));
                OnPropertyChanged(nameof(CanComplete));
                ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsNotProcessing => !IsProcessing;

    public string? ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (SetProperty(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationError));
            }
        }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationMessage);

    public string CustomerName
    {
        get => _customerName;
        set
        {
            if (SetProperty(ref _customerName, value))
            {
                OnPropertyChanged(nameof(RecipientName));
                OnPropertyChanged(nameof(RecipientSubtitle));
            }
        }
    }

    public string CustomerPhone
    {
        get => _customerPhone;
        set
        {
            if (SetProperty(ref _customerPhone, value))
            {
                OnPropertyChanged(nameof(RecipientSubtitle));
            }
        }
    }

    public string RecipientName =>
        string.IsNullOrWhiteSpace(CustomerName)
            ? "Walk-in Customer"
            : CustomerName.Trim();

    public string RecipientSubtitle =>
        string.IsNullOrWhiteSpace(CustomerPhone)
            ? "Walk-in sale"
            : CustomerPhone.Trim();

    public string PaymentReference
    {
        get => _paymentReference;
        set => SetProperty(ref _paymentReference, value ?? string.Empty);
    }

    public string CashierName
    {
        get => _cashierName;
        set => SetProperty(ref _cashierName, value);
    }

    public string? Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value);
    }

    public bool CanComplete
    {
        get
        {
            if (IsProcessing)
            {
                return false;
            }

            if (TotalToPay <= 0m)
            {
                return false;
            }

            if (PaymentMethod == PaymentMethod.Cash)
            {
                return AmountReceived >= TotalToPay;
            }

            // For Bank and Other, exact amount is strictly required
            return AmountReceived == TotalToPay;
        }
    }

    public bool? DialogResult { get; private set; }
    public SaleTransactionRecord? CompletedTransaction { get; private set; }

    // Commands
    public ICommand SelectCashCommand { get; }
    public ICommand SelectBankCommand { get; }
    public ICommand SelectOtherCommand { get; }
    public ICommand SelectPaymentMethodCommand { get; }
    public ICommand SetExactAmountCommand { get; }
    public ICommand SetPresetCashCommand { get; }
    public ICommand AddCashCommand { get; }
    public ICommand CompleteSaleCommand { get; }
    public ICommand CancelCommand { get; }

    public void SetExactAmount()
    {
        AmountReceived = TotalToPay;
    }

    public void SetPresetCash(object? parameter)
    {
        if (parameter is decimal d)
        {
            AmountReceived = d;
        }
        else if (parameter != null && decimal.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            AmountReceived = parsed;
        }
    }

    public void AddCash(object? parameter)
    {
        if (parameter is decimal d)
        {
            AmountReceived += d;
        }
        else if (parameter != null && decimal.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
        {
            AmountReceived += parsed;
        }
    }

    public async Task CompleteSaleAsync()
    {
        if (!CanComplete || IsProcessing)
        {
            return;
        }

        IsProcessing = true;
        ValidationMessage = null;

        try
        {
            var stockValidation = _preCommitValidation?.Invoke();
            if (!string.IsNullOrWhiteSpace(stockValidation))
            {
                ValidationMessage = stockValidation;
                return;
            }

            var request = new RecordSaleRequest
            {
                ClientOperationId = _clientOperationId,
                DraftId = _draftId,
                DraftVersion = _draftVersion,
                CustomerId = _customerId,
                TotalAmount = TotalToPay,
                PaymentMethod = PaymentMethod,
                AmountReceived = AmountReceived,
                ChangeReturned = ChangeReturned,
                PrintReceipt = PrintReceipt,
                CustomerName = CustomerName,
                CustomerPhone = CustomerPhone,
                CashierName = CashierName,
                PaymentReference = PaymentReference,
                Notes = Notes,
                Subtotal = _subtotal,
                DiscountAmount = _discountAmount,
                Items = _items
            };

            var record = await _transactionService.RecordTransactionAsync(request);

            CompletedTransaction = record;
            DialogResult = true;

            _toastService?.Show(
                $"Sale completed! Invoice {record.InvoiceNumber} recorded for {record.TotalDisplay}.",
                ToastTone.Success);

            OnSaleCompleted?.Invoke(record);

            _dialogService?.Close();
            RequestCloseRequested?.Invoke();
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

    public void Cancel()
    {
        if (IsProcessing)
        {
            return;
        }

        DialogResult = false;
        _dialogService?.Close();
        RequestCloseRequested?.Invoke();
    }

    private void Recalculate()
    {
        if (TotalToPay <= 0m)
        {
            ChangeReturned = 0m;
            ValidationMessage = "Total amount to pay must be greater than zero.";
        }
        else if (PaymentMethod == PaymentMethod.Cash)
        {
            if (AmountReceived >= TotalToPay)
            {
                ChangeReturned = AmountReceived - TotalToPay;
                ValidationMessage = null;
            }
            else
            {
                ChangeReturned = 0m;
                var deficit = TotalToPay - AmountReceived;
                ValidationMessage = $"Insufficient cash: Need Rs. {deficit:N0} more to complete sale.";
            }
        }
        else
        {
            // Bank or Other: exact amount required
            if (AmountReceived == TotalToPay)
            {
                ChangeReturned = 0m;
                ValidationMessage = null;
            }
            else
            {
                ChangeReturned = 0m;
                ValidationMessage = $"{PaymentMethodDisplay} payment requires exact amount (Rs. {TotalToPay:N0}).";
            }
        }

        OnPropertyChanged(nameof(RemainingDue));
        OnPropertyChanged(nameof(RemainingDueDisplay));
        OnPropertyChanged(nameof(HasRemainingDue));
        OnPropertyChanged(nameof(CanComplete));
        ((RelayCommand)CompleteSaleCommand).NotifyCanExecuteChanged();
    }
}
