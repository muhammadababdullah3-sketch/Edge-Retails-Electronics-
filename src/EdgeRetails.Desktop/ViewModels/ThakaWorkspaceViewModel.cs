using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class MaterialLedgerEntry
{
    public DateTime Date { get; init; }
    public string ChallanNumber { get; init; } = string.Empty;
    public string ProductName { get; init; } = string.Empty;
    public decimal Quantity { get; init; }
    public string Unit { get; init; } = "Pcs";
    public decimal Rate { get; init; }
    public decimal TotalValue { get; init; }

    public string DateFormatted => Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    public string RateFormatted => $"Rs. {Rate:N0}";
    public string TotalValueFormatted => $"Rs. {TotalValue:N0}";
    public string QuantityDisplay => Quantity % 1m == 0m ? $"{Quantity:0}" : $"{Quantity:0.##}";
    public string QuantityWithUnit => $"{QuantityDisplay} {Unit}";
}

public sealed class PaymentLedgerEntry
{
    public string ReceiptNumber { get; init; } = string.Empty;
    public DateTime Date { get; init; }
    public string PaymentMethod { get; init; } = "Cash";
    public decimal Amount { get; init; }
    public string RecordedBy { get; init; } = "Abdullah";
    public string Reference { get; init; } = string.Empty;

    public string DateFormatted => Date.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
    public string AmountFormatted => $"Rs. {Amount:N0}";
}

public sealed class ThakaWorkspaceViewModel : ViewModelBase
{
    private readonly ThakaProjectListItemViewModel _project;
    private readonly DemoRetailState _retailState;
    private readonly IDialogService? _dialogService;
    private readonly IToastService? _toastService;
    private string _selectedTab = "Materials";

    public ThakaWorkspaceViewModel()
        : this(
            DemoRetailState.Instance.ThakaProjects.First(project => project.IsActive),
            null,
            null)
    {
    }

    public ThakaWorkspaceViewModel(
        ThakaProjectListItemViewModel project,
        IDialogService? dialogService = null,
        IToastService? toastService = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _retailState = DemoRetailState.Instance;
        _dialogService = dialogService;
        _toastService = toastService;

        MaterialLedger = _retailState.GetMaterialLedger(_project);
        PaymentLedger = _retailState.GetPaymentLedger(_project);

        AddMaterialCommand = new RelayCommand(OpenAddMaterial, () => !IsSettled);
        RecordPaymentCommand = new RelayCommand(OpenRecordPayment, () => !IsSettled && Balance > 0m);
        FinalSettlementCommand = new RelayCommand(OpenFinalSettlement, () => !IsSettled && Balance > 0m);
        GoBackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        SwitchTabCommand = new RelayCommand<string>(tab =>
        {
            if (!string.IsNullOrWhiteSpace(tab))
            {
                SelectedTab = tab;
            }
        });
    }

    public string ProjectName => _project.ProjectName;
    public string CustomerName => _project.CustomerName;
    public string Phone => _project.Phone;
    public string Location => _project.Location;
    public string StartDateFormatted => _project.StartDateFormatted;
    public string StatusDisplay => _project.Status;
    public bool IsActive => _project.IsActive;
    public bool IsSettled => _project.IsSettled;
    public bool IsSettledBannerVisible => IsSettled;
    public Controls.BadgeTone StatusTone => _project.StatusTone;
    public string ContactSubtitle =>
        $"{CustomerName} · {Phone}  |  {Location}  |  Started: {StartDateFormatted}";

    public decimal MaterialValue => _project.MaterialValue;
    public string MaterialValueFormatted => _project.MaterialValueFormatted;
    public decimal TotalPaid => _project.Paid;
    public string TotalPaidFormatted => _project.PaidFormatted;
    public decimal Balance => _project.Balance;
    public string BalanceFormatted => _project.BalanceFormatted;

    public ObservableCollection<MaterialLedgerEntry> MaterialLedger { get; }
    public ObservableCollection<PaymentLedgerEntry> PaymentLedger { get; }

    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                OnPropertyChanged(nameof(IsMaterialsTabSelected));
                OnPropertyChanged(nameof(IsPaymentsTabSelected));
            }
        }
    }

    public bool IsMaterialsTabSelected =>
        string.Equals(SelectedTab, "Materials", StringComparison.OrdinalIgnoreCase);

    public bool IsPaymentsTabSelected =>
        string.Equals(SelectedTab, "Payments", StringComparison.OrdinalIgnoreCase);
    public ICommand AddMaterialCommand { get; }
    public ICommand RecordPaymentCommand { get; }
    public ICommand FinalSettlementCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand SwitchTabCommand { get; }

    public event EventHandler? BackRequested;
    public event EventHandler? ProjectSettled;

    private void OpenAddMaterial()
    {
        if (_dialogService == null)
        {
            _toastService?.Show("Add Material dialog requires DialogService.", ToastTone.Warning);
            return;
        }

        var vm = new ThakaAddMaterialViewModel(
            _project,
            onMaterialAdded: _ => RefreshFinancialState(),
            onClose: () => _dialogService.Close(),
            toastService: _toastService);

        _dialogService.Show(vm);
    }

    private void OpenRecordPayment()
    {
        if (_dialogService == null)
        {
            _toastService?.Show("Record Payment dialog requires DialogService.", ToastTone.Warning);
            return;
        }
        var vm = new ThakaRecordPaymentViewModel(
            _project,
            onPaymentRecorded: _ => RefreshFinancialState(),
            onClose: () => _dialogService.Close(),
            toastService: _toastService);

        _dialogService.Show(vm);
    }

    private void OpenFinalSettlement()
    {
        if (_dialogService == null)
        {
            _toastService?.Show("Final Settlement dialog requires DialogService.", ToastTone.Warning);
            return;
        }

        var vm = new ThakaFinalSettlementViewModel(
            _project,
            onSettled: _ =>
            {
                RefreshFinancialState();
                ProjectSettled?.Invoke(this, EventArgs.Empty);
            },
            onClose: () => _dialogService.Close(),
            toastService: _toastService);

        _dialogService.Show(vm);
    }

    private void RefreshFinancialState()
    {
        OnPropertyChanged(nameof(MaterialValue));
        OnPropertyChanged(nameof(MaterialValueFormatted));
        OnPropertyChanged(nameof(TotalPaid));
        OnPropertyChanged(nameof(TotalPaidFormatted));
        OnPropertyChanged(nameof(Balance));
        OnPropertyChanged(nameof(BalanceFormatted));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsSettled));
        OnPropertyChanged(nameof(IsSettledBannerVisible));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusTone));

        ((RelayCommand)AddMaterialCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RecordPaymentCommand).NotifyCanExecuteChanged();
        ((RelayCommand)FinalSettlementCommand).NotifyCanExecuteChanged();
    }
}
