using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class CustomerEditViewModel : ViewModelBase
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
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
        Action? saved = null)
    {
        _toastService = toastService;
        _close = close;
        _existing = existing;
        _saved = saved;

        _name = existing?.Name ?? string.Empty;
        _phone = existing?.Phone ?? string.Empty;
        _address = existing?.Address ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;

        SaveCommand = new RelayCommand(Save);
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

    private void Save()
    {
        try
        {
            _service.SaveCustomer(_existing, Name, Phone, Address, Notes);
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
    private readonly Action? _updated;

    public CustomerDetailViewModel(
        CustomerDirectoryRecord customer,
        IDrawerService drawerService,
        IDialogService dialogService,
        IToastService toastService,
        Action? updated = null)
    {
        Customer = customer;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _toastService = toastService;
        _updated = updated;

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
            _updated));
    }
}

public sealed class CustomersViewModel : ViewModelBase
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private string _searchText = string.Empty;

    public CustomersViewModel(
        IToastService toastService,
        IDialogService dialogService,
        IDrawerService drawerService)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _drawerService = drawerService;

        FilteredCustomers = [];
        AddCustomerCommand = new RelayCommand(OpenAddCustomer);
        ViewCustomerCommand = new RelayCommand<CustomerDirectoryRecord>(OpenCustomer);

        _service.StateChanged += OnStateChanged;
        Refresh();
    }

    public ObservableCollection<CustomerDirectoryRecord> FilteredCustomers { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                Refresh();
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
            saved: Refresh));
    }

    private void OpenCustomer(CustomerDirectoryRecord customer)
    {
        _drawerService.Show(new CustomerDetailViewModel(
            customer,
            _drawerService,
            _dialogService,
            _toastService,
            Refresh));
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        IEnumerable<CustomerDirectoryRecord> query = _service.Customers;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(customer =>
                customer.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                customer.Phone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                customer.ActiveThaka.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FilteredCustomers.Clear();
        foreach (var customer in query.OrderBy(customer => customer.Name))
        {
            FilteredCustomers.Add(customer);
        }
    }
}
