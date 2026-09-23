using System.Collections.ObjectModel;
using System.Windows.Input;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PurchaseHistoryViewModel : ViewModelBase, IDisposable
{
    private readonly DemoPurchaseInventoryService _service;
    private readonly IBackendPurchasingInventoryService? _backendService;
    private readonly IBackendPhase4WorkflowService? _phase4Service;
    private readonly List<PurchaseRecord> _backendPurchases = [];
    private bool _backendLoaded;
    private bool _backendLoading;
    private CancellationTokenSource? _backendRefreshCancellation;
    private long _backendRefreshVersion;
    private readonly Dictionary<string, Guid> _supplierIds = new(StringComparer.OrdinalIgnoreCase);
    private const int BackendPageSize = 200;
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
        IDialogService dialogService,
        IBackendPurchasingInventoryService? backendService = null,
        IBackendPhase4WorkflowService? phase4Service = null)
    {
        _toastService = toastService;
        _drawerService = drawerService;
        _dialogService = dialogService;
        _service = DemoPurchaseInventoryService.Instance;
        _backendService = backendService;
        _phase4Service = phase4Service;

        FilteredPurchases = [];
        Suppliers = backendService is null
            ? new ObservableCollection<string>(["All", .. _service.Suppliers])
            : new ObservableCollection<string>(["All"]);

        NewPurchaseCommand = new RelayCommand(OpenNewPurchase);
        CloseNewPurchaseCommand = new RelayCommand(CloseNewPurchase);
        SelectPeriodCommand = new RelayCommand<string>(SelectPeriod);
        RefreshCommand = new RelayCommand(() =>
        {
            if (_backendService is null)
            {
                Refresh();
            }
            else
            {
                _ = RefreshBackendAsync();
            }
        });
        ViewPurchaseCommand = new RelayCommand<PurchaseRecord>(SelectPurchase);

        if (_backendService is null)
        {
            _service.StateChanged += OnServiceStateChanged;
        }

        Refresh();
    }

    public void Dispose()
    {
        if (_backendService is null)
        {
            _service.StateChanged -= OnServiceStateChanged;
        }

        _backendRefreshCancellation?.Cancel();
        _backendRefreshCancellation?.Dispose();
    }

    public ObservableCollection<PurchaseRecord> FilteredPurchases { get; }
    public ObservableCollection<string> Suppliers { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                if (_backendService is null)
                {
                    Refresh();
                }
                else
                {
                    ScheduleBackendRefresh();
                }
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
                if (_backendService is null)
                {
                    Refresh();
                }
                else
                {
                    ScheduleBackendRefresh();
                }
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
                if (_backendService is null)
                {
                    Refresh();
                }
                else
                {
                    ScheduleBackendRefresh();
                }
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
            saved: savedPurchase =>
            {
                CloseNewPurchase();
                if (_backendService is null)
                {
                    Refresh();
                }
                else
                {
                    _ = RefreshBackendAsync();
                }
            },
            backendService: _backendService,
            dialogService: _dialogService);
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
            _toastService,
            _backendService,
            _phase4Service));
    }

    private void OnServiceStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_backendService is not null)
        {
            if (!_backendLoaded && !_backendLoading)
            {
                _ = RefreshBackendAsync();
                return;
            }

            ApplyFilters(_backendPurchases);
            return;
        }

        ApplyFilters(_service.Purchases);
    }

    private void ScheduleBackendRefresh()
    {
        if (_backendService is null)
        {
            Refresh();
            return;
        }

        _ = RefreshBackendAsync();
    }

    private async Task RefreshBackendAsync()
    {
        if (_backendService is null)
        {
            return;
        }

        var version = Interlocked.Increment(ref _backendRefreshVersion);
        var previous = Interlocked.Exchange(ref _backendRefreshCancellation, new CancellationTokenSource());
        previous?.Cancel();
        previous?.Dispose();
        var cts = _backendRefreshCancellation!;

        try
        {
            await Task.Delay(250, cts.Token);
            if (version != Volatile.Read(ref _backendRefreshVersion))
            {
                return;
            }

            _backendLoading = true;
            var selectedSupplier = SelectedSupplier;
            Guid? supplierId = null;
            if (!string.Equals(selectedSupplier, "All", StringComparison.OrdinalIgnoreCase) &&
                _supplierIds.TryGetValue(selectedSupplier, out var resolvedSupplierId))
            {
                supplierId = resolvedSupplierId;
            }

            var (fromDate, toDate) = ResolvePeriod(DateTime.Today);
            var purchasesTask = _backendService.GetPurchasesAsync(
                SearchText,
                fromDate,
                toDate,
                supplierId,
                cts.Token);
            var suppliersTask = _backendService.GetSuppliersAsync(cts.Token);
            await Task.WhenAll(purchasesTask, suppliersTask);

            if (cts.IsCancellationRequested || version != Volatile.Read(ref _backendRefreshVersion))
            {
                return;
            }

            var suppliers = await suppliersTask;
            _supplierIds.Clear();
            Suppliers.Clear();
            Suppliers.Add("All");
            foreach (var supplier in suppliers
                .Where(x => x.Id != Guid.Empty)
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
            {
                Suppliers.Add(supplier.Name);
                _supplierIds[supplier.Name] = supplier.Id;
            }

            _backendPurchases.Clear();
            _backendPurchases.AddRange(await purchasesTask);
            _backendLoaded = true;
            ApplyFilters(_backendPurchases, preserveServerFilter: true);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (version == Volatile.Read(ref _backendRefreshVersion))
        {
            _toastService.Show(
                $"Purchases could not be refreshed: {ex.Message}",
                ToastTone.Danger);
        }
        finally
        {
            if (version == Volatile.Read(ref _backendRefreshVersion))
            {
                _backendLoading = false;
            }
        }
    }

    private void ApplyFilters(
        IEnumerable<PurchaseRecord> source,
        bool preserveServerFilter = false)
    {
        var now = DateTime.Today;
        var query = source;
        if (preserveServerFilter)
        {
            var results = query.ToArray();
            ApplyResults(results);
            return;
        }

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

        ApplyResults(query.OrderByDescending(p => p.Date).ToArray());
    }

    private void ApplyResults(IReadOnlyCollection<PurchaseRecord> results)
    {
        FilteredPurchases.Clear();
        foreach (var purchase in results)
        {
            FilteredPurchases.Add(purchase);
        }

        PurchasesCount = results.Count;
        TotalPurchases = results.Where(p => !p.IsVoided).Sum(p => p.Total);
        OnPropertyChanged(nameof(PurchasesCount));
        OnPropertyChanged(nameof(TotalPurchases));
        OnPropertyChanged(nameof(PurchasesCountDisplay));
        OnPropertyChanged(nameof(TotalPurchasesDisplay));
    }

    private (DateOnly? FromDate, DateOnly? ToDate) ResolvePeriod(DateTime today)
    {
        var date = DateOnly.FromDateTime(today);
        return SelectedPeriod switch
        {
            "Today" => (date, date),
            "ThisWeek" => (DateOnly.FromDateTime(StartOfWeek(today)), date),
            "ThisMonth" => (new DateOnly(date.Year, date.Month, 1), date),
            _ => (null, null)
        };
    }

    private static DateTime StartOfWeek(DateTime date)
    {
        var diff = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7;
        return date.AddDays(-diff).Date;
    }
}
