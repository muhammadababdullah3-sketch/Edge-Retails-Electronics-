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

    private int _saveGate;
    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }
    public bool CanEdit => !IsBusy;

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
        set
        {
            if (!IsBusy)
            {
                SetProperty(ref _name, value ?? string.Empty);
            }
        }
    }

    public string Phone
    {
        get => _phone;
        set
        {
            if (!IsBusy)
            {
                SetProperty(ref _phone, value ?? string.Empty);
            }
        }
    }

    public string City
    {
        get => _city;
        set
        {
            if (!IsBusy)
            {
                SetProperty(ref _city, value ?? string.Empty);
            }
        }
    }

    public string Address
    {
        get => _address;
        set
        {
            if (!IsBusy)
            {
                SetProperty(ref _address, value ?? string.Empty);
            }
        }
    }

    public string Notes
    {
        get => _notes;
        set
        {
            if (!IsBusy)
            {
                SetProperty(ref _notes, value ?? string.Empty);
            }
        }
    }

    private string _explicitDealerPrefix = string.Empty;
    public string ExplicitDealerPrefix
    {
        get => _explicitDealerPrefix;
        set
        {
            if (!IsBusy)
            {
                SetProperty(ref _explicitDealerPrefix, value ?? string.Empty);
            }
        }
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (Interlocked.CompareExchange(ref _saveGate, 1, 0) != 0)
        {
            return;
        }
        IsBusy = true;
        var name = Name;
        var phone = Phone;
        var city = City;
        var address = Address;
        var notes = Notes;
        var dealerPrefix = string.IsNullOrWhiteSpace(ExplicitDealerPrefix) ? null : ExplicitDealerPrefix.Trim();
        try
        {
            if (_backendService is null)
            {
                _service.SaveSupplier(_existing, name, phone, city, address, notes);
            }
            else
            {
                await _backendService.SaveSupplierAsync(
                    _existing,
                    name,
                    phone,
                    city,
                    address,
                    notes,
                    dealerPrefix);
            }

            _toastService.Show(
                _existing is null ? "Supplier added." : "Supplier updated.",
                ToastTone.Success);
            _close();
            _saved?.Invoke();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Supplier details could not be loaded."),
                ToastTone.Danger);
        }
        finally
        {
            IsBusy = false;
            Interlocked.Exchange(ref _saveGate, 0);
        }
    }
}

public sealed class SupplierDetailViewModel : ViewModelBase
{
    private readonly IDrawerService _drawerService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly IBackendOperationsService? _operationsService;
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
    private int _financialGate;
    private bool _isSubmitting;
    private string? _unresolvedFinancialAction;
    private FinancialSubmission? _pendingSubmission;
    private readonly Dictionary<Guid, string> _paymentReversalReasons = [];
    private readonly Dictionary<Guid, string> _refundReversalReasons = [];
    private sealed record FinancialSubmission(Guid OperationId, decimal Amount, SupplierSettlementMethod Method,
        string ExternalReference, string Note);
    public bool IsSubmitting
    {
        get => _isSubmitting;
        private set
        {
            if (SetProperty(ref _isSubmitting, value))
            {
                OnPropertyChanged(nameof(CanEditFinancialFields));
            }
        }
    }
    public bool CanEditFinancialFields => !IsSubmitting;
    public bool HasUnresolvedFinancialOperation => _unresolvedFinancialAction is not null;
    public string FinancialOperationStatus => IsSubmitting ? "Submitting original supplier operation..."
        : HasUnresolvedFinancialOperation ? "Unresolved supplier operation retained. Retry the same action to submit its original values. Do not close this workspace; restart recovery is unavailable." : string.Empty;

    private bool BeginFinancialSubmission(string action)
    {
        if (Interlocked.CompareExchange(ref _financialGate, 1, 0) != 0)
        {
            return false;
        }
        if (_unresolvedFinancialAction is not null && _unresolvedFinancialAction != action)
        {
            Interlocked.Exchange(ref _financialGate, 0);
            _toastService.Show("Resolve the earlier supplier operation before starting a different financial action.", ToastTone.Warning);
            return false;
        }
        _unresolvedFinancialAction = action;
        IsSubmitting = true;
        OnPropertyChanged(nameof(HasUnresolvedFinancialOperation));
        OnPropertyChanged(nameof(FinancialOperationStatus));
        return true;
    }

    private void EndFinancialSubmission()
    {
        IsSubmitting = false;
        Interlocked.Exchange(ref _financialGate, 0);
        OnPropertyChanged(nameof(FinancialOperationStatus));
    }

    private void ConfirmFinancialSubmission()
    {
        _unresolvedFinancialAction = null;
        _pendingSubmission = null;
        OnPropertyChanged(nameof(HasUnresolvedFinancialOperation));
        OnPropertyChanged(nameof(FinancialOperationStatus));
    }

    private Guid? _pendingSettlementOperationId;
    private Guid? _pendingAdvanceOperationId;
    private Guid? _pendingRefundOperationId;
    private const int MaxRetainedStatementEntries = 300;
    private int _isLoadingStatement;
    private long _loadVersion;
    private CancellationTokenSource? _loadCts;

    public SupplierDetailViewModel(
        SupplierDirectoryRecord supplier,
        IDrawerService drawerService,
        IDialogService dialogService,
        IToastService toastService,
        Action? updated = null,
        IBackendBusinessOperationsService? backendService = null,
        IBackendOperationsService? operationsService = null)
    {
        Supplier = supplier;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _toastService = toastService;
        _updated = updated;
        _backendService = backendService;
        _operationsService = operationsService;

        CloseCommand = new RelayCommand(() =>
        {
            if (CanCloseFinancialWorkspace())
            {
                _drawerService.Close();
            }
        });
        EditCommand = new RelayCommand(Edit);
        RefreshAccountCommand = new RelayCommand(async () => await LoadAccountAsync());
        PostSettlementCommand = new RelayCommand(async () => await PostPaymentAsync(SupplierPaymentPurpose.Settlement));
        PostAdvanceCommand = new RelayCommand(async () => await PostPaymentAsync(SupplierPaymentPurpose.Advance));
        ReceiveRefundCommand = new RelayCommand(async () => await ReceiveRefundAsync());
        ReversePaymentCommand = new RelayCommand<SupplierPaymentReadDto>(row => _ = ReversePaymentAsync(row));
        ReverseRefundCommand = new RelayCommand<SupplierRefundReadDto>(row => _ = ReverseRefundAsync(row));
        LoadMoreStatementCommand = new RelayCommand(async () => await LoadMoreStatementAsync(), () => CanLoadMoreStatement);

        LoadMorePaymentsCommand = new RelayCommand(async () => await LoadHistoryAsync("Payments", false));
        LoadMoreRefundsCommand = new RelayCommand(async () => await LoadHistoryAsync("Refunds", false));
        LoadMoreProductsCommand = new RelayCommand(async () => await LoadHistoryAsync("Products", false));
        ReloadStatementCommand = new RelayCommand(async () => await LoadHistoryAsync("Statement", true));
        ReloadPaymentsCommand = new RelayCommand(async () => await LoadHistoryAsync("Payments", true));
        ReloadRefundsCommand = new RelayCommand(async () => await LoadHistoryAsync("Refunds", true));
        ReloadProductsCommand = new RelayCommand(async () => await LoadHistoryAsync("Products", true));
        _ = LoadAccountAsync();
    }

    public SupplierDirectoryRecord Supplier { get; }
    public double DrawerWidth => 760;
    public string Title => Supplier.Name;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Supplier.Notes) ? "-" : Supplier.Notes;
    public IReadOnlyList<SupplierSettlementMethod> SettlementMethods { get; } =
        Enum.GetValues<SupplierSettlementMethod>();

    private const int StatementPageSize = 100;
    private bool _hasMoreStatement;
    public bool HasMoreStatement
    {
        get => _hasMoreStatement;
        private set
        {
            if (SetProperty(ref _hasMoreStatement, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreStatement));
                ((RelayCommand)LoadMoreStatementCommand).NotifyCanExecuteChanged();
            }
        }
    }
    public bool CanLoadMoreStatement => HasMoreStatement && !IsLoading;
    public ICommand LoadMoreStatementCommand { get; }
    public ICommand LoadMorePaymentsCommand { get; }
    public ICommand LoadMoreRefundsCommand { get; }
    public ICommand LoadMoreProductsCommand { get; }
    public ICommand ReloadStatementCommand { get; }
    public ICommand ReloadPaymentsCommand { get; }
    public ICommand ReloadRefundsCommand { get; }
    public ICommand ReloadProductsCommand { get; }
    private bool _hasMorePayments;
    private bool _hasMoreRefunds;
    private bool _hasMoreProducts;
    public bool HasMorePayments { get => _hasMorePayments; private set => SetProperty(ref _hasMorePayments, value); }
    public bool HasMoreRefunds { get => _hasMoreRefunds; private set => SetProperty(ref _hasMoreRefunds, value); }
    public bool HasMoreProducts { get => _hasMoreProducts; private set => SetProperty(ref _hasMoreProducts, value); }


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
            if (!IsSubmitting && SetProperty(ref _transactionAmount, value))
            {
                // Editable draft changes never retire an unresolved submitted operation.
            }
        }
    }

    public SupplierSettlementMethod SelectedMethod
    {
        get => _selectedMethod;
        set
        {
            if (!IsSubmitting && SetProperty(ref _selectedMethod, value))
            {
                // Editable draft changes never retire an unresolved submitted operation.
            }
        }
    }

    public string ExternalReference
    {
        get => _externalReference;
        set
        {
            if (!IsSubmitting && SetProperty(ref _externalReference, value ?? string.Empty))
            {
                // Editable draft changes never retire an unresolved submitted operation.
            }
        }
    }

    public string TransactionNote
    {
        get => _transactionNote;
        set
        {
            if (!IsSubmitting && SetProperty(ref _transactionNote, value ?? string.Empty))
            {
                // Editable draft changes never retire an unresolved submitted operation.
            }
        }
    }

    public string ReversalReason
    {
        get => _reversalReason;
        set
        {
            if (!IsSubmitting)
            {
                SetProperty(ref _reversalReason, value ?? string.Empty);
            }
        }
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
        if (_operationsService is null || Supplier.BackendId is not Guid supplierId)
        {
            Workspace = null;
            HasMoreStatement = false;
            StatusMessage = "Supplier account authority is unavailable in preview mode.";
            return;
        }

        var version = Interlocked.Increment(ref _loadVersion);
        var previous = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cts = _loadCts;

        IsLoading = true;
        OnPropertyChanged(nameof(CanLoadMoreStatement));
        ((RelayCommand)LoadMoreStatementCommand).NotifyCanExecuteChanged();
        StatusMessage = "Loading authoritative Supplier account...";
        try
        {
            var page = await _operationsService.GetSupplierStatementPageAsync(supplierId, StatementPageSize, cancellationToken: cts?.Token ?? default);
            if (version != Volatile.Read(ref _loadVersion) || (cts?.IsCancellationRequested == true))
            {
                return;
            }

            Workspace = page.Workspace with { Statement = page.Workspace.Statement.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.EntryId).ToArray() };
            HasMoreStatement = page.HasMore;
            // A full ancillary page means more may exist; an empty terminal page clears it.
            HasMorePayments = Workspace.Payments.Count == StatementPageSize;
            HasMoreRefunds = Workspace.Refunds.Count == StatementPageSize;
            HasMoreProducts = Workspace.SuppliedProducts.Count == StatementPageSize;
            StatusMessage = Workspace.Statement.Count == 0
                ? "No Supplier account events have been posted yet."
                : "Supplier account is current.";
        }
        catch (OperationCanceledException) when (cts?.IsCancellationRequested == true)
        {
        }
        catch (OperationException ex) when (version == Volatile.Read(ref _loadVersion))
        {
            Workspace = null;
            HasMoreStatement = false;
            StatusMessage = DesktopErrorPresentation.ForException(
                ex,
                "Supplier account access was rejected.");
        }
        catch (Exception ex) when (version == Volatile.Read(ref _loadVersion))
        {
            Workspace = null;
            HasMoreStatement = false;
            StatusMessage = DesktopErrorPresentation.ForException(
                ex,
                "Supplier account authority is unavailable. Check the connection and try again.");
        }
        finally
        {
            if (version == Volatile.Read(ref _loadVersion))
            {
                IsLoading = false;
                OnPropertyChanged(nameof(CanLoadMoreStatement));
                ((RelayCommand)LoadMoreStatementCommand).NotifyCanExecuteChanged();
            }
        }
    }

    private Task LoadMoreStatementAsync() => LoadHistoryAsync("Statement", false);

    private async Task LoadHistoryAsync(string section, bool reload)
    {
        if (_operationsService is null || Supplier.BackendId is not Guid supplierId || IsLoading || Workspace is null)
        {
            return;
        }
        var hasMore = section switch
        {
            "Statement" => HasMoreStatement,
            "Payments" => HasMorePayments,
            "Refunds" => HasMoreRefunds,
            _ => HasMoreProducts
        };
        if ((!reload && !hasMore) || Interlocked.CompareExchange(ref _isLoadingStatement, 1, 0) != 0)
        {
            return;
        }
        var version = Volatile.Read(ref _loadVersion);
        var ct = _loadCts?.Token ?? CancellationToken.None;
        var cursor = new SupplierHistoryCursor();
        if (!reload)
        {
            switch (section)
            {
                case "Statement":
                    var entry = Workspace.Statement[^1];
                    cursor = cursor with { OccurredAt = entry.OccurredAt, CreatedAt = entry.CreatedAt, EntryId = entry.EntryId };
                    break;
                case "Payments":
                    var payment = Workspace.Payments[^1];
                    cursor = cursor with { PaymentAt = payment.PaidAt, PaymentId = payment.PaymentId };
                    break;
                case "Refunds":
                    var refund = Workspace.Refunds[^1];
                    cursor = cursor with { RefundAt = refund.ReceivedAt, RefundId = refund.RefundId };
                    break;
                default:
                    var product = Workspace.SuppliedProducts[^1];
                    cursor = cursor with { ProductName = product.ProductName, ProductId = product.ProductId };
                    break;
            }
        }
        IsLoading = true;
        try
        {
            var page = await _operationsService.GetSupplierHistoryPageAsync(supplierId, StatementPageSize, cursor, ct);
            bool? moreStatement = null;
            if (section == "Statement")
            {
                // Preserve the authoritative exact end-of-results probe.
                moreStatement = false;
                if (page.Statement.Count == StatementPageSize)
                {
                    var last = page.Statement.OrderBy(x => x.OccurredAt).ThenBy(x => x.CreatedAt).ThenBy(x => x.EntryId).First();
                    var probe = await _operationsService.GetSupplierHistoryPageAsync(supplierId, 1,
                        new(last.OccurredAt, last.CreatedAt, last.EntryId), ct);
                    moreStatement = probe.Statement.Count != 0;
                }
            }
            if (version != Volatile.Read(ref _loadVersion) || ct.IsCancellationRequested)
            {
                return;
            }
            // Each section replaces only its own rows. Latest/First reload recovers evicted
            // pages without retaining an unbounded cache or touching the other cursors.
            switch (section)
            {
                case "Statement":
                    var ordered = page.Statement.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.CreatedAt).ThenByDescending(x => x.EntryId).ToArray();
                    Workspace = Workspace with { Statement = Window(reload ? [] : Workspace.Statement, ordered, x => x.EntryId) };
                    HasMoreStatement = moreStatement!.Value;
                    break;
                case "Payments":
                    Workspace = Workspace with { Payments = Window(reload ? [] : Workspace.Payments, page.Payments, x => x.PaymentId) };
                    HasMorePayments = page.Payments.Count == StatementPageSize;
                    break;
                case "Refunds":
                    Workspace = Workspace with { Refunds = Window(reload ? [] : Workspace.Refunds, page.Refunds, x => x.RefundId) };
                    HasMoreRefunds = page.Refunds.Count == StatementPageSize;
                    break;
                default:
                    Workspace = Workspace with { SuppliedProducts = Window(reload ? [] : Workspace.SuppliedProducts, page.SuppliedProducts, x => x.ProductId) };
                    HasMoreProducts = page.SuppliedProducts.Count == StatementPageSize;
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _loadVersion))
        {
            _toastService.Show(DesktopErrorPresentation.ForException(ex, "History could not be loaded. Retry or reload this section."), ToastTone.Danger);
        }
        finally
        {
            Interlocked.Exchange(ref _isLoadingStatement, 0);
            if (version == Volatile.Read(ref _loadVersion))
            {
                IsLoading = false;
                OnPropertyChanged(nameof(CanLoadMoreStatement));
                ((RelayCommand)LoadMoreStatementCommand).NotifyCanExecuteChanged();
            }
        }
    }

    private static IReadOnlyList<T> Window<T>(IReadOnlyList<T> retained, IReadOnlyList<T> page, Func<T, Guid> id)
    {
        var seen = retained.Select(id).ToHashSet();
        if (page.Count > StatementPageSize || page.Any(row => !seen.Add(id(row))))
        {
            throw new InvalidOperationException("History cursor returned duplicate or oversized results.");
        }
        return retained.Concat(page).TakeLast(MaxRetainedStatementEntries).ToArray();
    }

    private async Task PostPaymentAsync(SupplierPaymentPurpose purpose)
    {
        if (_operationsService is null || Supplier.BackendId is not Guid supplierId)
        {
            return;
        }

        if (_pendingSubmission is null && TransactionAmount <= 0m)
        {
            _toastService.Show("Enter a payment amount greater than zero.", ToastTone.Warning);
            return;
        }

        if (!BeginFinancialSubmission("payment:" + purpose))
        {
            return;
        }
        try
        {
            var operationId = purpose == SupplierPaymentPurpose.Advance
                ? (_pendingAdvanceOperationId ??= Guid.CreateVersion7())
                : (_pendingSettlementOperationId ??= Guid.CreateVersion7());
            var submitted = _pendingSubmission ??= new(operationId, TransactionAmount, SelectedMethod, ExternalReference, TransactionNote);

            try
            {
                await _operationsService.CreateSupplierPaymentAsync(
                    supplierId,
                    submitted.Amount,
                    purpose,
                    submitted.Method,
                    submitted.OperationId,
                    submitted.ExternalReference,
                    submitted.Note);

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
                ConfirmFinancialSubmission();
                ClearTransactionEditor();
                await LoadAccountAsync();
            }
            catch (OperationException ex)
            {
                _toastService.Show(
                    $"Supplier operation remains unresolved; original identity and values retained. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm this intent.")}",
                    ToastTone.Danger);
            }
            catch (Exception ex)
            {
                _toastService.Show(
                    $"Outcome is uncertain. Check operation status before retrying; the same operation identity will be reused. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm the result.")}",
                    ToastTone.Danger);
            }
        }
        finally
        {
            EndFinancialSubmission();
        }
    }

    private async Task ReceiveRefundAsync()
    {
        if (_operationsService is null || Supplier.BackendId is not Guid supplierId)
        {
            return;
        }

        if (_pendingSubmission is null && TransactionAmount <= 0m)
        {
            _toastService.Show("Enter a refund amount greater than zero.", ToastTone.Warning);
            return;
        }

        if (!BeginFinancialSubmission("refund"))
        {
            return;
        }
        try
        {
            _pendingRefundOperationId ??= Guid.CreateVersion7();
            var submitted = _pendingSubmission ??= new(_pendingRefundOperationId.Value, TransactionAmount, SelectedMethod, ExternalReference, TransactionNote);
            try
            {
                await _operationsService.CreateSupplierRefundAsync(
                    supplierId,
                    submitted.Amount,
                    submitted.Method,
                    submitted.OperationId,
                    submitted.ExternalReference,
                    submitted.Note);
                _pendingRefundOperationId = null;
                _toastService.Show("Supplier refund received.", ToastTone.Success);
                ConfirmFinancialSubmission();
                ClearTransactionEditor();
                await LoadAccountAsync();
            }
            catch (OperationException ex)
            {
                _toastService.Show(
                    $"Supplier operation remains unresolved; original identity and values retained. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm this intent.")}",
                    ToastTone.Danger);
            }
            catch (Exception ex)
            {
                _toastService.Show(
                    $"Outcome is uncertain. Check operation status before retrying; the same operation identity will be reused. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm the result.")}",
                    ToastTone.Danger);
            }
        }
        finally
        {
            EndFinancialSubmission();
        }
    }

    private async Task ReversePaymentAsync(SupplierPaymentReadDto payment)
    {
        if (_operationsService is null)
        {
            return;
        }

        if (!HasUnresolvedFinancialOperation && string.IsNullOrWhiteSpace(ReversalReason))
        {
            _toastService.Show("Enter a reversal reason first.", ToastTone.Warning);
            return;
        }

        if (!BeginFinancialSubmission("payment-reversal:" + payment.PaymentId))
        {
            return;
        }
        try
        {
            if (!_pendingPaymentReversals.TryGetValue(payment.PaymentId, out var operationId))
            {
                operationId = Guid.CreateVersion7();
                _pendingPaymentReversals[payment.PaymentId] = operationId;
            }

            if (!_paymentReversalReasons.ContainsKey(payment.PaymentId))
            {
                _paymentReversalReasons[payment.PaymentId] = ReversalReason;
            }
            var submittedReason = _paymentReversalReasons[payment.PaymentId];

            try
            {
                await _operationsService.ReverseSupplierPaymentAsync(
                    payment.PaymentId,
                    submittedReason,
                    operationId);
                _pendingPaymentReversals.Remove(payment.PaymentId);
                _paymentReversalReasons.Remove(payment.PaymentId);
                ConfirmFinancialSubmission();
                _reversalReason = string.Empty;
                OnPropertyChanged(nameof(ReversalReason));
                _toastService.Show("Supplier payment reversed through append-only correction.", ToastTone.Success);
                await LoadAccountAsync();
            }
            catch (OperationException ex)
            {
                _toastService.Show(
                    $"Supplier operation remains unresolved; original identity and values retained. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm this intent.")}",
                    ToastTone.Danger);
            }
            catch (Exception ex)
            {
                _toastService.Show(
                    $"Outcome is uncertain. Check operation status before retrying; this payment reversal will reuse its operation identity. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm the result.")}",
                    ToastTone.Danger);
            }
        }
        finally
        {
            EndFinancialSubmission();
        }
    }

    private async Task ReverseRefundAsync(SupplierRefundReadDto refund)
    {
        if (_operationsService is null)
        {
            return;
        }

        if (!HasUnresolvedFinancialOperation && string.IsNullOrWhiteSpace(ReversalReason))
        {
            _toastService.Show("Enter a reversal reason first.", ToastTone.Warning);
            return;
        }

        if (!BeginFinancialSubmission("refund-reversal:" + refund.RefundId))
        {
            return;
        }
        try
        {
            if (!_pendingRefundReversals.TryGetValue(refund.RefundId, out var operationId))
            {
                operationId = Guid.CreateVersion7();
                _pendingRefundReversals[refund.RefundId] = operationId;
            }

            if (!_refundReversalReasons.ContainsKey(refund.RefundId))
            {
                _refundReversalReasons[refund.RefundId] = ReversalReason;
            }
            var submittedReason = _refundReversalReasons[refund.RefundId];

            try
            {
                await _operationsService.ReverseSupplierRefundAsync(
                    refund.RefundId,
                    submittedReason,
                    operationId);
                _pendingRefundReversals.Remove(refund.RefundId);
                _refundReversalReasons.Remove(refund.RefundId);
                ConfirmFinancialSubmission();
                _reversalReason = string.Empty;
                OnPropertyChanged(nameof(ReversalReason));
                _toastService.Show("Supplier refund reversed through append-only correction.", ToastTone.Success);
                await LoadAccountAsync();
            }
            catch (OperationException ex)
            {
                _toastService.Show(
                    $"Supplier operation remains unresolved; original identity and values retained. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm this intent.")}",
                    ToastTone.Danger);
            }
            catch (Exception ex)
            {
                _toastService.Show(
                    $"Outcome is uncertain. Check operation status before retrying; this refund reversal will reuse its operation identity. {DesktopErrorPresentation.ForException(ex, "The Server did not confirm the result.")}",
                    ToastTone.Danger);
            }
        }
        finally
        {
            EndFinancialSubmission();
        }
    }

    private bool CanCloseFinancialWorkspace()
    {
        if (IsSubmitting || HasUnresolvedFinancialOperation)
        {
            _toastService.Show("Keep this workspace open until the supplier operation is resolved. Durable restart recovery is not available.", ToastTone.Warning);
            return false;
        }
        return true;
    }

    private void Edit()
    {
        if (!CanCloseFinancialWorkspace())
        {
            return;
        }
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
    private readonly IBackendOperationsService? _operationsService;
    private readonly List<SupplierDirectoryRecord> _backendSuppliers = [];
    private bool _backendLoaded;
    private bool _backendLoading;
    private CancellationTokenSource? _searchCts;
    private long _searchVersion;
    private bool _disposed;
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private string _searchText = string.Empty;

    private const int PageSize = 100;
    private bool _hasMoreSuppliers;
    public bool HasMoreSuppliers
    {
        get => _hasMoreSuppliers;
        private set
        {
            if (SetProperty(ref _hasMoreSuppliers, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreSuppliers));
                ((RelayCommand)LoadMoreSuppliersCommand).NotifyCanExecuteChanged();
            }
        }
    }
    public bool CanLoadMoreSuppliers => HasMoreSuppliers && !_backendLoading;

    public SuppliersViewModel(
        IToastService toastService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IBackendBusinessOperationsService? backendService = null,
        IBackendOperationsService? operationsService = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _backendService = backendService;
        _operationsService = operationsService;

        FilteredSuppliers = [];
        AddSupplierCommand = new RelayCommand(OpenAddSupplier);
        ViewSupplierCommand = new RelayCommand<SupplierDirectoryRecord>(OpenSupplier);
        LoadMoreSuppliersCommand = new RelayCommand(async () => await LoadMoreSuppliersAsync(), () => CanLoadMoreSuppliers);

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
        _disposed = true;
        Interlocked.Increment(ref _searchVersion);
        var pending = Interlocked.Exchange(ref _searchCts, null);
        pending?.Cancel();
        pending?.Dispose();
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
    public ICommand LoadMoreSuppliersCommand { get; }

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
            _operationsService));
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

    private Task RefreshBackendAsync() => LoadDirectoryAsync(SearchText, debounce: false);

    private Task ScheduleSearchAsync(string search) => LoadDirectoryAsync(search, debounce: true);

    private async Task LoadDirectoryAsync(string search, bool debounce)
    {
        if (_disposed)
        {
            return;
        }

        if (_backendService is null)
        {
            Refresh();
            return;
        }

        // Every read, including post-save refresh, participates in one ordering domain.
        var version = Interlocked.Increment(ref _searchVersion);
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var previous = Interlocked.Exchange(ref _searchCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        _backendLoading = true;
        OnPropertyChanged(nameof(CanLoadMoreSuppliers));
        ((RelayCommand)LoadMoreSuppliersCommand).NotifyCanExecuteChanged();
        try
        {
            if (debounce)
            {
                await Task.Delay(250, token);
            }

            var records = await _backendService.GetSuppliersAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                PageSize,
                token);
            if (_disposed || token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            _backendSuppliers.Clear();
            _backendSuppliers.AddRange(records);
            _backendLoaded = true;
            HasMoreSuppliers = records.Count >= PageSize;
            ApplyFilter(_backendSuppliers);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested && version == Volatile.Read(ref _searchVersion))
            {
                _toastService.Show(
                    DesktopErrorPresentation.ForException(ex, "Suppliers could not be refreshed. Check the connection and try again."),
                    ToastTone.Danger);
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _searchVersion))
            {
                _backendLoading = false;
                OnPropertyChanged(nameof(CanLoadMoreSuppliers));
                ((RelayCommand)LoadMoreSuppliersCommand).NotifyCanExecuteChanged();
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCts, null, cts), cts))
            {
                cts.Dispose();
            }
        }
    }

    private async Task LoadMoreSuppliersAsync()
    {
        if (_disposed || _backendService is null || _backendLoading || !HasMoreSuppliers || _backendSuppliers.Count == 0)
        {
            return;
        }

        var version = Volatile.Read(ref _searchVersion);
        var search = SearchText;
        var cts = new CancellationTokenSource();
        var token = cts.Token;
        var previous = Interlocked.Exchange(ref _searchCts, cts);
        previous?.Cancel();
        previous?.Dispose();
        _backendLoading = true;
        OnPropertyChanged(nameof(CanLoadMoreSuppliers));
        ((RelayCommand)LoadMoreSuppliersCommand).NotifyCanExecuteChanged();
        try
        {
            // Deterministic keyset tie-breaker: (Name ASC, Id ASC)
            var last = _backendSuppliers[^1];
            var beforeName = last.Name;
            var beforeSupplierId = last.BackendId;

            var next = await _backendService.GetSuppliersAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                PageSize,
                beforeName,
                beforeSupplierId,
                token);

            if (_disposed || token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            _backendSuppliers.AddRange(next);
            HasMoreSuppliers = next.Count >= PageSize;
            ApplyFilter(_backendSuppliers);
        }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested && version == Volatile.Read(ref _searchVersion))
            {
                _toastService.Show(
                    DesktopErrorPresentation.ForException(ex, "Failed to load more suppliers."),
                    ToastTone.Danger);
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _searchVersion))
            {
                _backendLoading = false;
                OnPropertyChanged(nameof(CanLoadMoreSuppliers));
                ((RelayCommand)LoadMoreSuppliersCommand).NotifyCanExecuteChanged();
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCts, null, cts), cts))
            {
                cts.Dispose();
            }
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
