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
    private readonly DemoRetailState _retailState = DemoRetailState.Instance;
    private decimal _settlementAmount;
    private string _settlementAmountText = string.Empty;
    private bool _isConfirmed;
    private bool _isProcessing;
    private string? _validationMessage;

    public ThakaFinalSettlementViewModel(
        ThakaProjectListItemViewModel project,
        Action<SettlementResult>? onSettled = null,
        Action? onClose = null,
        IToastService? toastService = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _onSettled = onSettled;
        _onClose = onClose;
        _toastService = toastService;

        _settlementAmount = project.Balance;
        _settlementAmountText = _settlementAmount.ToString("0.##", CultureInfo.InvariantCulture);

        ConfirmSettlementCommand = new RelayCommand(ConfirmSettlement, () => CanConfirmSettlement);
        CancelCommand = new RelayCommand(Cancel, () => !IsProcessing);
        Validate();
    }

    public string ProjectName => _project.ProjectName;
    public string MaterialValueDisplay => _project.MaterialValueFormatted;
    public string TotalPaidDisplay => _project.PaidFormatted;
    public decimal RemainingBalance => _project.Balance;
    public string RemainingBalanceDisplay => _project.BalanceFormatted;

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

    private void ConfirmSettlement()
    {
        if (!CanConfirmSettlement)
        {
            return;
        }

        IsProcessing = true;
        try
        {
            var entry = _retailState.SettleProject(
                _project,
                SettlementAmount,
                "Abdullah");

            _onSettled?.Invoke(new SettlementResult
            {
                Amount = entry.Amount,
                Timestamp = entry.Date
            });

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

    private void Cancel()
    {
        if (!IsProcessing)
        {
            _onClose?.Invoke();
        }
    }
}
