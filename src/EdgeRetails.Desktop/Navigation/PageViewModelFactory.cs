using System.IO;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;
using EdgeRetails.Application.Production.Printing;
using EdgeRetails.Infrastructure.Production.Printing;

namespace EdgeRetails.Desktop.Navigation;

public sealed class PageViewModelFactory : IPageViewModelFactory, IDisposable
{
    private readonly PlaceholderPageViewModelFactory _placeholderFactory = new();
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private readonly IToastService _toastService;
    private readonly IClientOperationIntentStore _operationIntents;
    private readonly ITransactionService _transactionService;
    private readonly IBackendPurchasingInventoryService? _purchasingInventoryService;
    private readonly IBackendStockAdjustmentService? _stockAdjustmentService;
    private readonly IBackendProductManagementService? _productManagementService;
    private readonly IBackendThakaService? _backendThakaService;
    private readonly IBackendBusinessOperationsService? _businessOperationsService;
    private readonly IBackendOperationsService? _operationsService;
    private readonly IBackendSalesHistoryService? _backendSalesHistoryService;
    private readonly IBackendWorkflowReadService? _workflowReadService;
    private readonly IBackendWorkflowReadService? _posWorkflowReadService;
    private readonly IBackendDashboardService? _dashboardService;
    private readonly IBackendSettingsService? _settingsService;
    private readonly IBackendBackupRestoreService? _backupRestoreService;
    private readonly IWorkstationPrinterSettings _workstationPrinterSettings;
    private readonly IProductionDocumentPrintService? _documentPrintService;
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
        IPosCatalogGateway? posCatalogGateway = null,
        DesktopApiClient? apiClient = null,
        IClientOperationIntentStore? operationIntents = null,
        IWorkstationPrinterSettings? workstationPrinterSettings = null)
    {
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(drawerService);
        ArgumentNullException.ThrowIfNull(toastService);

        _themeService = themeService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _toastService = toastService;
        _workstationPrinterSettings = workstationPrinterSettings ?? CreateWorkstationPrinterSettings();
        _documentPrintService = apiClient is null ? null : new RemoteProductionDocumentPrintService(apiClient);
        _operationIntents = operationIntents ?? new FileClientOperationIntentStore();
        _sessionContext = sessionContext ?? new DesignPreviewSessionContext();
        _posCatalogGateway = posCatalogGateway;
        _transactionService = CreateTransactionService(backendScopeFactory, apiClient);
        _purchasingInventoryService = apiClient is not null
            ? new RemotePurchasingInventoryService(apiClient, () => _sessionContext.UserId, _operationIntents)
            : backendScopeFactory is null
                ? null
                : new BackendPurchasingInventoryService(
                    backendScopeFactory,
                    () => _sessionContext.UserId);
        _stockAdjustmentService = apiClient is null
            ? null
            : new RemoteStockAdjustmentService(apiClient, () => _sessionContext.UserId);
        _productManagementService = apiClient is not null
            ? new RemoteProductManagementService(apiClient, () => _sessionContext.UserId)
            : backendScopeFactory is null
                ? null
                : new BackendProductManagementService(
                    backendScopeFactory,
                    () => _sessionContext.UserId);
        _backendThakaService = apiClient is not null
            ? new RemoteBackendThakaService(apiClient, _operationIntents)
            : backendScopeFactory is null
                ? null
                : new BackendThakaService(backendScopeFactory, () => _sessionContext.UserId);
        _businessOperationsService = apiClient is not null
            ? new RemoteBackendBusinessOperationsService(apiClient)
            : backendScopeFactory is null
                ? null
                : new BackendBusinessOperationsService(backendScopeFactory, () => _sessionContext.UserId);
        _operationsService = apiClient is not null
            ? new RemoteBackendOperationsService(apiClient)
            : backendScopeFactory is null
                ? null
                : new BackendOperationsService(backendScopeFactory, () => _sessionContext.UserId);
        _backendSalesHistoryService = apiClient is not null
            ? new BackendSalesHistoryService(apiClient)
            : backendScopeFactory is null
                ? null
                : new BackendSalesHistoryService(backendScopeFactory);
        _workflowReadService = apiClient is not null
            ? new RemoteStocktakeWorkflowService(apiClient, _operationIntents)
            : backendScopeFactory is null
                ? null
                : new BackendWorkflowReadService(backendScopeFactory, () => _sessionContext.UserId);
        _posWorkflowReadService = apiClient is null
            ? _workflowReadService
            : new RemotePosWorkflowService(apiClient);
        _dashboardService = apiClient is not null
            ? new RemoteBackendDashboardService(apiClient, _backendThakaService!)
            : backendScopeFactory is null
            ? null
            : new BackendDashboardService(
                _businessOperationsService!,
                _backendThakaService!,
                backendScopeFactory);
        _settingsService = apiClient is not null
            ? new RemoteBackendSettingsService(apiClient, () => _sessionContext.UserId)
            : backendScopeFactory is null
            ? null
            : new BackendSettingsService(
                backendScopeFactory,
                () => _sessionContext.UserId);
        _backupRestoreService = apiClient is not null
            ? new RemoteBackupRestoreService(apiClient, _operationIntents)
            : null;
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
                _dashboardService,
                project => OnWorkspaceOpened(this, project)),

            NavigationTarget.POS => new PosViewModel(
                _toastService,
                _dialogService,
                _transactionService,
                _sessionContext,
                _posCatalogGateway,
                _businessOperationsService,
                _posWorkflowReadService,
                _documentPrintService,
                _workstationPrinterSettings,
                _operationIntents),

            NavigationTarget.SalesHistory => new SalesHistoryViewModel(
                _sessionContext,
                _navigationService,
                _toastService,
                _drawerService,
                _dialogService,
                _transactionService,
                _backendSalesHistoryService,
                _documentPrintService,
                _workstationPrinterSettings),

            NavigationTarget.ThakaProjects => GetOrCreateThakaProjects(),
            NavigationTarget.ThakaWorkspace => CreateThakaWorkspace(),
            NavigationTarget.Purchases => _purchaseHistoryViewModel ??=
                new PurchaseHistoryViewModel(
                    _toastService,
                    _drawerService,
                    _dialogService,
                    _purchasingInventoryService,
                    _workflowReadService),
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
                    _workflowReadService,
                    _stockAdjustmentService,
                    _operationIntents),
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
                _operationsService),
            NavigationTarget.Warranty => new WarrantyViewModel(
                _operationsService,
                _toastService),
            NavigationTarget.Reports => _reportsViewModel ??= new ReportsViewModel(
                _businessOperationsService,
                _toastService),
            NavigationTarget.Settings => _settingsViewModel ??= new SettingsViewModel(
                _themeService,
                _dialogService,
                _toastService,
                _settingsService,
                _backupRestoreService)
            {
                WorkstationPrinterSettings = _workstationPrinterSettings
            },

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
            _workflowReadService);

        workspace.BackRequested += (_, _) =>
        {
            projects.RefreshDirectory();
            _navigationService?.Navigate(NavigationTarget.ThakaProjects);
        };

        return workspace;
    }

    private void OnWorkspaceOpened(object? sender, ThakaProjectListItemViewModel project)
    {
        _selectedThakaProject = project;
        _navigationService?.Navigate(NavigationTarget.ThakaWorkspace);
    }

    private ITransactionService CreateTransactionService(
        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory? backendScopeFactory,
        DesktopApiClient? apiClient)
    {
        if (backendScopeFactory is not null)
        {
            return new BackendTransactionService(
                backendScopeFactory,
                () => _sessionContext.UserId,
                apiClient ?? throw new InvalidOperationException("Production POS requires the Server API client."),
                _operationIntents);
        }

#if DEBUG
        return DemoTransactionService.Instance;
#else
        throw new InvalidOperationException(
            "Production runtime requires a registered backend scope factory for transactions.");
#endif
    }

    private static IWorkstationPrinterSettings CreateWorkstationPrinterSettings()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("Local application data is unavailable for workstation printer settings.");
        }

        var profilePath = Path.Combine(localAppData, "EdgeRetails", "printer-profiles.json");
        return new WindowsWorkstationPrinterSettings(new JsonPrinterProfileStore(profilePath));
    }
}
