using System.Windows.Input;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Root ViewModel for MainWindow coordinating application lifecycle:
/// Application Start -> optional First Setup -> Login -> Main Shell -> Dashboard.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ILiveClock _clock;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private readonly IToastService _toastService;
    private readonly IFirstRunSetupState _setupState;
    private readonly IDemoIdentityService _identityService;
    private readonly IBackendIdentityService? _backendIdentityService;
    private readonly IBackendSetupService? _backendSetupService;
    private readonly IFrontendPermissionService _permissionService;
    private readonly PageViewModelFactory _pageFactory;
    private readonly NavigationService _navigationService;

    private ViewModelBase _currentContent = null!;
    private ShellViewModel? _shellViewModel;
    private LoginViewModel? _loginViewModel;
    private FirstSetupViewModel? _firstSetupViewModel;
    private ISessionContext? _activeSession;

    public MainViewModel(
        ILiveClock clock,
        IThemeService themeService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IToastService toastService,
        bool bypassLoginForSprint1Preview = false,
        IFirstRunSetupState? setupState = null,
        IDemoIdentityService? identityService = null,
        BackendRuntime? backendRuntime = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(themeService);
        ArgumentNullException.ThrowIfNull(dialogService);
        ArgumentNullException.ThrowIfNull(drawerService);
        ArgumentNullException.ThrowIfNull(toastService);

        _clock = clock;
        _themeService = themeService;
        _dialogService = dialogService;
        _drawerService = drawerService;
        _toastService = toastService;
        _setupState = setupState ?? new DemoFirstRunSetupState(isSetupRequired: false);
        _identityService = identityService ?? new DemoIdentityService();
        _backendIdentityService = backendRuntime is null
            ? null
            : new BackendIdentityService(backendRuntime.ScopeFactory);
        _backendSetupService = backendRuntime?.SetupService;

        var defaultSession = new DesignPreviewSessionContext();
        _pageFactory = new PageViewModelFactory(
            _themeService,
            _dialogService,
            _drawerService,
            _toastService,
            defaultSession,
            backendRuntime?.ScopeFactory,
            backendRuntime?.PosCatalogGateway);

        _permissionService = new DemoFrontendPermissionService();
        _navigationService = new NavigationService(
            _pageFactory,
            _permissionService,
            _dialogService,
            defaultSession);
        _pageFactory.SetNavigationService(_navigationService);

        ToggleSidebarCommand = new RelayCommand(
            () => _shellViewModel?.ToggleSidebarCommand.Execute(null),
            () => _shellViewModel is not null);

        if (bypassLoginForSprint1Preview)
        {
            SwitchToShell(defaultSession, NavigationTarget.Sprint1Verification);
            _currentContent = _shellViewModel!;
        }
        else if (_setupState.IsSetupRequired)
        {
            ShowFirstSetup();
        }
        else
        {
            ShowLogin();
        }
    }

    public ViewModelBase CurrentContent
    {
        get => _currentContent;
        private set => SetProperty(ref _currentContent, value);
    }

    public ICommand ToggleSidebarCommand { get; }

    public void OnLoginSuccess(ISessionContext session)
    {
        _activeSession = session;
        _pageFactory.SetSessionContext(session);
        _navigationService.SetSessionContext(session);
        SwitchToShell(session, NavigationTarget.Dashboard);
    }

    private void ShowFirstSetup()
    {
        _firstSetupViewModel = new FirstSetupViewModel(
            _setupState,
            _identityService,
            _toastService,
            settingsState: null,
            backendSetupService: _backendSetupService);

        _firstSetupViewModel.SetupCompleted += OnSetupCompleted;
        CurrentContent = _firstSetupViewModel;
    }

    private void OnSetupCompleted(object? sender, EventArgs e)
    {
        _firstSetupViewModel?.SetupCompleted -= OnSetupCompleted;
        _firstSetupViewModel = null;

        ShowLogin();
    }

    private void ShowLogin()
    {
        _loginViewModel = new LoginViewModel(
            _identityService,
            _backendIdentityService)
        {
            OnLoginSuccess = OnLoginSuccess
        };
        CurrentContent = _loginViewModel;
    }

    private void SwitchToShell(ISessionContext session, NavigationTarget initialTarget)
    {
        _shellViewModel?.Dispose();
        _shellViewModel = new ShellViewModel(
            _navigationService,
            _clock,
            session,
            _themeService,
            _dialogService,
            _drawerService,
            _toastService,
            _permissionService,
            SwitchUser);

        CurrentContent = _shellViewModel;
        _navigationService.Navigate(initialTarget);
    }

    private void SwitchUser()
    {
        var sessionToEnd = _activeSession;
        _activeSession = null;
        if (_backendIdentityService is not null &&
            sessionToEnd is not null)
        {
            _ = EndSessionAsync(sessionToEnd);
        }

        _dialogService.Close();
        _drawerService.Close();

        _shellViewModel?.Dispose();
        _shellViewModel = null;

        _navigationService.Reset();
        _pageFactory.ClearCachedPages();

        ShowLogin();
    }

    private async Task EndSessionAsync(ISessionContext session)
    {
        try
        {
            if (_backendIdentityService is not null)
            {
                await _backendIdentityService.SignOutAsync(session);
            }
        }
        catch (Exception ex)
        {
            _toastService.Show(
                $"Previous session could not be closed cleanly: {ex.Message}",
                ToastTone.Warning);
        }
    }

    public void Dispose()
    {
        if (_backendIdentityService is not null &&
            _activeSession is not null)
        {
            try
            {
                _backendIdentityService
                    .SignOutAsync(_activeSession)
                    .GetAwaiter()
                    .GetResult();
            }
            catch
            {
                // App shutdown must continue even if the database is unavailable.
            }

            _activeSession = null;
        }

        _shellViewModel?.Dispose();
        _pageFactory.Dispose();
    }
}
