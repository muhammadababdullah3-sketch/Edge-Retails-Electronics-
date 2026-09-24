using System.Windows;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop;

public partial class App : System.Windows.Application
{
    private ILiveClock? _clock;
    private ThemeService? _themeService;
    private MainViewModel? _mainViewModel;
    private BackendRuntime? _backendRuntime;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                $"An unexpected application error occurred:\n\n{args.Exception.Message}",
                "Application Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };

        _themeService = new ThemeService();
        var themeService = _themeService;
        var dialogService = new DialogService();
        var drawerService = new DrawerService();
        var toastService = new ToastService();
        _clock = new LiveClockService();

        themeService.ApplyTheme(AppTheme.Light);

#if DEBUG
        var bypassLogin = e.Args.Any(
            arg => string.Equals(
                arg,
                "--sprint1-preview",
                StringComparison.OrdinalIgnoreCase));

        var forceSetupPreview = e.Args.Any(
            arg => string.Equals(
                arg,
                "--setup-preview",
                StringComparison.OrdinalIgnoreCase));
#else
        var bypassLogin = false;
        var forceSetupPreview = false;
#endif

        var environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
#if DEBUG
            ?? "Development";
#else
            ?? "Production";
#endif

        var isDevelopment = string.Equals(environmentName, "Development", StringComparison.OrdinalIgnoreCase);

        var identityService = new DemoIdentityService();
        var previewMode = bypassLogin || forceSetupPreview;
        if (previewMode)
        {
            _backendRuntime = null;
        }
        else
        {
            try
            {
                _backendRuntime = BackendRuntime.CreateFromEnvironment();
            }
            catch (Exception ex)
            {
                var failure = StartupFailureClassifier.Classify(ex);
                var modeLabel = isDevelopment ? "Development" : "Production";
                MessageBox.Show(
                    $"[{failure.Code}] {failure.Title} ({modeLabel})\n\n{failure.ErrorMessage}\n\nTroubleshooting Guidance:\n{failure.RemediationGuidance}",
                    $"Edge Retails — {failure.Title}",
                    MessageBoxButton.OK,
                    isDevelopment ? MessageBoxImage.Warning : MessageBoxImage.Error);

                Shutdown();
                return;
            }
        }

        var setupRequired = forceSetupPreview;
        if (_backendRuntime is not null)
        {
            var startup = await _backendRuntime.CheckStartupAsync();

            if (!startup.IsReady)
            {
                var failure = StartupFailureClassifier.ClassifyDatabaseFailure(
                    startup.FailureReason,
                    startup.PendingMigrations);

                MessageBox.Show(
                    $"[{failure.Code}] {failure.Title}\n\n{failure.ErrorMessage}\n\nTroubleshooting Guidance:\n{failure.RemediationGuidance}",
                    $"Edge Retails — {failure.Title}",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                _backendRuntime.Dispose();
                _backendRuntime = null;
                Shutdown();
                return;
            }

            setupRequired = startup.IsSetupRequired;
        }

        var setupState = new DemoFirstRunSetupState(setupRequired);

        _mainViewModel = new MainViewModel(
            _clock,
            themeService,
            dialogService,
            drawerService,
            toastService,
            bypassLogin,
            setupState,
            identityService,
            _backendRuntime);

        var mainWindow = new MainWindow
        {
            DataContext = _mainViewModel,
        };

        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        _clock?.Dispose();
        _themeService?.Dispose();
        _backendRuntime?.Dispose();

        base.OnExit(e);
    }
}
