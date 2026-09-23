using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ThakaFinalSettlementViewModel : ViewModelBase
{
    public sealed class SettlementResult
    {
        public decimal Amount { get; init; }
        public DateTime Timestamp { get; init; } = DateTime.Now;
    }

    private readonly ThakaProjectListItemViewModel _project;
    private readonly Action<SettlementResult>? _onSettled;
    private readonly Action? _onClose;
    private readonly IToastService? _toastService;
    private readonly IBackendThakaService? _backendService;
    private readonly DemoRetailState? _retailState;
    private decimal _settlementAmount;
    private string _settlementAmountText = string.Empty;
    private string _paymentMethod = "Cash";
    private bool _isConfirmed;
    private bool _isProcessing;
    private string? _validationMessage;

    public ThakaFinalSettlementViewModel(
        ThakaProjectListItemViewModel project,
        Action<SettlementResult>? onSettled = null,
        Action? onClose = null,
        IToastService? toastService = null,
        IBackendThakaService? backendService = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _onSettled = onSettled;
        _onClose = onClose;
        _toastService = toastService;
        _backendService = backendService;
        _retailState = ResolvePreviewRetailState(backendService);

        _settlementAmount = project.Balance;
        _settlementAmountText = _settlementAmount.ToString("0.##", CultureInfo.InvariantCulture);

        PaymentMethods = new[] { "Cash", "Bank", "Other" };
        ConfirmSettlementCommand = new RelayCommand(
            async () => await ConfirmSettlementAsync(),
            () => CanConfirmSettlement);
        CancelCommand = new RelayCommand(Cancel, () => !IsProcessing);
        Validate();
    }

    public string ProjectName => _project.ProjectName;
    public string MaterialValueDisplay => _project.MaterialValueFormatted;
    public string TotalPaidDisplay => _project.PaidFormatted;
    public decimal RemainingBalance => _project.Balance;
    public string RemainingBalanceDisplay => _project.BalanceFormatted;
    public IReadOnlyList<string> PaymentMethods { get; }

    public string PaymentMethod
    {
        get => _paymentMethod;
        set => SetProperty(
            ref _paymentMethod,
            string.IsNullOrWhiteSpace(value) ? "Cash" : value);
    }

    public decimal SettlementAmount
    {
        get => _settlementAmount;
        set
        {
            var rounded = Math.Max(0m, Math.Round(value, 2));
            if (SetProperty(ref _settlementAmount, rounded))
            {
                _settlementAmountText = rounded.ToString("0.##", CultureInfo.InvariantCulture);
                OnPropertyChanged(nameof(SettlementAmountText));
                Validate();
            }
        }
    }

    public string SettlementAmountText
    {
        get => _settlementAmountText;
        set
        {
            if (SetProperty(ref _settlementAmountText, value ?? string.Empty))
            {
                if (decimal.TryParse(_settlementAmountText, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
                    decimal.TryParse(_settlementAmountText, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed))
                {
                    _settlementAmount = Math.Max(0m, Math.Round(parsed, 2));
                    OnPropertyChanged(nameof(SettlementAmount));
                }

                Validate();
            }
        }
    }

    public bool IsConfirmed
    {
        get => _isConfirmed;
        set
        {
            if (SetProperty(ref _isConfirmed, value))
            {
                Validate();
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

    public bool IsProcessing
    {
        get => _isProcessing;
        private set
        {
            if (SetProperty(ref _isProcessing, value))
            {
                OnPropertyChanged(nameof(CanConfirmSettlement));
                ((RelayCommand)ConfirmSettlementCommand).NotifyCanExecuteChanged();
                ((RelayCommand)CancelCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanConfirmSettlement =>
        !IsProcessing &&
        RemainingBalance > 0m &&
        SettlementAmount == RemainingBalance &&
        IsConfirmed;

    public ICommand ConfirmSettlementCommand { get; }
    public ICommand CancelCommand { get; }

    private void Validate()
    {
        if (RemainingBalance <= 0m)
        {
            ValidationMessage = "There is no balance left to settle.";
        }
        else if (SettlementAmount != RemainingBalance)
        {
            ValidationMessage = $"Final settlement must clear the exact remaining balance of Rs. {RemainingBalance:N0}.";
        }
        else if (!IsConfirmed)
        {
            ValidationMessage = "Confirm that the project account is reconciled before settlement.";
        }
        else
        {
            ValidationMessage = null;
        }

        OnPropertyChanged(nameof(CanConfirmSettlement));
        ((RelayCommand)ConfirmSettlementCommand).NotifyCanExecuteChanged();
    }

    private async Task ConfirmSettlementAsync()
    {
        if (!CanConfirmSettlement)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            SettlementResult result;
            if (_backendService is null)
            {
                if (_retailState is null)
                {
                    throw new InvalidOperationException(
                        "Authoritative Thaka settlement service is unavailable.");
                }

                var entry = _retailState.SettleProject(
                    _project,
                    SettlementAmount,
                    "Abdullah");

                result = new SettlementResult
                {
                    Amount = entry.Amount,
                    Timestamp = entry.Date
                };
            }
            else
            {
                await _backendService.SettleAsync(
                    _project,
                    SettlementAmount,
                    PaymentMethod);

                result = new SettlementResult
                {
                    Amount = SettlementAmount,
                    Timestamp = DateTime.Now
                };
            }

            _onSettled?.Invoke(result);

            _toastService?.Show(
                $"{ProjectName} settled successfully. The workspace is now read-only.",
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
