using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PurchaseHistoryViewModel : ViewModelBase
{
    private readonly DemoPurchaseInventoryService _service;
    private readonly IToastService _toastService;
    private readonly IDrawerService _drawerService;
    private readonly IDialogService _dialogService;
    private string _searchText = string.Empty;
    private string _selectedPeriod = "ThisMonth";
    private string _selectedSupplier = "All";
    private bool _isNewPurchaseActive;
    private NewPurchaseViewModel? _currentNewPurchase;
    private PurchaseRecord? _selectedPurchase;

    public PurchaseHistoryViewModel(
        IToastService toastService,
        IDrawerService drawerService,
        IDialogService dialogService)
    {
        _toastService = toastService;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _service = DemoPurchaseInventoryService.Instance;

        FilteredPurchases = [];
        Suppliers = ["All", .. _service.Suppliers];

        NewPurchaseCommand = new RelayCommand(OpenNewPurchase);
        CloseNewPurchaseCommand = new RelayCommand(CloseNewPurchase);
        SelectPeriodCommand = new RelayCommand<string>(SelectPeriod);
        RefreshCommand = new RelayCommand(Refresh);
        ViewPurchaseCommand = new RelayCommand<PurchaseRecord>(SelectPurchase);

        _service.StateChanged += OnServiceStateChanged;
        Refresh();
    }

    public ObservableCollection<PurchaseRecord> FilteredPurchases { get; }
    public IReadOnlyList<string> Suppliers { get; }

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

    public string SelectedPeriod
    {
        get => _selectedPeriod;
        private set
        {
            if (SetProperty(ref _selectedPeriod, value))
            {
                OnPropertyChanged(nameof(IsTodaySelected));
                OnPropertyChanged(nameof(IsThisWeekSelected));
                OnPropertyChanged(nameof(IsThisMonthSelected));
                Refresh();
            }
        }
    }

    public string SelectedSupplier
    {
        get => _selectedSupplier;
        set
        {
            if (SetProperty(ref _selectedSupplier, value ?? "All"))
            {
                Refresh();
            }
        }
    }

    public bool IsTodaySelected => SelectedPeriod == "Today";
    public bool IsThisWeekSelected => SelectedPeriod == "ThisWeek";
    public bool IsThisMonthSelected => SelectedPeriod == "ThisMonth";

    public bool IsNewPurchaseActive
    {
        get => _isNewPurchaseActive;
        private set => SetProperty(ref _isNewPurchaseActive, value);
    }

    public NewPurchaseViewModel? CurrentNewPurchase
    {
        get => _currentNewPurchase;
        private set => SetProperty(ref _currentNewPurchase, value);
    }

    public PurchaseRecord? SelectedPurchase
    {
        get => _selectedPurchase;
        private set => SetProperty(ref _selectedPurchase, value);
    }

    public int PurchasesCount { get; private set; }
    public decimal TotalPurchases { get; private set; }
    public string PurchasesCountDisplay => PurchasesCount.ToString();
    public string TotalPurchasesDisplay => $"Rs. {TotalPurchases:N0}";

    public ICommand NewPurchaseCommand { get; }
    public ICommand CloseNewPurchaseCommand { get; }
    public ICommand SelectPeriodCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ViewPurchaseCommand { get; }

    private void OpenNewPurchase()
    {
        CurrentNewPurchase = new NewPurchaseViewModel(
            _toastService,
            cancel: CloseNewPurchase,
            saved: _ =>
            {
                CloseNewPurchase();
                Refresh();
            });
        IsNewPurchaseActive = true;
    }

    private void CloseNewPurchase()
    {
        IsNewPurchaseActive = false;
        CurrentNewPurchase = null;
    }

    private void SelectPeriod(string period)
    {
        if (!string.IsNullOrWhiteSpace(period))
        {
            SelectedPeriod = period;
        }
    }

    private void SelectPurchase(PurchaseRecord purchase)
    {
        SelectedPurchase = purchase;
        _drawerService.Show(new PurchaseDetailViewModel(
            purchase,
            _drawerService,
            _dialogService,
            _toastService));
    }

    private void OnServiceStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var now = DateTime.Today;
        IEnumerable<PurchaseRecord> query = _service.Purchases;

        query = SelectedPeriod switch
        {
            "Today" => query.Where(p => p.Date.Date == now),
            "ThisWeek" => query.Where(p => p.Date.Date >= StartOfWeek(now)),
            "ThisMonth" => query.Where(p => p.Date.Year == now.Year && p.Date.Month == now.Month),
            _ => query
        };

        if (!string.Equals(SelectedSupplier, "All", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(p =>
                string.Equals(p.Supplier, SelectedSupplier, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(p =>
                p.PurchaseNumber.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.InvoiceNumber.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                p.Supplier.Contains(term, StringComparison.OrdinalIgnoreCase));
        }
        var results = query.OrderByDescending(p => p.Date).ToArray();
        FilteredPurchases.Clear();
        foreach (var purchase in results)
        {
            FilteredPurchases.Add(purchase);
        }

        PurchasesCount = results.Length;
        TotalPurchases = results.Sum(p => p.Total);
        OnPropertyChanged(nameof(PurchasesCount));
        OnPropertyChanged(nameof(TotalPurchases));
        OnPropertyChanged(nameof(PurchasesCountDisplay));
        OnPropertyChanged(nameof(TotalPurchasesDisplay));
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }
}
