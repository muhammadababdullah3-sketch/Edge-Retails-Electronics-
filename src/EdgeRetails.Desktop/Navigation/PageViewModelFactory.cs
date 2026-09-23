using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class PageViewModelFactory : IPageViewModelFactory, IDisposable
{
    private readonly PlaceholderPageViewModelFactory _placeholderFactory = new();
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private readonly IToastService _toastService;
    private readonly ITransactionService _transactionService;
    private readonly IBackendPurchasingInventoryService? _purchasingInventoryService;
    private readonly IBackendProductManagementService? _productManagementService;
    private readonly IBackendThakaService? _backendThakaService;
    private readonly IBackendBusinessOperationsService? _businessOperationsService;
    private readonly IBackendPhase5OperationsService? _phase5OperationsService;
    private readonly IBackendSalesHistoryService? _backendSalesHistoryService;
    private readonly IBackendPhase4WorkflowService? _phase4WorkflowService;
    private readonly IBackendDashboardService? _dashboardService;
    private readonly IBackendSettingsService? _settingsService;
    private readonly IPosCatalogGateway? _posCatalogGateway;
    private ISessionContext _sessionContext;
    private INavigationService? _navigationService;
    private ThakaProjectsViewModel? _thakaProjectsViewModel;
    private ThakaProjectListItemViewModel? _selectedThakaProject;
    private PurchaseHistoryViewModel? _purchaseHistoryViewModel;
    private InventoryViewModel? _inventoryViewModel;
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
        ISessionContext? sessionContext = null,
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? backendScopeFactory = null,
        IPosCatalogGateway? posCatalogGateway = null)
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
        _posCatalogGateway = posCatalogGateway;
        _transactionService = CreateTransactionService(backendScopeFactory);
        _purchasingInventoryService = backendScopeFactory is null
            ? null
            : new BackendPurchasingInventoryService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        _productManagementService = backendScopeFactory is null
            ? null
            : new BackendProductManagementService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        _backendThakaService = backendScopeFactory is null
            ? null
            : new BackendThakaService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        _businessOperationsService = backendScopeFactory is null
            ? null
            : new BackendBusinessOperationsService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        _phase5OperationsService = backendScopeFactory is null
            ? null
            : new BackendPhase5OperationsService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        _backendSalesHistoryService = backendScopeFactory is null
            ? null
            : new BackendSalesHistoryService(backendScopeFactory);
        _phase4WorkflowService = backendScopeFactory is null
            ? null
            : new BackendPhase4WorkflowService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        _dashboardService = backendScopeFactory is null
            ? null
            : new BackendDashboardService(
                _businessOperationsService!,
                _backendThakaService!,
                backendScopeFactory);
        _settingsService = backendScopeFactory is null
            ? null
            : new BackendSettingsService(
                backendScopeFactory,
                () => _sessionContext.UserId);
    }

    public void SetNavigationService(INavigationService navigationService)
    {
        _navigationService = navigationService;
    }

    public void SetSessionContext(ISessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        ResetCachedPages();
        _sessionContext = sessionContext;
    }

    public ViewModelBase Create(NavigationTarget target)
    {
        return target switch
        {
            NavigationTarget.Dashboard => new DashboardViewModel(
                _sessionContext,
                _navigationService,
                _toastService,
                _dashboardService),

            NavigationTarget.POS => new PosViewModel(
                _toastService,
                _dialogService,
                _transactionService,
                _sessionContext,
                _posCatalogGateway,
                _businessOperationsService,
                _phase4WorkflowService),

            NavigationTarget.SalesHistory => new SalesHistoryViewModel(
                _sessionContext,
                _navigationService,
                _toastService,
                _drawerService,
                _dialogService,
                _transactionService,
                _backendSalesHistoryService),

            NavigationTarget.ThakaProjects => GetOrCreateThakaProjects(),
            NavigationTarget.ThakaWorkspace => CreateThakaWorkspace(),
            NavigationTarget.Purchases => _purchaseHistoryViewModel ??=
                new PurchaseHistoryViewModel(
                    _toastService,
                    _drawerService,
                    _dialogService,
                    _purchasingInventoryService,
                    _phase4WorkflowService),
            NavigationTarget.ProductManagement => new ProductManagementViewModel(
                _toastService,
                _dialogService,
                _productManagementService,
                _purchasingInventoryService),
            NavigationTarget.Inventory => _inventoryViewModel ??=
                new InventoryViewModel(
                    _toastService,
                    _dialogService,
                    _purchasingInventoryService,
                    _productManagementService,
                    _phase4WorkflowService),
            NavigationTarget.Expenses => _expensesViewModel ??= new ExpensesViewModel(
                _toastService,
                _dialogService,
                _businessOperationsService),
            NavigationTarget.Customers => _customersViewModel ??= new CustomersViewModel(
                _toastService,
                _dialogService,
                _drawerService,
                _businessOperationsService),
            NavigationTarget.Suppliers => _suppliersViewModel ??= new SuppliersViewModel(
                _toastService,
                _dialogService,
                _drawerService,
                _businessOperationsService,
                _phase5OperationsService),
            NavigationTarget.Warranty => new WarrantyViewModel(
                _phase5OperationsService,
                _toastService),
            NavigationTarget.Reports => _reportsViewModel ??= new ReportsViewModel(
                _businessOperationsService,
                _toastService),
            NavigationTarget.Settings => _settingsViewModel ??= new SettingsViewModel(
                _themeService,
                _dialogService,
                _toastService,
                _settingsService),

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

    public void Dispose()
    {
        ResetCachedPages();
    }

    public void ClearCachedPages()
    {
        ResetCachedPages();
    }

    private void ResetCachedPages()
    {
        if (_thakaProjectsViewModel is not null)
        {
            _thakaProjectsViewModel.WorkspaceOpened -= OnWorkspaceOpened;
            _thakaProjectsViewModel.Dispose();
        }

        _purchaseHistoryViewModel?.Dispose();
        _inventoryViewModel?.Dispose();
        _expensesViewModel?.Dispose();
        _customersViewModel?.Dispose();
        _suppliersViewModel?.Dispose();
        _reportsViewModel?.Dispose();
        _settingsViewModel?.Dispose();

        _thakaProjectsViewModel = null;
        _selectedThakaProject = null;
        _purchaseHistoryViewModel = null;
        _inventoryViewModel = null;
        _expensesViewModel = null;
        _customersViewModel = null;
        _suppliersViewModel = null;
        _reportsViewModel = null;
        _settingsViewModel = null;
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
            _drawerService,
            _backendThakaService);

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
            _toastService,
            _backendThakaService,
            _phase4WorkflowService);

        workspace.BackRequested += (_, _) =>
            _navigationService?.Navigate(NavigationTarget.ThakaProjects);

        return workspace;
    }

    private void OnWorkspaceOpened(object? sender, ThakaProjectListItemViewModel project)
    {
        _selectedThakaProject = project;
        _navigationService?.Navigate(NavigationTarget.ThakaWorkspace);
    }

    private ITransactionService CreateTransactionService(
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? backendScopeFactory)
    {
        if (backendScopeFactory is not null)
        {
            return new BackendTransactionService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        }

#if DEBUG
        return DemoTransactionService.Instance;
#else
        throw new InvalidOperationException(
            "Production runtime requires a registered backend scope factory for transactions.");
#endif
    }
}
