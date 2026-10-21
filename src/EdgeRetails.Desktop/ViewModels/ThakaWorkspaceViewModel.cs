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
    private PaymentLedgerEntry? _selectedPayment;
    private Guid? _pendingMaterialReversalIssueId;
    private Guid? _pendingMaterialReversalOperationId;
    private readonly IClientOperationIntentStore _operationIntents;
    private bool _isLoading;
    private bool _hasSnapshot;
    private bool _changingSuspension;
    private string _loadError = string.Empty;
    private bool _isRefundPanelVisible;
    private decimal _refundAmountInput;
    private string _refundReasonInput = string.Empty;
    private bool _isRefundingCredit;
    private string? _refundErrorMessage;

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
        IBackendWorkflowReadService? workflowService = null,
        IClientOperationIntentStore? operationIntents = null)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        _backendService = backendService;
        _workflowService = workflowService;
        _operationIntents = operationIntents ?? new FileClientOperationIntentStore();
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
        FinalSettlementCommand = new RelayCommand(OpenFinalSettlement, () => CanMutate && (IsActive || IsSuspended) && Balance >= 0m);
        OpenRefundCreditCommand = new RelayCommand(OpenRefundPanel, () => CanMutate && (IsActive || IsSuspended) && HasCustomerCredit);
        CloseRefundCreditCommand = new RelayCommand(CloseRefundPanel);
        RetainCreditCommand = new RelayCommand(RetainCreditInProject, () => CanMutate && HasCustomerCredit);
        ConfirmRefundCreditCommand = new RelayCommand(async () => await ConfirmRefundCreditAsync(), () => CanConfirmRefund);
        RefundCreditCommand = new RelayCommand(OpenRefundPanel, () => CanMutate && (IsActive || IsSuspended) && HasCustomerCredit);
        RefreshCommand = new RelayCommand(async () => await RefreshBackendAsync(), () => !_isLoading && !_changingSuspension);
        ToggleSuspensionCommand = new RelayCommand(async () => await ToggleSuspensionAsync(),
            () => CanMutate && _backendService is not null && (IsActive || IsSuspended));
        ReopenProjectCommand = new RelayCommand(async () => await ReopenProjectAsync(),
            () => CanMutate && _backendService is not null && IsSettled);
        ReverseMaterialCommand = new RelayCommand(
            async () => await ReverseSelectedMaterialAsync(),
            () => CanReverseSelectedMaterial);
        ReversePaymentCommand = new RelayCommand(
            async () => await ReverseSelectedPaymentAsync(),
            () => CanReverseSelectedPayment);
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
    public bool HasCustomerCredit => Balance < 0m;
    public decimal CustomerCreditAmount => HasCustomerCredit ? -Balance : 0m;
    public string CustomerCreditFormatted => $"Rs. {CustomerCreditAmount:N0}";

    public ObservableCollection<MaterialLedgerEntry> MaterialLedger { get; }
    public ObservableCollection<PaymentLedgerEntry> PaymentLedger { get; }

    public MaterialLedgerEntry? SelectedMaterial
    {
        get => _selectedMaterial;
        set
        {
            if (SetProperty(ref _selectedMaterial, value))
            {
                OnPropertyChanged(nameof(CanReverseSelectedMaterial));
                ((RelayCommand)ReverseMaterialCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanReverseSelectedMaterial =>
        CanMutate && (IsActive || IsSuspended) &&
        _backendService is not null &&
        SelectedMaterial?.BackendMaterialIssueId is Guid;

    public PaymentLedgerEntry? SelectedPayment
    {
        get => _selectedPayment;
        set
        {
            if (SetProperty(ref _selectedPayment, value))
            {
                OnPropertyChanged(nameof(CanReverseSelectedPayment));
                ((RelayCommand)ReversePaymentCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool CanReverseSelectedPayment =>
        CanMutate && (IsActive || IsSuspended) &&
        _backendService is not null &&
        SelectedPayment?.BackendPaymentId is Guid;

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

    public bool IsRefundPanelVisible
    {
        get => _isRefundPanelVisible;
        set
        {
            if (SetProperty(ref _isRefundPanelVisible, value))
            {
                OnPropertyChanged(nameof(CanConfirmRefund));
            }
        }
    }

    public decimal RefundAmountInput
    {
        get => _refundAmountInput;
        set
        {
            if (SetProperty(ref _refundAmountInput, value))
            {
                OnPropertyChanged(nameof(CanConfirmRefund));
                ((RelayCommand)ConfirmRefundCreditCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string RefundReasonInput
    {
        get => _refundReasonInput;
        set
        {
            if (SetProperty(ref _refundReasonInput, value))
            {
                OnPropertyChanged(nameof(CanConfirmRefund));
                ((RelayCommand)ConfirmRefundCreditCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsRefundingCredit
    {
        get => _isRefundingCredit;
        set
        {
            if (SetProperty(ref _isRefundingCredit, value))
            {
                OnPropertyChanged(nameof(CanConfirmRefund));
                ((RelayCommand)ConfirmRefundCreditCommand).NotifyCanExecuteChanged();
            }
        }
    }

    public string? RefundErrorMessage
    {
        get => _refundErrorMessage;
        set
        {
            if (SetProperty(ref _refundErrorMessage, value))
            {
                OnPropertyChanged(nameof(HasRefundErrorMessage));
            }
        }
    }

    public bool HasRefundErrorMessage => !string.IsNullOrWhiteSpace(RefundErrorMessage);

    public bool CanConfirmRefund =>
        CanMutate && (IsActive || IsSuspended) && !_isRefundingCredit && HasCustomerCredit &&
        _refundAmountInput > 0m && _refundAmountInput <= CustomerCreditAmount &&
        !string.IsNullOrWhiteSpace(_refundReasonInput);

    public ICommand AddMaterialCommand { get; }
    public ICommand RecordPaymentCommand { get; }
    public ICommand FinalSettlementCommand { get; }
    public ICommand OpenRefundCreditCommand { get; }
    public ICommand CloseRefundCreditCommand { get; }
    public ICommand RetainCreditCommand { get; }
    public ICommand ConfirmRefundCreditCommand { get; }
    public ICommand RefundCreditCommand { get; }
    public ICommand ReverseMaterialCommand { get; }
    public ICommand ReversePaymentCommand { get; }
    public ICommand ReopenProjectCommand { get; }
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
            dialogService: _dialogService,
            operationIntents: _operationIntents);

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

        var reason = $"Operator reversal of {SelectedMaterial.ChallanNumber}";
        var operationKey = $"thaka:material-reversal:{issueId:D}";
        var canonicalPayload = $"{issueId:D}|{reason}";
        Guid clientOperationId;
        try
        {
            clientOperationId = _operationIntents.GetOrCreate(operationKey, canonicalPayload);
        }
        catch (InvalidOperationException)
        {
            _toastService?.Show(
                "A previous material reversal for this issue has an unresolved outcome and different details. Reconcile or retry with the original details before changing values.",
                ToastTone.Warning);
            return;
        }

        try
        {
            await _backendService.ReverseMaterialAsync(
                _project,
                issueId,
                reason,
                clientOperationId);

            _operationIntents.Complete(operationKey, clientOperationId);

            _toastService?.Show(
                $"{SelectedMaterial.ChallanNumber} reversed and exact stock provenance restored.",
                ToastTone.Success);
            SelectedMaterial = null;
            await RefreshBackendAsync();
        }
        catch (DesktopApiException ex) when (IsAuthoritativeDeterministicRejection(ex))
        {
            _operationIntents.Complete(operationKey, clientOperationId);
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Thaka material reversal was rejected."),
                ToastTone.Danger);
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

    private async Task ReopenProjectAsync()
    {
        if (!CanMutate || _backendService is null || !IsSettled)
        {
            return;
        }

        try
        {
            var reason = "Operator reopened project from workspace";
            await _backendService.ReopenProjectAsync(_project, reason);
            _toastService?.Show($"{ProjectName} reopened successfully.", ToastTone.Success);
            await RefreshBackendAsync();
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Project could not be reopened."),
                ToastTone.Danger);
        }
    }

    private async Task ReverseSelectedPaymentAsync()
    {
        if (!CanReverseSelectedPayment ||
            _backendService is null ||
            SelectedPayment?.BackendPaymentId is not Guid paymentId)
        {
            return;
        }

        var reason = $"Operator reversal of payment {SelectedPayment.ReceiptNumber}";
        var operationKey = $"thaka:payment-reversal:{paymentId:D}";
        var canonicalPayload = $"{paymentId:D}|{reason}";
        Guid clientOperationId;
        try
        {
            clientOperationId = _operationIntents.GetOrCreate(operationKey, canonicalPayload);
        }
        catch (InvalidOperationException)
        {
            _toastService?.Show(
                "A previous payment reversal for this receipt has an unresolved outcome and different details. Reconcile or retry with the original details before changing values.",
                ToastTone.Warning);
            return;
        }

        try
        {
            await _backendService.ReversePaymentAsync(
                _project,
                paymentId,
                reason,
                clientOperationId);

            _operationIntents.Complete(operationKey, clientOperationId);

            _toastService?.Show(
                $"{SelectedPayment.ReceiptNumber} reversed successfully.",
                ToastTone.Success);
            SelectedPayment = null;
            await RefreshBackendAsync();
        }
        catch (DesktopApiException ex) when (IsAuthoritativeDeterministicRejection(ex))
        {
            _operationIntents.Complete(operationKey, clientOperationId);
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Thaka payment reversal was rejected."),
                ToastTone.Danger);
        }
        catch (BackendOperationException ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Thaka payment reversal was rejected."),
                ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService?.Show(
                DesktopErrorPresentation.ForException(ex, "Thaka payment reversal failed."),
                ToastTone.Danger);
        }
    }

    public void OpenRefundPanel()
    {
        RefundAmountInput = CustomerCreditAmount;
        RefundReasonInput = $"Authorized customer credit refund for {ProjectName}";
        RefundErrorMessage = null;
        IsRefundPanelVisible = true;
    }

    public void CloseRefundPanel()
    {
        IsRefundPanelVisible = false;
        RefundErrorMessage = null;
    }

    public void RetainCreditInProject()
    {
        IsRefundPanelVisible = false;
        RefundErrorMessage = null;
        _toastService?.Show(
            $"Customer credit of Rs. {CustomerCreditAmount:N0} retained in project. Subsequent material charges will automatically consume this credit.",
            ToastTone.Info);
    }

    public async Task ConfirmRefundCreditAsync()
    {
        if (!CanConfirmRefund)
        {
            return;
        }

        await RefundCreditAsync(RefundAmountInput, RefundReasonInput);
    }

    public async Task RefundCreditAsync(decimal? refundAmount = null, string? refundReason = null)
    {
        if (!CanMutate || _backendService is null || !HasCustomerCredit)
        {
            return;
        }

        var amountToRefund = refundAmount ?? CustomerCreditAmount;
        if (amountToRefund <= 0m || amountToRefund > CustomerCreditAmount)
        {
            RefundErrorMessage = "Refund amount must be positive and cannot exceed available credit.";
            _toastService?.Show("Invalid refund amount.", ToastTone.Warning);
            return;
        }

        var reason = string.IsNullOrWhiteSpace(refundReason)
            ? $"Authorized customer credit refund for {ProjectName}"
            : refundReason.Trim();

        var operationKey = $"thaka:credit-refund:{_project.BackendProjectId:D}";
        var canonicalPayload = $"{_project.BackendProjectId:D}|{amountToRefund:G29}|{reason}";
        Guid clientOperationId;
        try
        {
            clientOperationId = _operationIntents.GetOrCreate(operationKey, canonicalPayload);
        }
        catch (InvalidOperationException)
        {
            var msg = "A previous credit refund for this project has an unresolved outcome and different details. Reconcile or retry with the original details before changing values.";
            RefundErrorMessage = msg;
            _toastService?.Show(msg, ToastTone.Warning);
            return;
        }

        IsRefundingCredit = true;
        RefundErrorMessage = null;
        try
        {
            await _backendService.RefundCreditAsync(
                _project,
                amountToRefund,
                reason,
                clientOperationId);

            _operationIntents.Complete(operationKey, clientOperationId);

            _toastService?.Show(
                $"Authorized refund of Rs. {amountToRefund:N0} processed from cash drawer.",
                ToastTone.Success);
            IsRefundPanelVisible = false;
            await RefreshBackendAsync();
        }
        catch (DesktopApiException ex) when (IsAuthoritativeDeterministicRejection(ex))
        {
            _operationIntents.Complete(operationKey, clientOperationId);
            var err = DesktopErrorPresentation.ForException(ex, "Credit refund was rejected.");
            RefundErrorMessage = err;
            _toastService?.Show(err, ToastTone.Danger);
        }
        catch (BackendOperationException ex)
        {
            var err = DesktopErrorPresentation.ForException(ex, "Credit refund was rejected.");
            RefundErrorMessage = err;
            _toastService?.Show(err, ToastTone.Danger);
        }
        catch (Exception ex)
        {
            var err = DesktopErrorPresentation.ForException(ex, "Credit refund failed.");
            RefundErrorMessage = err;
            _toastService?.Show(err, ToastTone.Danger);
        }
        finally
        {
            IsRefundingCredit = false;
        }
    }

    private static bool IsAuthoritativeDeterministicRejection(DesktopApiException ex) =>
        DesktopOperationRejectionClassifier.IsAuthoritativeDeterministicBusinessRejection(ex);

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
        OnPropertyChanged(nameof(HasCustomerCredit));
        OnPropertyChanged(nameof(CustomerCreditAmount));
        OnPropertyChanged(nameof(CustomerCreditFormatted));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsSettled));
        OnPropertyChanged(nameof(IsSettledBannerVisible));
        OnPropertyChanged(nameof(StatusDisplay));
        OnPropertyChanged(nameof(StatusTone));

        OnPropertyChanged(nameof(CanReverseSelectedMaterial));
        OnPropertyChanged(nameof(CanReverseSelectedPayment));
        OnPropertyChanged(nameof(IsRefundPanelVisible));
        OnPropertyChanged(nameof(RefundAmountInput));
        OnPropertyChanged(nameof(RefundReasonInput));
        OnPropertyChanged(nameof(IsRefundingCredit));
        OnPropertyChanged(nameof(RefundErrorMessage));
        OnPropertyChanged(nameof(HasRefundErrorMessage));
        OnPropertyChanged(nameof(CanConfirmRefund));
        ((RelayCommand)AddMaterialCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RecordPaymentCommand).NotifyCanExecuteChanged();
        ((RelayCommand)FinalSettlementCommand).NotifyCanExecuteChanged();
        ((RelayCommand)OpenRefundCreditCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RetainCreditCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ConfirmRefundCreditCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RefundCreditCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReverseMaterialCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReversePaymentCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ReopenProjectCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RefreshCommand).NotifyCanExecuteChanged();
        ((RelayCommand)ToggleSuspensionCommand).NotifyCanExecuteChanged();
    }
}
