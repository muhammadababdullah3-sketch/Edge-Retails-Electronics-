using System.Windows;
using EdgeRetails.Desktop.Navigation;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop;

public partial class App : System.Windows.Application
{
    private ILiveClock? _clock;
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        var themeService = new ThemeService();
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
#else
        var bypassLogin = false;
#endif

        _mainViewModel = new MainViewModel(
            _clock,
            themeService,
            dialogService,
            drawerService,
            toastService,
            bypassLogin);

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

        base.OnExit(e);
    }
}
