using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class SaleCustomerPickerViewModel : ViewModelBase
{
    private readonly IReadOnlyList<CustomerDirectoryRecord> _source;
    private readonly Action<CustomerDirectoryRecord?> _select;
    private readonly Action _close;
    private string _searchText = string.Empty;
    private CustomerDirectoryRecord? _selectedCustomer;

    public SaleCustomerPickerViewModel(
        IReadOnlyList<CustomerDirectoryRecord> customers,
        CustomerDirectoryRecord? selectedCustomer,
        Action<CustomerDirectoryRecord?> select,
        Action close)
    {
        ArgumentNullException.ThrowIfNull(customers);
        ArgumentNullException.ThrowIfNull(select);
        ArgumentNullException.ThrowIfNull(close);

        _source = customers;
        _selectedCustomer = selectedCustomer;
        _select = select;
        _close = close;

        FilteredCustomers = [];
        ApplyFilter();

        UseWalkInCommand = new RelayCommand(UseWalkIn);
        SelectCustomerCommand = new RelayCommand<CustomerDirectoryRecord>(SelectCustomer);
        CancelCommand = new RelayCommand(_close);
    }
    public ObservableCollection<CustomerDirectoryRecord> FilteredCustomers { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                ApplyFilter();
            }
        }
    }

    public CustomerDirectoryRecord? SelectedCustomer
    {
        get => _selectedCustomer;
        set => SetProperty(ref _selectedCustomer, value);
    }

    public bool HasCustomers => FilteredCustomers.Count > 0;

    public ICommand UseWalkInCommand { get; }
    public ICommand SelectCustomerCommand { get; }
    public ICommand CancelCommand { get; }

    private void UseWalkIn()
    {
        _select(null);
        _close();
    }

    private void SelectCustomer(CustomerDirectoryRecord customer)
    {
        if (customer is null)
        {
            return;
        }

        SelectedCustomer = customer;
        _select(customer);
        _close();
    }
    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        var query = _source.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(term))
        {
            query = query.Where(customer =>
                customer.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                customer.Phone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                customer.Address.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FilteredCustomers.Clear();
        foreach (var customer in query.OrderBy(customer => customer.Name))
        {
            FilteredCustomers.Add(customer);
        }

        OnPropertyChanged(nameof(HasCustomers));
    }
}
