using System.Windows.Input;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.ViewModels;

/// <summary>
/// Root ViewModel for MainWindow coordinating application lifecycle:
/// Application Start -> Login -> successful login -> Main Shell -> Dashboard.
/// </summary>
public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ILiveClock _clock;
    private readonly IThemeService _themeService;
    private readonly IDialogService _dialogService;
    private readonly IDrawerService _drawerService;
    private readonly IToastService _toastService;
    private readonly PageViewModelFactory _pageFactory;
    private readonly NavigationService _navigationService;

    private ViewModelBase _currentContent;
    private ShellViewModel? _shellViewModel;
    private LoginViewModel? _loginViewModel;

    public MainViewModel(
        ILiveClock clock,
        IThemeService themeService,
        IDialogService dialogService,
        IDrawerService drawerService,
        IToastService toastService,
        bool bypassLoginForSprint1Preview = false)
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

        var defaultSession = new DesignPreviewSessionContext();
        _pageFactory = new PageViewModelFactory(
            _themeService,
            _dialogService,
            _drawerService,
            _toastService,
            defaultSession);

        _navigationService = new NavigationService(_pageFactory);
        _pageFactory.SetNavigationService(_navigationService);

        ToggleSidebarCommand = new RelayCommand(
            () => _shellViewModel?.ToggleSidebarCommand.Execute(null),
            () => _shellViewModel is not null);

        if (bypassLoginForSprint1Preview)
        {
            SwitchToShell(defaultSession, NavigationTarget.Sprint1Verification);
            _currentContent = _shellViewModel!;
        }
        else
        {
            _loginViewModel = new LoginViewModel();
            _loginViewModel.OnLoginSuccess = OnLoginSuccess;
            _currentContent = _loginViewModel;
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
        _pageFactory.SetSessionContext(session);
        SwitchToShell(session, NavigationTarget.Dashboard);
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
            _toastService);

        CurrentContent = _shellViewModel;
        _navigationService.Navigate(initialTarget);
    }

    public void Dispose()
    {
        _shellViewModel?.Dispose();
    }
}
