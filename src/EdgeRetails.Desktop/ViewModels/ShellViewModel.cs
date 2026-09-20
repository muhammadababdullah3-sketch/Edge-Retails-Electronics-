using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class ShellViewModel : ViewModelBase, IDisposable
{
    private const double ExpandedSidebarWidth = 232;
    private const double CollapsedSidebarWidth = 72;

    private readonly INavigationService _navigationService;
    private readonly ILiveClock _clock;
    private bool _isSidebarCollapsed;
    private ViewModelBase? _currentPage;
    private string _pageTitle = "Dashboard";
    private string _dateText = string.Empty;
    private string _timeText = string.Empty;

    public ShellViewModel(
        INavigationService navigationService,
        ILiveClock clock,
        ISessionContext sessionContext,
        IThemeService themeService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IToastService toastService)
    {
        ArgumentNullException.ThrowIfNull(navigationService);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(drawerService);
        ArgumentNullException.ThrowIfNull(toastService);

        _navigationService = navigationService;
        _clock = clock;

        UserName = sessionContext.DisplayName;
        UserRole = sessionContext.RoleName;
        UserInitials = sessionContext.Initials;
        IsOnline = sessionContext.IsOnline;

        ThemeService = themeService;
        DialogService = dialogService;
        DrawerService = drawerService;
        ToastService = toastService;

        var navigationItems = new List<NavigationItemViewModel>
        {
            CreateNavigationItem("Dashboard", NavigationTarget.Dashboard, "Icon.Nav.Dashboard"),
            CreateNavigationItem("New Sale", NavigationTarget.NewSale, "Icon.Nav.NewSale"),
            CreateNavigationItem("Sales History", NavigationTarget.SalesHistory, "Icon.Nav.SalesHistory"),
            CreateNavigationItem("Thaka / Projects", NavigationTarget.ThakaProjects, "Icon.Nav.ThakaProjects"),
            CreateNavigationItem("Purchases", NavigationTarget.Purchases, "Icon.Nav.Purchases"),
            CreateNavigationItem("Inventory", NavigationTarget.Inventory, "Icon.Nav.Inventory"),
            CreateNavigationItem("Expenses", NavigationTarget.Expenses, "Icon.Nav.Expenses"),
            CreateNavigationItem("Customers", NavigationTarget.Customers, "Icon.Nav.Customers"),
            CreateNavigationItem("Suppliers", NavigationTarget.Suppliers, "Icon.Nav.Suppliers"),
            CreateNavigationItem("Reports", NavigationTarget.Reports, "Icon.Nav.Reports"),
            CreateNavigationItem("Settings", NavigationTarget.Settings, "Icon.Nav.Settings"),
        };

#if DEBUG
        navigationItems.Add(
            CreateNavigationItem(
                "Sprint 1 QA",
                NavigationTarget.Sprint1Verification,
                "Icon.Nav.Settings"));
#endif

        NavigationItems =
            new ReadOnlyCollection<NavigationItemViewModel>(navigationItems);

        NavigateCommand = new RelayCommand<NavigationTarget>(Navigate);
        ToggleSidebarCommand = new RelayCommand(ToggleSidebar);

        _navigationService.Navigated += OnNavigated;
        _clock.Tick += OnClockTick;

        UpdateClock(_clock.Now);
    }

    public IReadOnlyList<NavigationItemViewModel> NavigationItems { get; }

    public ICommand NavigateCommand { get; }

    public ICommand ToggleSidebarCommand { get; }

    public IThemeService ThemeService { get; }

    public IDialogService DialogService { get; }

    public IDrawerService DrawerService { get; }

    public IToastService ToastService { get; }

    public string UserName { get; }

    public string UserRole { get; }

    public string UserInitials { get; }

    public bool IsOnline { get; }

    public ViewModelBase? CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public string DateText
    {
        get => _dateText;
        private set => SetProperty(ref _dateText, value);
    }

    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    public bool IsSidebarCollapsed
    {
        get => _isSidebarCollapsed;
        private set
        {
            if (SetProperty(ref _isSidebarCollapsed, value))
            {
                OnPropertyChanged(nameof(SidebarWidth));
            }
        }
    }

    public double SidebarWidth =>
        IsSidebarCollapsed ? CollapsedSidebarWidth : ExpandedSidebarWidth;

    public void Dispose()
    {
        _navigationService.Navigated -= OnNavigated;
        _clock.Tick -= OnClockTick;
    }

    private static NavigationItemViewModel CreateNavigationItem(
        string title,
        NavigationTarget target,
        string iconResourceKey)
    {
        var icon = Application.Current.TryFindResource(iconResourceKey) as Geometry
            ?? throw new InvalidOperationException(
                $"Navigation icon resource '{iconResourceKey}' was not found.");

        return new NavigationItemViewModel(title, target, icon);
    }

    private void Navigate(NavigationTarget target)
    {
        _navigationService.Navigate(target);
    }

    private void ToggleSidebar()
    {
        IsSidebarCollapsed = !IsSidebarCollapsed;
    }

    private void OnNavigated(
        object? sender,
        NavigationChangedEventArgs e)
    {
        CurrentPage = e.ViewModel;
        var navigationItem = NavigationItems.FirstOrDefault(item => item.Target == e.Target);
        PageTitle = navigationItem?.Title ?? e.Target switch
        {
            NavigationTarget.ThakaWorkspace => "Thaka Workspace",
            _ => e.Target.ToString()
        };

        foreach (var item in NavigationItems)
        {
            item.IsSelected = item.Target == e.Target;
        }
    }

    private void OnClockTick(object? sender, DateTimeOffset now)
    {
        UpdateClock(now);
    }

    private void UpdateClock(DateTimeOffset now)
    {
        var culture = CultureInfo.GetCultureInfo("en-GB");
        DateText = now.ToString("dd MMM yyyy", culture);
        TimeText = now.ToString("hh:mm tt", culture);
    }
}

