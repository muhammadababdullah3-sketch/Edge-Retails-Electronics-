using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class PageViewModelFactory : IPageViewModelFactory
{
    private readonly PlaceholderPageViewModelFactory _placeholderFactory = new();
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private readonly IToastService _toastService;
    private readonly ITransactionService _transactionService = DemoTransactionService.Instance;
    private ISessionContext _sessionContext;
    private INavigationService? _navigationService;
    private ThakaProjectsViewModel? _thakaProjectsViewModel;
    private ThakaProjectListItemViewModel? _selectedThakaProject;
    private ExpensesViewModel? _expensesViewModel;
    private CustomersViewModel? _customersViewModel;
    private SuppliersViewModel? _suppliersViewModel;
    private ReportsViewModel? _reportsViewModel;
    private SettingsViewModel? _settingsViewModel;

    public PageViewModelFactory(
        IThemeService themeService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IToastService toastService,
        ISessionContext? sessionContext = null)
    {
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(drawerService);
        ArgumentNullException.ThrowIfNull(toastService);

        _themeService = themeService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _toastService = toastService;
        _sessionContext = sessionContext ?? new DesignPreviewSessionContext();
    }

    public void SetNavigationService(INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    public void SetSessionContext(ISessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        _sessionContext = sessionContext;
        _thakaProjectsViewModel = null;
        _selectedThakaProject = null;
    }

    public ViewModelBase Create(NavigationTarget target)
    {
        return target switch
        {
            NavigationTarget.Dashboard => new DashboardViewModel(
                _sessionContext,
                _navigationService,
                _toastService),

            NavigationTarget.NewSale => new NewSaleViewModel(
                _toastService,
                _dialogService,
                _transactionService,
                _sessionContext),

            NavigationTarget.SalesHistory => new SalesHistoryViewModel(
                _sessionContext,
                _navigationService,
                _toastService,
                _drawerService,
                _dialogService,
                _transactionService),

            NavigationTarget.ThakaProjects => GetOrCreateThakaProjects(),
            NavigationTarget.ThakaWorkspace => CreateThakaWorkspace(),
            NavigationTarget.Purchases => new PurchaseHistoryViewModel(
                _toastService,
                _drawerService,
                _dialogService),
            NavigationTarget.Inventory => new InventoryViewModel(
                _toastService,
                _dialogService),
            NavigationTarget.Expenses => _expensesViewModel ??= new ExpensesViewModel(
                _toastService,
                _dialogService),
            NavigationTarget.Customers => _customersViewModel ??= new CustomersViewModel(
                _toastService,
                _dialogService,
                _drawerService),
            NavigationTarget.Suppliers => _suppliersViewModel ??= new SuppliersViewModel(
                _toastService,
                _dialogService,
                _drawerService),
            NavigationTarget.Reports => _reportsViewModel ??= new ReportsViewModel(),
            NavigationTarget.Settings => _settingsViewModel ??= new SettingsViewModel(
                _themeService,
                _dialogService,
                _toastService),

#if DEBUG
            NavigationTarget.Sprint1Verification => new Sprint1VerificationViewModel(
                _themeService,
                _dialogService,
                _drawerService,
                _toastService),
#endif

            _ => _placeholderFactory.Create(target)
        };
    }

    private ThakaProjectsViewModel GetOrCreateThakaProjects()
    {
        if (_thakaProjectsViewModel is not null)
        {
            return _thakaProjectsViewModel;
        }

        _thakaProjectsViewModel = new ThakaProjectsViewModel(
            _sessionContext,
            _navigationService,
            _toastService,
            _dialogService,
            _drawerService);

        _thakaProjectsViewModel.WorkspaceOpened += OnWorkspaceOpened;
        return _thakaProjectsViewModel;
    }

    private ThakaWorkspaceViewModel CreateThakaWorkspace()
    {
        var projects = GetOrCreateThakaProjects();
        _selectedThakaProject ??= projects.AllProjects.FirstOrDefault(p => p.IsActive)
            ?? projects.AllProjects.First();

        var workspace = new ThakaWorkspaceViewModel(
            _selectedThakaProject,
            _dialogService,
            _toastService);

        workspace.BackRequested += (_, _) =>
            _navigationService?.Navigate(NavigationTarget.ThakaProjects);

        return workspace;
    }

    private void OnWorkspaceOpened(object? sender, ThakaProjectListItemViewModel project)
    {
        _selectedThakaProject = project;
        _navigationService?.Navigate(NavigationTarget.ThakaWorkspace);
    }
}
