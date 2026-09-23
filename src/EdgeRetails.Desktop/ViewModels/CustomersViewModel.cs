using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class CustomerEditViewModel : ViewModelBase
{
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

        SaveCommand = new RelayCommand(async () => await SaveAsync());
        CancelCommand = new RelayCommand(_close);
    }

    public string Title => _existing is null ? "Add Customer" : "Edit Customer";
    public string SaveButtonText => _existing is null ? "Add Customer" : "Save Changes";

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
                _service.SaveCustomer(_existing, Name, Phone, Address, Notes);
            }
            else
            {
                await _backendService.SaveCustomerAsync(
                    _existing,
                    Name,
                    Phone,
                    Address,
                    Notes);
            }
            _toastService.Show(
                _existing is null ? "Customer added." : "Customer updated.",
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
    }

    public CustomerDirectoryRecord Customer { get; }
    public string Title => Customer.Name;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Customer.Notes) ? "—" : Customer.Notes;

    public ICommand CloseCommand { get; }
    public ICommand EditCommand { get; }

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
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private string _searchText = string.Empty;

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

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null || _backendLoading)
        {
            return;
        }

        _backendLoading = true;
        try
        {
            var customers = await _backendService.GetCustomersAsync(
                string.IsNullOrWhiteSpace(SearchText) ? null : SearchText,
                200);
            _backendCustomers.Clear();
            _backendCustomers.AddRange(customers);
            _backendLoaded = true;
            ApplyFilter(_backendCustomers);
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Customers could not be refreshed: {ex.Message}",
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

            var customers = await _backendService.GetCustomersAsync(
                string.IsNullOrWhiteSpace(search) ? null : search,
                200,
                cts.Token);

            if (version != Volatile.Read(ref _searchVersion) || cts.IsCancellationRequested)
            {
                return;
            }

            _backendCustomers.Clear();
            _backendCustomers.AddRange(customers);
            _backendLoaded = true;
            ApplyFilter(_backendCustomers);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _searchVersion))
        {
            _toastService.Show($"Customers search failed: {ex.Message}", ToastTone.Danger);
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
