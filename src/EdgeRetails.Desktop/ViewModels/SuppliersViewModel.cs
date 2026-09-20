using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class SupplierEditViewModel : ViewModelBase
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
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
        Action? saved = null)
    {
        _toastService = toastService;
        _close = close;
        _existing = existing;
        _saved = saved;

        _name = existing?.Name ?? string.Empty;
        _phone = existing?.Phone ?? string.Empty;
        _city = existing?.City ?? string.Empty;
        _address = existing?.Address ?? string.Empty;
        _notes = existing?.Notes ?? string.Empty;

        SaveCommand = new RelayCommand(Save);
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

    private void Save()
    {
        try
        {
            _service.SaveSupplier(_existing, Name, Phone, City, Address, Notes);
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
    private readonly Action? _updated;

    public SupplierDetailViewModel(
        SupplierDirectoryRecord supplier,
        IDrawerService drawerService,
        IDialogService dialogService,
        IToastService toastService,
        Action? updated = null)
    {
        Supplier = supplier;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _toastService = toastService;
        _updated = updated;

        CloseCommand = new RelayCommand(_drawerService.Close);
        EditCommand = new RelayCommand(Edit);
    }

    public SupplierDirectoryRecord Supplier { get; }
    public string Title => Supplier.Name;
    public string NotesDisplay => string.IsNullOrWhiteSpace(Supplier.Notes) ? "—" : Supplier.Notes;

    public ICommand CloseCommand { get; }
    public ICommand EditCommand { get; }

    private void Edit()
    {
        _drawerService.Close();
        _dialogService.Show(new SupplierEditViewModel(
            _toastService,
            _dialogService.Close,
            Supplier,
            _updated));
    }
}

public sealed class SuppliersViewModel : ViewModelBase
{
    private readonly DemoBusinessDirectoryService _service = DemoBusinessDirectoryService.Instance;
    private readonly IToastService _toastService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private string _searchText = string.Empty;

    public SuppliersViewModel(
        IToastService toastService,
        IDialogService dialogService,
        IDrawerService drawerService)
    {
        _toastService = toastService;
        _dialogService = dialogService;
        _drawerService = drawerService;

        FilteredSuppliers = [];
        AddSupplierCommand = new RelayCommand(OpenAddSupplier);
        ViewSupplierCommand = new RelayCommand<SupplierDirectoryRecord>(OpenSupplier);

        _service.StateChanged += OnStateChanged;
        Refresh();
    }

    public ObservableCollection<SupplierDirectoryRecord> FilteredSuppliers { get; }

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

    public ICommand AddSupplierCommand { get; }
    public ICommand ViewSupplierCommand { get; }

    private void OpenAddSupplier()
    {
        _dialogService.Show(new SupplierEditViewModel(
            _toastService,
            _dialogService.Close,
            saved: Refresh));
    }

    private void OpenSupplier(SupplierDirectoryRecord supplier)
    {
        _drawerService.Show(new SupplierDetailViewModel(
            supplier,
            _drawerService,
            _dialogService,
            _toastService,
            Refresh));
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        IEnumerable<SupplierDirectoryRecord> query = _service.Suppliers;
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(supplier =>
                supplier.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                supplier.Phone.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                supplier.City.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        FilteredSuppliers.Clear();
        foreach (var supplier in query.OrderBy(supplier => supplier.Name))
        {
            FilteredSuppliers.Add(supplier);
        }
    }
}
