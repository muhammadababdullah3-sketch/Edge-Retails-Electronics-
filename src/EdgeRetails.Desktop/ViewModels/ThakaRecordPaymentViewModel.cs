using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ThakaRecordPaymentViewModel : ViewModelBase
{
    public sealed class PaymentRecordResult
    {
        public decimal Amount { get; init; }
        public string PaymentMethod { get; init; } = "Cash";
        public string Reference { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; } = DateTime.Now;
    }

    private readonly ThakaProjectListItemViewModel _project;
    private readonly Action<PaymentRecordResult>? _onPaymentRecorded;
    private readonly Action? _onClose;
    private readonly IToastService? _toastService;
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;
    private decimal _amount;
    private string _amountText = string.Empty;
    private string _paymentMethod = "Cash";
    private string _reference = string.Empty;
    private bool _isProcessing;
    private string? _validationMessage;

    public ThakaRecordPaymentViewModel(
        ThakaProjectListItemViewModel project,
        Action<PaymentRecordResult>? onPaymentRecorded = null,
        Action? onClose = null,
        IToastService? toastService = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _onPaymentRecorded = onPaymentRecorded;
        _onClose = onClose;
        _toastService = toastService;

        PaymentMethods = new[] { "Cash", "Bank", "Other" };
        RecordPaymentCommand = new RelayCommand(RecordPayment, () => CanRecordPayment);
        CancelCommand = new RelayCommand(Cancel, () => !IsProcessing);
    }

    public string ProjectName => _project.ProjectName;
    public decimal OutstandingBalance => _project.Balance;
    public string OutstandingBalanceDisplay => $"Rs. {OutstandingBalance:N0}";
    public IReadOnlyList<string> PaymentMethods { get; }

    public decimal Amount
    {
        get => _amount;
        set
        {
            var rounded = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _amount, rounded))
            {
                _amountText = rounded == 0m ? string.Empty : rounded.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(AmountText));
                Validate();
            }
        }
    }

    public string AmountText
    {
        get => _amountText;
        set
        {
            if (SetProperty(ref _amountText, value ?? string.Empty))
            {
                if (decimal.TryParse(_amountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
                    decimal.TryParse(_amountText, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
                {
                    _amount = Math.Max(0m, Math.Round(parsed, 2));
                    OnPropertyChanged(nameof(Amount));
                }
                else if (string.IsNullOrWhiteSpace(_amountText))
                {
                    _amount = 0m;
                    OnPropertyChanged(nameof(Amount));
                }

                Validate();
            }
        }
    }

    public string PaymentMethod
    {
        get => _paymentMethod;
        set => SetProperty(ref _paymentMethod, string.IsNullOrWhiteSpace(value) ? "Cash" : value);
    }

    public string Reference
    {
        get => _reference;
        set => SetProperty(ref _reference, value ?? string.Empty);
    }

    public bool IsOverpayment => Amount > OutstandingBalance && OutstandingBalance > 0m;

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
                OnPropertyChanged(nameof(CanRecordPayment));
                ((RelayCommand)RecordPaymentCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanRecordPayment =>
        !IsProcessing &&
        OutstandingBalance > 0m &&
        Amount > 0m &&
        Amount <= OutstandingBalance;

    public ICommand RecordPaymentCommand { get; }
    public ICommand CancelCommand { get; }

    private void Validate()
    {
        if (OutstandingBalance <= 0m)
        {
            ValidationMessage = "This project has no outstanding balance.";
        }
        else if (Amount <= 0m)
        {
            ValidationMessage = "Payment amount must be greater than zero.";
        }
        else if (Amount > OutstandingBalance)
        {
            ValidationMessage = "Payment exceeds current outstanding balance. Overpayment is not enabled.";
        }
        else
        {
            ValidationMessage = null;
        }

        OnPropertyChanged(nameof(IsOverpayment));
        OnPropertyChanged(nameof(CanRecordPayment));
        ((RelayCommand)RecordPaymentCommand).NotifyCanExecuteChanged();
    }

    private void RecordPayment()
    {
        if (!CanRecordPayment)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            var entry = _retailState.RecordPayment(
                _project,
                Amount,
                PaymentMethod,
                Reference,
                "Abdullah");

            var result = new PaymentRecordResult
            {
                Amount = entry.Amount,
                PaymentMethod = entry.PaymentMethod,
                Reference = entry.Reference,
                Timestamp = entry.Date
            };

            _onPaymentRecorded?.Invoke(result);
            _toastService?.Show(
                $"Payment of Rs. {Amount:N0} recorded for {ProjectName}.",
                ToastTone.Success);
            _onClose?.Invoke();
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

    private void Cancel()
    {
        if (!IsProcessing)
        {
            _onClose?.Invoke();
        }
    }
}
