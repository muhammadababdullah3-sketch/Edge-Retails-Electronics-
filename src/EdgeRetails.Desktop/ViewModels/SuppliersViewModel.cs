using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Application.Features.Finance;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Domain.Finance;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class SupplierEditViewModel : ViewModelBase
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly SupplierDirectoryRecord? _existing;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly Action? _saved;

    private string _name;
    private string _phone;
    private string _city;
    private string _address;
    private string _notes;

    public SupplierEditViewModel(
        IToastService toastService,
        Action close,
        SupplierDirectoryRecord? existing = null,
        Action? saved = null,
        IBackendBusinessOperationsService? backendService = null)
    {
        _toastService = toastService;
        _close = close;
        _existing = existing;
        _saved = saved;
        _backendService = backendService;

        _name = existing?.Name ?? string.Empty;
        _phone = existing?.Phone ?? string.Empty;
        _city = existing?.City ?? string.Empty;
        _address = existing?.Address ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;

        SaveCommand = new RelayCommand(async () => await SaveAsync());
        CancelCommand = new RelayCommand(_close);
    }

    public string Title => _existing is null ? "Add Supplier" : "Edit Supplier";
    public string SaveButtonText => _existing is null ? "Add Supplier" : "Save Changes";

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? string.Empty);
    }

    public string Phone
    {
        get => _phone;
        set => SetProperty(ref _phone, value ?? string.Empty);
    }

    public string City
    {
        get => _city;
        set => SetProperty(ref _city, value ?? string.Empty);
    }

    public string Address
    {
        get => _address;
        set => SetProperty(ref _address, value ?? string.Empty);
    }

    public string Notes
    {
        get => _notes;
        set => SetProperty(ref _notes, value ?? string.Empty);
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        try
        {
            if (_backendService is null)
            {
                _service.SaveSupplier(_existing, Name, Phone, City, Address, Notes);
            }
            else
            {
                await _backendService.SaveSupplierAsync(
                    _existing,
                    Name,
                    Phone,
                    City,
                    Address,
                    Notes);
            }

            _toastService.Show(
                _existing is null ? "Supplier added." : "Supplier updated.",
                ToastTone.Success);
            _close();
            _saved?.Invoke();
        }
        catch (Exception ex)
        {
            _toastService.Show(ex.Message, ToastTone.Danger);
        }
    }
}

public sealed class SupplierDetailViewModel : ViewModelBase
{
    private readonly IDrawerService _drawerService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly IBackendPhase5OperationsService? _phase5Service;
    private readonly Action? _updated;
    private readonly Dictionary<Guid, Guid> _pendingPaymentReversals = [];
    private readonly Dictionary<Guid, Guid> _pendingRefundReversals = [];

    private SupplierAccountWorkspaceDto? _workspace;
    private bool _isLoading;
    private string _statusMessage = "Loading authoritative Supplier account...";
    private decimal _transactionAmount;
    private SupplierSettlementMethod _selectedMethod = SupplierSettlementMethod.External;
    private string _externalReference = string.Empty;
    private string _transactionNote = string.Empty;
    private string _reversalReason = string.Empty;
    private Guid? _pendingSettlementOperationId;
    private Guid? _pendingAdvanceOperationId;
    private Guid? _pendingRefundOperationId;

    public SupplierDetailViewModel(
        SupplierDirectoryRecord supplier,
        IDrawerService drawerService,
        IDialogService dialogService,
        IToastService toastService,
        Action? updated = null,
        IBackendBusinessOperationsService? backendService = null,
        IBackendPhase5OperationsService? phase5Service = null)
    {
        Supplier = supplier;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _toastService = toastService;
        _updated = updated;
        _backendService = backendService;
        _phase5Service = phase5Service;

        CloseCommand = new RelayCommand(_drawerService.Close);
        EditCommand = new RelayCommand(Edit);
        RefreshAccountCommand = new RelayCommand(async () => await LoadAccountAsync());
        PostSettlementCommand = new RelayCommand(async () => await PostPaymentAsync(SupplierPaymentPurpose.Settlement));
        PostAdvanceCommand = new RelayCommand(async () => await PostPaymentAsync(SupplierPaymentPurpose.Advance));
        ReceiveRefundCommand = new RelayCommand(async () => await ReceiveRefundAsync());
        ReversePaymentCommand = new RelayCommand<SupplierPaymentReadDto>(row => _ = ReversePaymentAsync(row));
        ReverseRefundCommand = new RelayCommand<SupplierRefundReadDto>(row => _ = ReverseRefundAsync(row));

        _ = LoadAccountAsync();
    }

    public SupplierDirectoryRecord Supplier { get; }
    public string Title => Supplier.Name;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Supplier.Notes) ? "-" : Supplier.Notes;
    public IReadOnlyList<SupplierSettlementMethod> SettlementMethods { get; } =
        Enum.GetValues<SupplierSettlementMethod>();

    public SupplierAccountWorkspaceDto? Workspace
    {
        get => _workspace;
        private set
        {
            if (SetProperty(ref _workspace, value))
            {
                OnPropertyChanged(nameof(HasWorkspace));
            }
        }
    }

    public bool HasWorkspace => Workspace is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public decimal TransactionAmount
    {
        get => _transactionAmount;
        set
        {
            if (SetProperty(ref _transactionAmount, value))
            {
                ResetPendingEntryOperations();
            }
        }
    }

    public SupplierSettlementMethod SelectedMethod
    {
        get => _selectedMethod;
        set
        {
            if (SetProperty(ref _selectedMethod, value))
            {
                ResetPendingEntryOperations();
            }
        }
    }

    public string ExternalReference
    {
        get => _externalReference;
        set
        {
            if (SetProperty(ref _externalReference, value ?? string.Empty))
            {
                ResetPendingEntryOperations();
            }
        }
    }

    public string TransactionNote
    {
        get => _transactionNote;
        set
        {
            if (SetProperty(ref _transactionNote, value ?? string.Empty))
            {
                ResetPendingEntryOperations();
            }
        }
    }

    public string ReversalReason
    {
        get => _reversalReason;
        set => SetProperty(ref _reversalReason, value ?? string.Empty);
    }

    public ICommand CloseCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand RefreshAccountCommand { get; }
    public ICommand PostSettlementCommand { get; }
    public ICommand PostAdvanceCommand { get; }
    public ICommand ReceiveRefundCommand { get; }
    public ICommand ReversePaymentCommand { get; }
    public ICommand ReverseRefundCommand { get; }

    private async Task LoadAccountAsync()
    {
        if (_phase5Service is null || Supplier.BackendId is not Guid supplierId)
        {
            Workspace = null;
            StatusMessage = "Supplier account authority is unavailable in preview mode.";
            return;
        }

        IsLoading = true;
        StatusMessage = "Loading authoritative Supplier account...";
        try
        {
            Workspace = await _phase5Service.GetSupplierWorkspaceAsync(supplierId);
            StatusMessage = Workspace.Statement.Count == 0
                ? "No Supplier account events have been posted yet."
                : "Supplier account is current.";
        }
        catch (Phase5OperationException ex)
        {
            Workspace = null;
            StatusMessage = $"{ex.Code}: {ex.Message}";
        }
        catch (Exception ex)
        {
            Workspace = null;
            StatusMessage = $"Supplier account authority unavailable: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PostPaymentAsync(SupplierPaymentPurpose purpose)
    {
        if (_phase5Service is null || Supplier.BackendId is not Guid supplierId)
        {
            return;
        }

        if (TransactionAmount <= 0m)
        {
            _toastService.Show("Enter a payment amount greater than zero.", ToastTone.Warning);
            return;
        }

        var operationId = purpose == SupplierPaymentPurpose.Advance
            ? (_pendingAdvanceOperationId ??= Guid.CreateVersion7())
            : (_pendingSettlementOperationId ??= Guid.CreateVersion7());

        try
        {
            await _phase5Service.CreateSupplierPaymentAsync(
                supplierId,
                TransactionAmount,
                purpose,
                SelectedMethod,
                operationId,
                ExternalReference,
                TransactionNote);

            if (purpose == SupplierPaymentPurpose.Advance)
            {
                _pendingAdvanceOperationId = null;
            }
            else
            {
                _pendingSettlementOperationId = null;
            }

            _toastService.Show(
                purpose == SupplierPaymentPurpose.Advance
                    ? "Supplier advance posted."
                    : "Supplier payment posted.",
                ToastTone.Success);
            ClearTransactionEditor();
            await LoadAccountAsync();
        }
        catch (Phase5OperationException ex)
        {
            if (purpose == SupplierPaymentPurpose.Advance)
            {
                _pendingAdvanceOperationId = null;
            }
            else
            {
                _pendingSettlementOperationId = null;
            }

            _toastService.Show($"{ex.Code}: {ex.Message}", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Outcome is uncertain. Retry will reuse the same operation identity. {ex.Message}",
                ToastTone.Danger);
        }
    }

    private async Task ReceiveRefundAsync()
    {
        if (_phase5Service is null || Supplier.BackendId is not Guid supplierId)
        {
            return;
        }

        if (TransactionAmount <= 0m)
        {
            _toastService.Show("Enter a refund amount greater than zero.", ToastTone.Warning);
            return;
        }

        _pendingRefundOperationId ??= Guid.CreateVersion7();
        try
        {
            await _phase5Service.CreateSupplierRefundAsync(
                supplierId,
                TransactionAmount,
                SelectedMethod,
                _pendingRefundOperationId.Value,
                ExternalReference,
                TransactionNote);
            _pendingRefundOperationId = null;
            _toastService.Show("Supplier refund received.", ToastTone.Success);
            ClearTransactionEditor();
            await LoadAccountAsync();
        }
        catch (Phase5OperationException ex)
        {
            _pendingRefundOperationId = null;
            _toastService.Show($"{ex.Code}: {ex.Message}", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Outcome is uncertain. Retry will reuse the same operation identity. {ex.Message}",
                ToastTone.Danger);
        }
    }

    private async Task ReversePaymentAsync(SupplierPaymentReadDto payment)
    {
        if (_phase5Service is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ReversalReason))
        {
            _toastService.Show("Enter a reversal reason first.", ToastTone.Warning);
            return;
        }

        if (!_pendingPaymentReversals.TryGetValue(payment.PaymentId, out var operationId))
        {
            operationId = Guid.CreateVersion7();
            _pendingPaymentReversals[payment.PaymentId] = operationId;
        }

        try
        {
            await _phase5Service.ReverseSupplierPaymentAsync(
                payment.PaymentId,
                ReversalReason,
                operationId);
            _pendingPaymentReversals.Remove(payment.PaymentId);
            ReversalReason = string.Empty;
            _toastService.Show("Supplier payment reversed through append-only correction.", ToastTone.Success);
            await LoadAccountAsync();
        }
        catch (Phase5OperationException ex)
        {
            _pendingPaymentReversals.Remove(payment.PaymentId);
            _toastService.Show($"{ex.Code}: {ex.Message}", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Outcome is uncertain. Retry this payment reversal to reuse its operation identity. {ex.Message}",
                ToastTone.Danger);
        }
    }

    private async Task ReverseRefundAsync(SupplierRefundReadDto refund)
    {
        if (_phase5Service is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(ReversalReason))
        {
            _toastService.Show("Enter a reversal reason first.", ToastTone.Warning);
            return;
        }

        if (!_pendingRefundReversals.TryGetValue(refund.RefundId, out var operationId))
        {
            operationId = Guid.CreateVersion7();
            _pendingRefundReversals[refund.RefundId] = operationId;
        }

        try
        {
            await _phase5Service.ReverseSupplierRefundAsync(
                refund.RefundId,
                ReversalReason,
                operationId);
            _pendingRefundReversals.Remove(refund.RefundId);
            ReversalReason = string.Empty;
            _toastService.Show("Supplier refund reversed through append-only correction.", ToastTone.Success);
            await LoadAccountAsync();
        }
        catch (Phase5OperationException ex)
        {
            _pendingRefundReversals.Remove(refund.RefundId);
            _toastService.Show($"{ex.Code}: {ex.Message}", ToastTone.Danger);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Outcome is uncertain. Retry this refund reversal to reuse its operation identity. {ex.Message}",
                ToastTone.Danger);
        }
    }

    private void Edit()
    {
        _drawerService.Close();
        _dialogService.Show(new SupplierEditViewModel(
            _toastService,
            _dialogService.Close,
            Supplier,
            _updated,
            _backendService));
    }

    private void ClearTransactionEditor()
    {
        _transactionAmount = 0m;
        _externalReference = string.Empty;
        _transactionNote = string.Empty;
        OnPropertyChanged(nameof(TransactionAmount));
        OnPropertyChanged(nameof(ExternalReference));
        OnPropertyChanged(nameof(TransactionNote));
        ResetPendingEntryOperations();
    }

    private void ResetPendingEntryOperations()
    {
        _pendingSettlementOperationId = null;
        _pendingAdvanceOperationId = null;
        _pendingRefundOperationId = null;
    }
}

public sealed class SuppliersViewModel : ViewModelBase, IDisposable
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly IBackendPhase5OperationsService? _phase5Service;
    private readonly List<SupplierDirectoryRecord> _backendSuppliers = [];
    private bool _backendLoaded;
    private bool _backendLoading;
    private CancellationTokenSource? _searchCts;
    private long _searchVersion;
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private string _searchText = string.Empty;

    public SuppliersViewModel(
        IToastService toastService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IBackendBusinessOperationsService? backendService = null,
        IBackendPhase5OperationsService? phase5Service = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _backendService = backendService;
        _phase5Service = phase5Service;

        FilteredSuppliers = [];
        AddSupplierCommand = new RelayCommand(OpenAddSupplier);
        ViewSupplierCommand = new RelayCommand<SupplierDirectoryRecord>(OpenSupplier);

        if (_backendService is null)
        {
            _service.StateChanged += OnStateChanged;
            Refresh();
        }
        else
        {
            _ = RefreshBackendAsync();
        }
    }

    public void Dispose()
    {
        if (_backendService is null)
        {
            _service.StateChanged -= OnStateChanged;
        }
    }

    public ObservableCollection<SupplierDirectoryRecord> FilteredSuppliers { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                _ = ScheduleSearchAsync(_searchText);
            }
        }
    }

    public ICommand AddSupplierCommand { get; }
    public ICommand ViewSupplierCommand { get; }

    private void OpenAddSupplier()
    {
        _dialogService.Show(new SupplierEditViewModel(
            _toastService,
            _dialogService.Close,
            saved: RefreshAfterMutation,
            backendService: _backendService));
    }

    private void OpenSupplier(SupplierDirectoryRecord supplier)
    {
        _drawerService.Show(new SupplierDetailViewModel(
            supplier,
            _drawerService,
            _dialogService,
            _toastService,
            RefreshAfterMutation,
            _backendService,
            _phase5Service));
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_backendService is not null)
        {
            if (!_backendLoaded && !_backendLoading)
            {
                _ = RefreshBackendAsync();
                return;
            }

            ApplyFilter(_backendSuppliers);
            return;
        }

        ApplyFilter(_service.Suppliers);
    }

    private void RefreshAfterMutation()
    {
        if (_backendService is null)
        {
            Refresh();
        }
        else
        {
            _ = RefreshBackendAsync();
        }
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null || _backendLoading)
        {
            return;
        }

        _backendLoading = true;
        try
        {
            var suppliers = await _backendService.GetSuppliersAsync(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                200);
            _backendSuppliers.Clear();
            _backendSuppliers.AddRange(suppliers);
            _backendLoaded = true;
            ApplyFilter(_backendSuppliers);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Suppliers could not be refreshed: {ex.Message}",
                ToastTone.Danger);
        }
        finally
        {
            _backendLoading = false;
        }
    }

    private async Task ScheduleSearchAsync(string search)
    {
        var version = Interlocked.Increment(ref _searchVersion);
        var previous = Interlocked.Exchange(ref _searchCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cts = _searchCts!;

        try
        {
            await Task.Delay(250, cts.Token);
            if (_backendService is null || version != Volatile.Read(ref _searchVersion))
            {
                Refresh();
                return;
            }

            var suppliers = await _backendService.GetSuppliersAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                200,
                cts.Token);
            if (version != Volatile.Read(ref _searchVersion) || cts.IsCancellationRequested)
            {
                return;
            }

            _backendSuppliers.Clear();
            _backendSuppliers.AddRange(suppliers);
            _backendLoaded = true;
            ApplyFilter(_backendSuppliers);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _searchVersion))
        {
            _toastService.Show($"Suppliers search failed: {ex.Message}", ToastTone.Danger);
        }
    }

    private void ApplyFilter(IEnumerable<SupplierDirectoryRecord> source)
    {
        var query = source;

        FilteredSuppliers.Clear();
        foreach (var supplier in query.OrderBy(supplier => supplier.Name))
        {
            FilteredSuppliers.Add(supplier);
        }
    }
}
