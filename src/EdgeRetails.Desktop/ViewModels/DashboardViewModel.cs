using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class DashboardThakaItem
{
    public string ProjectName { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string BalanceFormatted { get; init; } = string.Empty;
    public string Subtitle { get; init; } = "balance";
}

public sealed class DashboardActivityItem
{
    public string Title { get; init; } = string.Empty;
    public string TimeFormatted { get; init; } = string.Empty;
    public string AmountFormatted { get; init; } = string.Empty;
    public Brush DotBrush { get; init; } = Brushes.Transparent;
}

public sealed class DashboardLowStockItem
{
    public string ProductName { get; init; } = string.Empty;
    public string StockBadgeText { get; init; } = string.Empty;
}

/// <summary>
/// Production Dashboard presentation state. Operational truth comes from
/// IBackendDashboardService; missing read authority is represented explicitly.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly INavigationService? _navigationService;
    private readonly IToastService? _toastService;
    private readonly IBackendDashboardService? _backendService;

    private string _userName = "User";
    private string _greeting = string.Empty;
    private string _subtitle = "Authoritative shop status and today's activity.";
    private string _currentDateFormatted = DateTime.Now.ToString("dddd, MMMM d, yyyy");
    private bool _isDatabaseConnected;
    private bool _isBackupUpToDate;
    private string _databaseStatusText = "Database status unavailable";
    private string _backupStatusText = "Backup status unavailable";
    private bool _isLoading;
    private string? _loadError;

    private string _todaySales = "Unavailable";
    private string _todayProfit = "Unavailable";
    private string _expenses = "Unavailable";
    private string _lowStockCount = "Unavailable";
    private string _lowStockSubtitle =
        "Minimum-stock authority is not exposed by the current backend read model.";

    private string _activeThakasCount = "Unavailable";
    private string _activeThakasSubtitle = "Authoritative active projects";
    private string _thakaValue = "Unavailable";
    private string _thakaValueSubtitle = "Outstanding balance of active projects";
    private string _todayThakaMaterial = "Unavailable";
    private string _todayThakaMaterialSubtitle = "Today's issued material less reversals";

    public DashboardViewModel()
        : this(null, null, null, null)
    {
    }

    public DashboardViewModel(
        ISessionContext? sessionContext = null,
        INavigationService? navigationService = null,
        IToastService? toastService = null,
        IBackendDashboardService? backendService = null)
    {
        _navigationService = navigationService;
        _toastService = toastService;
        _backendService = backendService;

        if (sessionContext is not null &&
            !string.IsNullOrWhiteSpace(sessionContext.DisplayName))
        {
            _userName = sessionContext.DisplayName;
        }

        _greeting = ComputeGreeting(_userName);

        ThakaProjects = [];
        RecentActivities = [];
        LowStockItems = [];

        NewThakaCommand = new RelayCommand(ExecuteNewThaka);
        ViewAllProjectsCommand = new RelayCommand(ExecuteViewAllProjects);
        OpenThakaItemCommand =
            new RelayCommand<DashboardThakaItem>(ExecuteOpenThakaItem);
        OpenActivityItemCommand =
            new RelayCommand<DashboardActivityItem>(ExecuteOpenActivityItem);
        OpenLowStockItemCommand =
            new RelayCommand<DashboardLowStockItem>(ExecuteOpenLowStockItem);
        ViewAllLowStockCommand = new RelayCommand(ExecuteViewAllLowStock);
        RefreshCommand = new RelayCommand(() => _ = RefreshBackendAsync(true));

        if (_backendService is null)
        {
            ApplyUnavailable(
                "Authoritative Dashboard backend service is not attached.");
        }
        else
        {
            _ = RefreshBackendAsync(false);
        }
    }

    public string UserName
    {
        get => _userName;
        set
        {
            if (SetProperty(ref _userName, value))
            {
                Greeting = ComputeGreeting(value);
            }
        }
    }

    public string Greeting
    {
        get => _greeting;
        private set => SetProperty(ref _greeting, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        private set => SetProperty(ref _subtitle, value);
    }

    public string CurrentDateFormatted
    {
        get => _currentDateFormatted;
        private set => SetProperty(ref _currentDateFormatted, value);
    }

    public bool IsDatabaseConnected
    {
        get => _isDatabaseConnected;
        private set => SetProperty(ref _isDatabaseConnected, value);
    }

    public string DatabaseStatusText
    {
        get => _databaseStatusText;
        private set => SetProperty(ref _databaseStatusText, value);
    }

    public bool IsBackupUpToDate
    {
        get => _isBackupUpToDate;
        private set => SetProperty(ref _isBackupUpToDate, value);
    }

    public string BackupStatusText
    {
        get => _backupStatusText;
        private set => SetProperty(ref _backupStatusText, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string? LoadError
    {
        get => _loadError;
        private set
        {
            if (SetProperty(ref _loadError, value))
            {
                OnPropertyChanged(nameof(HasLoadError));
            }
        }
    }

    public bool HasLoadError => !string.IsNullOrWhiteSpace(LoadError);

    public string TodaySales
    {
        get => _todaySales;
        private set => SetProperty(ref _todaySales, value);
    }

    public string TodayProfit
    {
        get => _todayProfit;
        private set => SetProperty(ref _todayProfit, value);
    }

    public string Expenses
    {
        get => _expenses;
        private set => SetProperty(ref _expenses, value);
    }

    public string LowStockCount
    {
        get => _lowStockCount;
        private set => SetProperty(ref _lowStockCount, value);
    }

    public string LowStockSubtitle
    {
        get => _lowStockSubtitle;
        private set => SetProperty(ref _lowStockSubtitle, value);
    }

    public string ActiveThakasCount
    {
        get => _activeThakasCount;
        private set => SetProperty(ref _activeThakasCount, value);
    }

    public string ActiveThakasSubtitle
    {
        get => _activeThakasSubtitle;
        private set => SetProperty(ref _activeThakasSubtitle, value);
    }

    public string ThakaValue
    {
        get => _thakaValue;
        private set => SetProperty(ref _thakaValue, value);
    }

    public string ThakaValueSubtitle
    {
        get => _thakaValueSubtitle;
        private set => SetProperty(ref _thakaValueSubtitle, value);
    }

    public string TodayThakaMaterial
    {
        get => _todayThakaMaterial;
        private set => SetProperty(ref _todayThakaMaterial, value);
    }

    public string TodayThakaMaterialSubtitle
    {
        get => _todayThakaMaterialSubtitle;
        private set => SetProperty(ref _todayThakaMaterialSubtitle, value);
    }

    // Compatibility aliases retained for older bindings/tests while the canonical
    // Dashboard now displays Today Thaka Material instead of a duplicate balance KPI.
    public string OutstandingBalance => TodayThakaMaterial;
    public string OutstandingBalanceSubtitle => TodayThakaMaterialSubtitle;

    public ObservableCollection<DashboardThakaItem> ThakaProjects { get; }
    public ObservableCollection<DashboardActivityItem> RecentActivities { get; }
    public ObservableCollection<DashboardLowStockItem> LowStockItems { get; }

    public ICommand NewThakaCommand { get; }
    public ICommand ViewAllProjectsCommand { get; }
    public ICommand OpenThakaItemCommand { get; }
    public ICommand OpenActivityItemCommand { get; }
    public ICommand OpenLowStockItemCommand { get; }
    public ICommand ViewAllLowStockCommand { get; }
    public ICommand RefreshCommand { get; }

    private void ExecuteNewThaka() =>
        _navigationService?.Navigate(NavigationTarget.ThakaProjects);

    private void ExecuteViewAllProjects() =>
        _navigationService?.Navigate(NavigationTarget.ThakaProjects);

    private void ExecuteOpenThakaItem(DashboardThakaItem? item)
    {
        if (item is not null)
        {
            _navigationService?.Navigate(NavigationTarget.ThakaProjects);
        }
    }

    private void ExecuteOpenActivityItem(DashboardActivityItem? item)
    {
        if (item is not null)
        {
            _toastService?.Show(
                "Unified authoritative recent-activity navigation is not attached yet.",
                ToastTone.Info);
        }
    }

    private void ExecuteOpenLowStockItem(DashboardLowStockItem? item)
    {
        if (item is not null)
        {
            _navigationService?.Navigate(NavigationTarget.Inventory);
        }
    }

    private void ExecuteViewAllLowStock() =>
        _navigationService?.Navigate(NavigationTarget.Inventory);

    private async Task RefreshBackendAsync(bool showSuccessToast)
    {
        if (_backendService is null)
        {
            ApplyUnavailable(
                "Authoritative Dashboard backend service is not attached.");
            return;
        }

        IsLoading = true;
        LoadError = null;
        CurrentDateFormatted = DateTime.Now.ToString("dddd, MMMM d, yyyy");
        Greeting = ComputeGreeting(UserName);

        try
        {
            var snapshot = await _backendService.LoadAsync();
            ApplySnapshot(snapshot);

            if (snapshot.Issues.Count > 0)
            {
                LoadError = string.Join(" ", snapshot.Issues);
                _toastService?.Show(
                    "Dashboard loaded with unavailable backend sections.",
                    ToastTone.Warning);
            }
            else if (showSuccessToast)
            {
                _toastService?.Show(
                    "Dashboard refreshed from authoritative backend reads.",
                    ToastTone.Success);
            }
        }
        catch (Exception ex)
        {
            ApplyUnavailable($"Dashboard backend unavailable: {ex.Message}");
            _toastService?.Show(LoadError!, ToastTone.Danger);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplySnapshot(BackendDashboardSnapshot snapshot)
    {
        TodaySales = CurrencyOrUnavailable(snapshot.TodaySales);
        TodayProfit = CurrencyOrUnavailable(snapshot.TodayProfit);
        Expenses = CurrencyOrUnavailable(snapshot.Expenses);

        // The current inventory overview read model does not expose
        // MinimumStockLevel. Showing a numeric low-stock KPI would therefore be fake.
        LowStockCount = "Unavailable";
        LowStockSubtitle =
            "Minimum-stock authority is not exposed by the current backend read model.";
        LowStockItems.Clear();

        ActiveThakasCount = snapshot.ActiveThakaCount?.ToString() ?? "Unavailable";
        ThakaValue = CurrencyOrUnavailable(snapshot.ActiveThakaOutstanding);
        TodayThakaMaterial = CurrencyOrUnavailable(snapshot.TodayThakaMaterial);

        ThakaProjects.Clear();
        foreach (var project in snapshot.ActiveProjects.Take(3))
        {
            ThakaProjects.Add(new DashboardThakaItem
            {
                ProjectName = project.ProjectName,
                CustomerName = project.CustomerName,
                BalanceFormatted = project.BalanceFormatted,
                Subtitle = "outstanding balance"
            });
        }

        // There is no unified authoritative recent-activity read contract in the
        // current backend composition. Empty is truthful; sample events are not.
        RecentActivities.Clear();

        IsDatabaseConnected = snapshot.IsDatabaseConnected == true;
        DatabaseStatusText = snapshot.DatabaseStatusText;
        IsBackupUpToDate = snapshot.IsBackupUpToDate == true;
        BackupStatusText = snapshot.BackupStatusText;

        Subtitle = snapshot.Issues.Count == 0
            ? "Authoritative shop status and today's activity."
            : "Some authoritative dashboard data is currently unavailable.";

        OnPropertyChanged(nameof(OutstandingBalance));
        OnPropertyChanged(nameof(OutstandingBalanceSubtitle));
    }

    private void ApplyUnavailable(string reason)
    {
        LoadError = reason;
        TodaySales = "Unavailable";
        TodayProfit = "Unavailable";
        Expenses = "Unavailable";
        LowStockCount = "Unavailable";
        ActiveThakasCount = "Unavailable";
        ThakaValue = "Unavailable";
        TodayThakaMaterial = "Unavailable";
        DatabaseStatusText = "Database status unavailable";
        BackupStatusText = "Backup status unavailable";
        IsDatabaseConnected = false;
        IsBackupUpToDate = false;
        ThakaProjects.Clear();
        RecentActivities.Clear();
        LowStockItems.Clear();
        Subtitle = "Authoritative dashboard data is currently unavailable.";

        OnPropertyChanged(nameof(OutstandingBalance));
        OnPropertyChanged(nameof(OutstandingBalanceSubtitle));
    }

    private static string CurrencyOrUnavailable(decimal? amount) =>
        amount is null ? "Unavailable" : $"Rs. {amount.Value:N0}";

    private static string ComputeGreeting(string name)
    {
        var hour = DateTime.Now.Hour;
        var prefix = hour switch
        {
            < 12 => "Good Morning",
            < 17 => "Good Afternoon",
            _ => "Good Evening"
        };

        return $"{prefix}, {name}";
    }
}
