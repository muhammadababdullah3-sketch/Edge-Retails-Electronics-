using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class MaterialLedgerEntry
{
    public Guid? BackendMaterialIssueId { get; init; }
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
    public Guid? BackendPaymentId { get; init; }
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
    private readonly DemoRetailState? _previewRetailState;
    private readonly IBackendThakaService? _backendService;
    private readonly IBackendWorkflowReadService? _workflowService;
    private readonly IDialogService? _dialogService;
    private readonly IToastService? _toastService;
    private string _selectedTab = "Materials";
    private MaterialLedgerEntry? _selectedMaterial;
    private Guid? _pendingMaterialReversalIssueId;
    private Guid? _pendingMaterialReversalOperationId;
    private bool _isLoading;
    private bool _hasSnapshot;
    private bool _changingSuspension;
    private string _loadError = string.Empty;

    public ThakaWorkspaceViewModel()
        : this(
            CreatePreviewProject(),
            null,
            null,
            null)
    {
    }

    public ThakaWorkspaceViewModel(
        ThakaProjectListItemViewModel project,
        IDialogService? dialogService = null,
        IToastService? toastService = null,
        IBackendThakaService? backendService = null,
        IBackendWorkflowReadService? workflowService = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _backendService = backendService;
        _workflowService = workflowService;
        _previewRetailState = ResolvePreviewRetailState(backendService);
        _dialogService = dialogService;
        _toastService = toastService;
        _hasSnapshot = _previewRetailState is not null;

        MaterialLedger = _previewRetailState is not null
            ? _previewRetailState.GetMaterialLedger(_project)
            : [];
        PaymentLedger = _previewRetailState is not null
            ? _previewRetailState.GetPaymentLedger(_project)
            : [];

        AddMaterialCommand = new RelayCommand(OpenAddMaterial, () => CanMutate && IsActive && _project.CustomerIsActive);
        RecordPaymentCommand = new RelayCommand(OpenRecordPayment, () => CanMutate && (IsActive || IsSuspended) && Balance > 0m);
        FinalSettlementCommand = new RelayCommand(OpenFinalSettlement, () => CanMutate && (IsActive || IsSuspended) && Balance > 0m);
        RefreshCommand = new RelayCommand(async () => await RefreshBackendAsync(), () => !_isLoading && !_changingSuspension);
        ToggleSuspensionCommand = new RelayCommand(async () => await ToggleSuspensionAsync(),
            () => CanMutate && _backendService is not null && (IsActive || IsSuspended));
        ReverseMaterialCommand = new RelayCommand(
            async () => await ReverseSelectedMaterialAsync(),
            () => CanReverseSelectedMaterial);
        GoBackCommand = new RelayCommand(() => BackRequested?.Invoke(this, EventArgs.Empty));
        SwitchTabCommand = new RelayCommand<string>(tab =>
        {
            if (!string.IsNullOrWhiteSpace(tab))
            {
                SelectedTab = tab;
            }
        });

        if (_backendService is not null)
        {
            _ = RefreshBackendAsync();
        }
    }

    public string ProjectName => _project.ProjectName;
    public string CustomerName => _project.CustomerName;
    public string Phone => _project.Phone;
    public string Location => _project.Location;
    public string StartDateFormatted => _project.StartDateFormatted;
    public string StatusDisplay => _project.Status;
    public bool IsActive => _project.IsActive;
    public bool IsSuspended => _project.IsSuspended;
    public bool IsCustomerSuspended => !_project.CustomerIsActive;
    public string SuspensionAction => IsSuspended ? "Resume Khata" : "Suspend Khata";
    public bool IsLoading => _isLoading;
    public string LoadError => _loadError;
    public bool HasLoadError => !string.IsNullOrWhiteSpace(_loadError);
    private bool CanMutate => _hasSnapshot && !_isLoading && !_changingSuspension && !HasLoadError;
    public bool IsSettled => _project.IsSettled;
    public bool IsSettledBannerVisible => IsSettled;
    public Controls.BadgeTone StatusTone => _project.StatusTone;
    public string ContactSubtitle =>
        $"{CustomerName} · {Phone}  |  {Location}  |  Started: {StartDateFormatted}";

    public decimal MaterialValue => _project.MaterialValue;
    public string MaterialValueFormatted => _hasSnapshot ? _project.MaterialValueFormatted : _isLoading ? "Loading…" : "Unavailable";
    public decimal TotalPaid => _project.Paid;
    public string TotalPaidFormatted => _hasSnapshot ? _project.PaidFormatted : _isLoading ? "Loading…" : "Unavailable";
    public decimal Balance => _project.Balance;
    public string BalanceFormatted => _hasSnapshot ? _project.BalanceFormatted : _isLoading ? "Loading…" : "Unavailable";

    public ObservableCollection<MaterialLedgerEntry> MaterialLedger { get; }
    public ObservableCollection<PaymentLedgerEntry> PaymentLedger { get; }

    public MaterialLedgerEntry? SelectedMaterial
    {
        get => _selectedMaterial;
        set
        {
            if (SetProperty(ref _selectedMaterial, value))
            {
                if (_pendingMaterialReversalIssueId != value?.BackendMaterialIssueId)
                {
                    _pendingMaterialReversalIssueId = value?.BackendMaterialIssueId;
                    _pendingMaterialReversalOperationId = null;
                }

                OnPropertyChanged(nameof(CanReverseSelectedMaterial));
                ((RelayCommand)ReverseMaterialCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanReverseSelectedMaterial =>
        CanMutate && (IsActive || IsSuspended) &&
        _backendService is not null &&
        SelectedMaterial?.BackendMaterialIssueId is Guid;

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
    public ICommand ReverseMaterialCommand { get; }
    public ICommand GoBackCommand { get; }
    public ICommand SwitchTabCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ToggleSuspensionCommand { get; }

    private async Task ToggleSuspensionAsync()
    {
        if (!CanMutate || _backendService is null)
        {
            return;
        }
        _changingSuspension = true;
        NotifyFinancialState();
        try
        {
            await _backendService.SetSuspensionAsync(_project, !IsSuspended,
                IsSuspended ? "Resumed from workspace" : "Suspended from workspace");
            await RefreshBackendAsync();
        }
        catch (Exception ex)
        {
            _toastService?.Show(DesktopErrorPresentation.ForException(ex, "Khata status could not be changed."), ToastTone.Danger);
        }
        finally
        {
            _changingSuspension = false;
            NotifyFinancialState();
        }
    }

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
            toastService: _toastService,
            backendService: _backendService,
            workflowService: _workflowService,
            dialogService: _dialogService);

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
            toastService: _toastService,
            backendService: _backendService);

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
            toastService: _toastService,
            backendService: _backendService);

        _dialogService.Show(vm);
    }

    private async Task ReverseSelectedMaterialAsync()
    {
        if (!CanReverseSelectedMaterial ||
            _backendService is null ||
            SelectedMaterial?.BackendMaterialIssueId is not Guid issueId)
        {
            return;
        }

        _pendingMaterialReversalIssueId = issueId;
        _pendingMaterialReversalOperationId ??= Guid.CreateVersion7();

        try
        {
            await _backendService.ReverseMaterialAsync(
                _project,
                issueId,
                $"Operator reversal of {SelectedMaterial.ChallanNumber}",
                _pendingMaterialReversalOperationId.Value);

            _toastService?.Show(
                $"{SelectedMaterial.ChallanNumber} reversed and exact stock provenance restored.",
                ToastTone.Success);
            _pendingMaterialReversalIssueId = null;
            _pendingMaterialReversalOperationId = null;
            SelectedMaterial = null;
            await RefreshBackendAsync();
        }
        catch (BackendOperationException ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Thaka operation was rejected."),
                ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Thaka operation failed."),
                ToastTone.Danger);
        }
    }

    private void RefreshFinancialState()
    {
        if (_backendService is not null)
        {
            _ = RefreshBackendAsync();
            return;
        }

        NotifyFinancialState();
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null ||
            _project.BackendProjectId is not Guid projectId)
        {
            return;
        }

        if (_isLoading)
        {
            return;
        }
        _isLoading = true;
        _loadError = string.Empty;
        NotifyFinancialState();
        try
        {
            var snapshot = await _backendService.GetWorkspaceAsync(projectId);
            _hasSnapshot = true;

            _project.MaterialValue = snapshot.Project.MaterialValue;
            _project.Paid = snapshot.Project.Paid;
            _project.SettlementDiscount = snapshot.Project.SettlementDiscount;
            _project.Status = snapshot.Project.Status;
            _project.CustomerIsActive = snapshot.Project.CustomerIsActive;
            _project.Phone = snapshot.Project.Phone;
            _project.Location = snapshot.Project.Location;
            _project.Notes = snapshot.Project.Notes;

            MaterialLedger.Clear();
            foreach (var entry in snapshot.Materials)
            {
                MaterialLedger.Add(entry);
            }

            PaymentLedger.Clear();
            foreach (var entry in snapshot.Payments)
            {
                PaymentLedger.Add(entry);
            }

            NotifyFinancialState();
        }
        catch (Exception ex)
        {
            _hasSnapshot = false;
            _loadError = DesktopErrorPresentation.ForException(ex, "Workspace could not be loaded. Retry or contact support if the problem persists.");
            _toastService?.Show(
                DesktopErrorPresentation.ForException(
                    ex,
                    "Workspace could not be loaded. Retry or contact support if the problem persists."),
                ToastTone.Danger);
        }
        finally
        {
            _isLoading = false;
            NotifyFinancialState();
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

    private static ThakaProjectListItemViewModel CreatePreviewProject()
    {
#if DEBUG
        return DemoRetailState.Instance.ThakaProjects
            .First(project => project.IsActive);
#else
        throw new InvalidOperationException(
            "Parameterless Thaka workspace construction is preview-only.");
#endif
    }

    private void NotifyFinancialState()
    {
        OnPropertyChanged(nameof(IsCustomerSuspended));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(LoadError));
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(IsSuspended));
        OnPropertyChanged(nameof(SuspensionAction));
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

        OnPropertyChanged(nameof(CanReverseSelectedMaterial));
        ((RelayCommand)AddMaterialCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RecordPaymentCommand).NotifyCanExecuteChanged();
        ((RelayCommand)FinalSettlementCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReverseMaterialCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ToggleSuspensionCommand).NotifyCanExecuteChanged();
    }
}
