using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class CustomerEditViewModel : ViewModelBase
{
    private int _submissionGate;
    private bool _confirmed;
    public bool IsBusy => Volatile.Read(ref _submissionGate) != 0;
    public bool CanEdit => !IsBusy && !_confirmed;
    public string SubmissionStatus => IsBusy ? "Submitting customer…" : string.Empty;
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly CustomerDirectoryRecord? _existing;
    private readonly IToastService _toastService;
    private readonly Action _close;
    private readonly Action? _saved;

    private string _name;
    private string _phone;
    private string _address;
    private string _notes;

    public CustomerEditViewModel(
        IToastService toastService,
        Action close,
        CustomerDirectoryRecord? existing = null,
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
        _address = existing?.Address ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;

        SaveCommand = new RelayCommand(async () => await SaveAsync(), () => !IsBusy && !_confirmed);
        CancelCommand = new RelayCommand(_close);
    }

    public string Title => _existing is null ? "Add Customer" : "Edit Customer";
    public string SaveButtonText => _existing is null ? "Add Customer" : "Save Changes";

    public string Name
    {
        get => _name;
        set { if (CanEdit) { SetProperty(ref _name, value ?? string.Empty); } }
    }

    public string Phone
    {
        get => _phone;
        set { if (CanEdit) { SetProperty(ref _phone, value ?? string.Empty); } }
    }

    public string Address
    {
        get => _address;
        set { if (CanEdit) { SetProperty(ref _address, value ?? string.Empty); } }
    }

    public string Notes
    {
        get => _notes;
        set { if (CanEdit) { SetProperty(ref _notes, value ?? string.Empty); } }
    }

    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task SaveAsync()
    {
        if (Interlocked.CompareExchange(ref _submissionGate, 1, 0) != 0)
        {
            return;
        }
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(SubmissionStatus));
        ((RelayCommand)SaveCommand).NotifyCanExecuteChanged();
        var name = Name;
        var phone = Phone;
        var address = Address;
        var notes = Notes;
        try
        {
            if (_confirmed)
            {
                return;
            }
            if (_backendService is null)
            {
                _service.SaveCustomer(_existing, name, phone, address, notes);
            }
            else
            {
                await _backendService.SaveCustomerAsync(
                    _existing,
                    name,
                    phone,
                    address,
                    notes);
            }
            _confirmed = true;
            _toastService.Show(
                _existing is null ? "Customer added." : "Customer updated.",
                ToastTone.Success);
            _close();
            _saved?.Invoke();
        }
        catch (Exception ex)
        {
            _toastService.Show(
                _confirmed
                    ? "Customer saved. The customer list could not be refreshed; refresh it without saving again."
                    : CustomerSaveFailure(ex),
                ToastTone.Danger);
        }
        finally
        {
            Interlocked.Exchange(ref _submissionGate, 0);
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(SubmissionStatus));
            ((RelayCommand)SaveCommand).NotifyCanExecuteChanged();
        }
    }

    private static string CustomerSaveFailure(Exception exception)
    {
        var rejected = exception is DesktopApiException apiError && apiError.StatusCode is not null
            && (int)apiError.StatusCode < 500 && apiError.StatusCode != System.Net.HttpStatusCode.RequestTimeout
            && apiError.StatusCode != System.Net.HttpStatusCode.Conflict;
        return rejected
            ? $"Customer save was rejected. {DesktopErrorPresentation.ForException(exception, "Review the submitted details and permissions.")}"
            : $"Customer save outcome is unconfirmed. Check the original operation before retrying. {DesktopErrorPresentation.ForException(exception, "The Server did not confirm the save.")}";
    }
}

public sealed class CustomerDetailViewModel : ViewModelBase
{
    private readonly IDrawerService _drawerService;
    private readonly IDialogService _dialogService;
    private readonly IToastService _toastService;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly Action? _updated;

    public CustomerDetailViewModel(
        CustomerDirectoryRecord customer,
        IDrawerService drawerService,
        IDialogService dialogService,
        IToastService toastService,
        Action? updated = null,
        IBackendBusinessOperationsService? backendService = null)
    {
        Customer = customer;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _toastService = toastService;
        _updated = updated;
        _backendService = backendService;

        CloseCommand = new RelayCommand(_drawerService.Close);
        EditCommand = new RelayCommand(Edit);
        ToggleSuspensionCommand = new RelayCommand(async () => await ToggleSuspensionAsync(), () => !_changingStatus);
    }

    public CustomerDirectoryRecord Customer { get; }
    public string Title => Customer.Name;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Customer.Notes) ? "—" : Customer.Notes;

    public ICommand CloseCommand { get; }
    public ICommand EditCommand { get; }
    private bool _changingStatus;
    public string SuspensionAction => Customer.IsActive ? "Suspend Customer" : "Resume Customer";
    public ICommand ToggleSuspensionCommand { get; }

    private async Task ToggleSuspensionAsync()
    {
        if (_changingStatus)
        {
            return;
        }
        _changingStatus = true;
        ((RelayCommand)ToggleSuspensionCommand).NotifyCanExecuteChanged();
        var target = !Customer.IsActive;
        try
        {
            if (_backendService is null)
            {
                throw new InvalidOperationException("Customer status authority is not attached.");
            }
            await _backendService.SetCustomerSuspensionAsync(Customer, !target);
            Customer.IsActive = target;
            OnPropertyChanged(nameof(SuspensionAction));
            _updated?.Invoke();
            _toastService.Show(target ? "Customer resumed." : "Customer suspended. Existing payments and history remain available.", ToastTone.Success);
        }
        catch (Exception ex)
        {
            _toastService.Show(DesktopErrorPresentation.ForException(ex, "Customer status could not be changed."), ToastTone.Danger);
        }
        finally
        {
            _changingStatus = false;
            ((RelayCommand)ToggleSuspensionCommand).NotifyCanExecuteChanged();
        }
    }

    private void Edit()
    {
        _drawerService.Close();
        _dialogService.Show(new CustomerEditViewModel(
            _toastService,
            _dialogService.Close,
            Customer,
            _updated,
            _backendService));
    }
}

public sealed class CustomersViewModel : ViewModelBase, IDisposable
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IBackendBusinessOperationsService? _backendService;
    private readonly List<CustomerDirectoryRecord> _backendCustomers = [];
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
    private bool _hasMore;
    public bool HasMoreCustomers
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value))
            {
                OnPropertyChanged(nameof(CanLoadMoreCustomers));
                ((RelayCommand)LoadMoreCustomersCommand).NotifyCanExecuteChanged();
            }
        }
    }
    public bool CanLoadMoreCustomers => HasMoreCustomers && !_backendLoading;

    public CustomersViewModel(
        IToastService toastService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IBackendBusinessOperationsService? backendService = null)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _backendService = backendService;

        FilteredCustomers = [];
        AddCustomerCommand = new RelayCommand(OpenAddCustomer);
        ViewCustomerCommand = new RelayCommand<CustomerDirectoryRecord>(OpenCustomer);
        LoadMoreCustomersCommand = new RelayCommand(async () => await LoadMoreCustomersAsync(), () => CanLoadMoreCustomers);

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

    public ObservableCollection<CustomerDirectoryRecord> FilteredCustomers { get; }

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

    public ICommand AddCustomerCommand { get; }
    public ICommand ViewCustomerCommand { get; }
    public ICommand LoadMoreCustomersCommand { get; }

    private void OpenAddCustomer()
    {
        _dialogService.Show(new CustomerEditViewModel(
            _toastService,
            _dialogService.Close,
            saved: RefreshAfterMutation,
            backendService: _backendService));
    }

    private void OpenCustomer(CustomerDirectoryRecord customer)
    {
        _drawerService.Show(new CustomerDetailViewModel(
            customer,
            _drawerService,
            _dialogService,
            _toastService,
            RefreshAfterMutation,
            _backendService));
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

            ApplyFilter(_backendCustomers);
            return;
        }

        ApplyFilter(_service.Customers);
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
        OnPropertyChanged(nameof(CanLoadMoreCustomers));
        ((RelayCommand)LoadMoreCustomersCommand).NotifyCanExecuteChanged();
        try
        {
            if (debounce)
            {
                await Task.Delay(250, token);
            }

            var records = await _backendService.GetCustomersAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                PageSize,
                token);
            if (_disposed || token.IsCancellationRequested || version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            _backendCustomers.Clear();
            _backendCustomers.AddRange(records);
            _backendLoaded = true;
            HasMoreCustomers = records.Count >= PageSize;
            ApplyFilter(_backendCustomers);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!_disposed && !token.IsCancellationRequested && version == Volatile.Read(ref _searchVersion))
            {
                _toastService.Show(
                    DesktopErrorPresentation.ForException(ex, "Customers could not be refreshed. Check the connection and try again."),
                    ToastTone.Danger);
            }
        }
        finally
        {
            if (version == Volatile.Read(ref _searchVersion))
            {
                _backendLoading = false;
                OnPropertyChanged(nameof(CanLoadMoreCustomers));
                ((RelayCommand)LoadMoreCustomersCommand).NotifyCanExecuteChanged();
            }
            if (ReferenceEquals(Interlocked.CompareExchange(ref _searchCts, null, cts), cts))
            {
                cts.Dispose();
            }
        }
    }

    private async Task LoadMoreCustomersAsync()
    {
        if (_disposed || _backendService is null || _backendLoading || !HasMoreCustomers || _backendCustomers.Count == 0)
        {
            return;
        }

        var version = Volatile.Read(ref _searchVersion);
        var search = SearchText;
        _backendLoading = true;
        OnPropertyChanged(nameof(CanLoadMoreCustomers));
        ((RelayCommand)LoadMoreCustomersCommand).NotifyCanExecuteChanged();
        try
        {
            // Deterministic keyset tie-breaker: (Name ASC, Id ASC)
            var last = _backendCustomers[^1];
            var beforeName = last.Name;
            var beforeCustomerId = last.BackendId;

            var next = await _backendService.GetCustomersAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                PageSize,
                beforeName,
                beforeCustomerId);

            if (_disposed || version != Volatile.Read(ref _searchVersion))
            {
                return;
            }

            _backendCustomers.AddRange(next);
            HasMoreCustomers = next.Count >= PageSize;
            ApplyFilter(_backendCustomers);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                DesktopErrorPresentation.ForException(ex, "Failed to load more customers."),
                ToastTone.Danger);
        }
        finally
        {
            if (version == Volatile.Read(ref _searchVersion))
            {
                _backendLoading = false;
                OnPropertyChanged(nameof(CanLoadMoreCustomers));
                ((RelayCommand)LoadMoreCustomersCommand).NotifyCanExecuteChanged();
            }
        }
    }

    private void ApplyFilter(IEnumerable<CustomerDirectoryRecord> source)
    {
        var query = source;

        FilteredCustomers.Clear();
        foreach (var customer in query.OrderBy(customer => customer.Name))
        {
            FilteredCustomers.Add(customer);
        }
    }
}
