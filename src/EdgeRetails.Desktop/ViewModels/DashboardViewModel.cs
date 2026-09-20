using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Represents a Thaka / Project summary item on the dashboard.
/// </summary>
public sealed class DashboardThakaItem
{
    public string ProjectName { get; init; } = string.Empty;
    public string CustomerName { get; init; } = string.Empty;
    public string BalanceFormatted { get; init; } = string.Empty;
    public string Subtitle { get; init; } = "balance";
}

/// <summary>
/// Represents a recent operational activity item (Sale, Thaka, Expense, Purchase) on the dashboard.
/// </summary>
public sealed class DashboardActivityItem
{
    public string Title { get; init; } = string.Empty;
    public string TimeFormatted { get; init; } = string.Empty;
    public string AmountFormatted { get; init; } = string.Empty;
    public Brush DotBrush { get; init; } = Brushes.Transparent;
}

/// <summary>
/// Represents a low-stock inventory alert item on the dashboard.
/// </summary>
public sealed class DashboardLowStockItem
{
    public string ProductName { get; init; } = string.Empty;
    public string StockBadgeText { get; init; } = string.Empty;
}

/// <summary>
/// ViewModel for the main Edge Retails POS Dashboard screen.
/// Implements MVVM pattern with support for live navigation, toast alerts, and Figma-exact demo data.
/// </summary>
public sealed class DashboardViewModel : ViewModelBase
{
    private readonly INavigationService? _navigationService;
    private readonly IToastService? _toastService;

    private string _userName = "Abdullah";
    private string _greeting = "Good Evening, Abdullah";
    private string _subtitle = "Here's what's happening in your shop today.";
    private string _currentDateFormatted = DateTime.Now.ToString("dddd, MMMM d, yyyy");
    private bool _isDatabaseConnected = true;
    private bool _isBackupUpToDate = true;

    // KPI row 1
    private string _todaySales = "Rs. 84,500";
    private string _todayProfit = "Rs. 13,400";
    private string _expenses = "Rs. 3,200";
    private string _lowStockCount = "12 Items";
    private string _lowStockSubtitle = "products below minimum";

    // KPI row 2 (Thaka / Projects)
    private string _activeThakasCount = "3";
    private string _activeThakasSubtitle = "ongoing projects";
    private string _thakaValue = "Rs. 687,400";
    private string _thakaValueSubtitle = "total material issued";
    private string _outstandingBalance = "Rs. 587,400";
    private string _outstandingBalanceSubtitle = "unpaid balance";

    public DashboardViewModel()
        : this(null, null, null)
    {
    }

    public DashboardViewModel(
        ISessionContext? sessionContext = null,
        INavigationService? navigationService = null,
        IToastService? toastService = null)
    {
        _navigationService = navigationService;
        _toastService = toastService;

        if (sessionContext != null && !string.IsNullOrWhiteSpace(sessionContext.DisplayName))
        {
            _userName = sessionContext.DisplayName;
            _greeting = ComputeGreeting(_userName);
        }
        else
        {
            _greeting = ComputeGreeting(_userName);
        }

        ThakaProjects = new ObservableCollection<DashboardThakaItem>();
        RecentActivities = new ObservableCollection<DashboardActivityItem>();
        LowStockItems = new ObservableCollection<DashboardLowStockItem>();

        NewThakaCommand = new RelayCommand(ExecuteNewThaka);
        ViewAllProjectsCommand = new RelayCommand(ExecuteViewAllProjects);
        OpenThakaItemCommand = new RelayCommand<DashboardThakaItem>(ExecuteOpenThakaItem);
        OpenActivityItemCommand = new RelayCommand<DashboardActivityItem>(ExecuteOpenActivityItem);
        OpenLowStockItemCommand = new RelayCommand<DashboardLowStockItem>(ExecuteOpenLowStockItem);
        ViewAllLowStockCommand = new RelayCommand(ExecuteViewAllLowStock);
        RefreshCommand = new RelayCommand(ExecuteRefresh);

        LoadDemoData();
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
        set => SetProperty(ref _greeting, value);
    }

    public string Subtitle
    {
        get => _subtitle;
        set => SetProperty(ref _subtitle, value);
    }

    public string CurrentDateFormatted
    {
        get => _currentDateFormatted;
        set => SetProperty(ref _currentDateFormatted, value);
    }

    public bool IsDatabaseConnected
    {
        get => _isDatabaseConnected;
        set
        {
            if (SetProperty(ref _isDatabaseConnected, value))
            {
                OnPropertyChanged(nameof(DatabaseStatusText));
            }
        }
    }

    public string DatabaseStatusText => IsDatabaseConnected ? "Database Connected" : "Database Disconnected";

    public bool IsBackupUpToDate
    {
        get => _isBackupUpToDate;
        set
        {
            if (SetProperty(ref _isBackupUpToDate, value))
            {
                OnPropertyChanged(nameof(BackupStatusText));
            }
        }
    }

    public string BackupStatusText => IsBackupUpToDate ? "Backup Up to Date" : "Backup Pending";

    // KPI Row 1 Properties
    public string TodaySales
    {
        get => _todaySales;
        set => SetProperty(ref _todaySales, value);
    }

    public string TodayProfit
    {
        get => _todayProfit;
        set => SetProperty(ref _todayProfit, value);
    }

    public string Expenses
    {
        get => _expenses;
        set => SetProperty(ref _expenses, value);
    }

    public string LowStockCount
    {
        get => _lowStockCount;
        set => SetProperty(ref _lowStockCount, value);
    }

    public string LowStockSubtitle
    {
        get => _lowStockSubtitle;
        set => SetProperty(ref _lowStockSubtitle, value);
    }

    // KPI Row 2 Properties
    public string ActiveThakasCount
    {
        get => _activeThakasCount;
        set => SetProperty(ref _activeThakasCount, value);
    }

    public string ActiveThakasSubtitle
    {
        get => _activeThakasSubtitle;
        set => SetProperty(ref _activeThakasSubtitle, value);
    }

    public string ThakaValue
    {
        get => _thakaValue;
        set => SetProperty(ref _thakaValue, value);
    }

    public string ThakaValueSubtitle
    {
        get => _thakaValueSubtitle;
        set => SetProperty(ref _thakaValueSubtitle, value);
    }

    public string OutstandingBalance
    {
        get => _outstandingBalance;
        set => SetProperty(ref _outstandingBalance, value);
    }

    public string OutstandingBalanceSubtitle
    {
        get => _outstandingBalanceSubtitle;
        set => SetProperty(ref _outstandingBalanceSubtitle, value);
    }

    // Collections
    public ObservableCollection<DashboardThakaItem> ThakaProjects { get; }
    public ObservableCollection<DashboardActivityItem> RecentActivities { get; }
    public ObservableCollection<DashboardLowStockItem> LowStockItems { get; }

    // Commands
    public ICommand NewThakaCommand { get; }
    public ICommand ViewAllProjectsCommand { get; }
    public ICommand OpenThakaItemCommand { get; }
    public ICommand OpenActivityItemCommand { get; }
    public ICommand OpenLowStockItemCommand { get; }
    public ICommand ViewAllLowStockCommand { get; }
    public ICommand RefreshCommand { get; }

    private void ExecuteNewThaka()
    {
        _toastService?.Show("Creating a new Thaka project...", ToastTone.Info);
        _navigationService?.Navigate(NavigationTarget.ThakaProjects);
    }

    private void ExecuteViewAllProjects()
    {
        _navigationService?.Navigate(NavigationTarget.ThakaProjects);
    }

    private void ExecuteOpenThakaItem(DashboardThakaItem? item)
    {
        if (item is null)
        {
            return;
        }

        _toastService?.Show($"Viewing {item.ProjectName} for {item.CustomerName}.", ToastTone.Info);
        _navigationService?.Navigate(NavigationTarget.ThakaProjects);
    }

    private void ExecuteOpenActivityItem(DashboardActivityItem? item)
    {
        if (item is null)
        {
            return;
        }

        _toastService?.Show($"{item.Title} ({item.TimeFormatted}) — {item.AmountFormatted}", ToastTone.Info);
    }

    private void ExecuteOpenLowStockItem(DashboardLowStockItem? item)
    {
        if (item is null)
        {
            return;
        }

        _toastService?.Show($"{item.ProductName} is low in stock ({item.StockBadgeText}).", ToastTone.Warning);
        _navigationService?.Navigate(NavigationTarget.Inventory);
    }

    private void ExecuteViewAllLowStock()
    {
        _navigationService?.Navigate(NavigationTarget.Inventory);
    }

    private void ExecuteRefresh()
    {
        CurrentDateFormatted = DateTime.Now.ToString("dddd, MMMM d, yyyy");
        Greeting = ComputeGreeting(UserName);
        LoadDemoData();
        _toastService?.Show("Dashboard metrics updated.", ToastTone.Success);
    }

    private void LoadDemoData()
    {
        // 1. Thaka / Projects (matching Figma forensic spec)
        ThakaProjects.Clear();
        ThakaProjects.Add(new DashboardThakaItem
        {
            ProjectName = "Ahmed House",
            CustomerName = "Ahmed",
            BalanceFormatted = "Rs. 135,000",
            Subtitle = "balance"
        });
        ThakaProjects.Add(new DashboardThakaItem
        {
            ProjectName = "Ali Plaza",
            CustomerName = "Ali",
            BalanceFormatted = "Rs. 370,000",
            Subtitle = "balance"
        });
        ThakaProjects.Add(new DashboardThakaItem
        {
            ProjectName = "Usman House",
            CustomerName = "Usman",
            BalanceFormatted = "Rs. 82,400",
            Subtitle = "balance"
        });

        // 2. Recent Activity (matching Figma forensic spec)
        RecentActivities.Clear();
        RecentActivities.Add(new DashboardActivityItem
        {
            Title = "Sale #1288",
            TimeFormatted = "04:20 PM",
            AmountFormatted = "Rs. 4,200",
            DotBrush = CreateFrozenBrush(Color.FromRgb(0x4F, 0x46, 0xE5)) // Indigo
        });
        RecentActivities.Add(new DashboardActivityItem
        {
            Title = "Thaka #45",
            TimeFormatted = "03:55 PM",
            AmountFormatted = "Rs. 8,700",
            DotBrush = CreateFrozenBrush(Color.FromRgb(0x7C, 0x3A, 0xED)) // Purple
        });
        RecentActivities.Add(new DashboardActivityItem
        {
            Title = "Expense – Lunch",
            TimeFormatted = "01:30 PM",
            AmountFormatted = "Rs. 850",
            DotBrush = CreateFrozenBrush(Color.FromRgb(0xEA, 0x58, 0x0C)) // Orange
        });
        RecentActivities.Add(new DashboardActivityItem
        {
            Title = "Purchase #252",
            TimeFormatted = "11:10 AM",
            AmountFormatted = "Rs. 72,000",
            DotBrush = CreateFrozenBrush(Color.FromRgb(0x25, 0x63, 0xEB)) // Blue
        });
        RecentActivities.Add(new DashboardActivityItem
        {
            Title = "Sale #1287",
            TimeFormatted = "10:45 AM",
            AmountFormatted = "Rs. 1,500",
            DotBrush = CreateFrozenBrush(Color.FromRgb(0x4F, 0x46, 0xE5)) // Indigo
        });

        // 3. Low Stock (matching Figma forensic spec)
        LowStockItems.Clear();
        LowStockItems.Add(new DashboardLowStockItem
        {
            ProductName = "LED Bulb 12W",
            StockBadgeText = "8 left"
        });
        LowStockItems.Add(new DashboardLowStockItem
        {
            ProductName = "Wire 2.5mm",
            StockBadgeText = "2 rolls"
        });
        LowStockItems.Add(new DashboardLowStockItem
        {
            ProductName = "Breaker 32A",
            StockBadgeText = "4 left"
        });
        LowStockItems.Add(new DashboardLowStockItem
        {
            ProductName = "Switch 16A",
            StockBadgeText = "7 left"
        });
        LowStockItems.Add(new DashboardLowStockItem
        {
            ProductName = "Socket 16A",
            StockBadgeText = "3 left"
        });
    }

    private static SolidColorBrush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

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
