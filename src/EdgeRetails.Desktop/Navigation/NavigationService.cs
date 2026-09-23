using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IPageViewModelFactory _pageFactory;
    private readonly IFrontendPermissionService _permissionService;
    private readonly IDialogService? _dialogService;
    private ISessionContext _sessionContext;

    public NavigationService(
        IPageViewModelFactory pageFactory,
        IFrontendPermissionService? permissionService = null,
        IDialogService? dialogService = null,
        ISessionContext? sessionContext = null)
    {
        ArgumentNullException.ThrowIfNull(pageFactory);

        _pageFactory = pageFactory;
        _permissionService =
            permissionService ?? new DemoFrontendPermissionService();
        _dialogService = dialogService;
        _sessionContext =
            sessionContext ?? new DesignPreviewSessionContext();
    }

    public event EventHandler<NavigationChangedEventArgs>? Navigated;
    public NavigationTarget? CurrentTarget { get; private set; }

    public ViewModelBase? CurrentViewModel { get; private set; }

    public void SetSessionContext(ISessionContext sessionContext)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        _sessionContext = sessionContext;
        CurrentTarget = null;
        CurrentViewModel = null;
    }

    public void Reset()
    {
        CurrentTarget = null;
        CurrentViewModel = null;
    }

    public bool Navigate(NavigationTarget target)
    {
        if (!_permissionService.CanNavigate(
                _sessionContext,
                target,
                out var denialReason))
        {
            _dialogService?.Show(
                new PermissionRequiredViewModel(
                    denialReason,
                    _dialogService.Close));

            return false;
        }

        if (CurrentTarget == target && CurrentViewModel is not null)
        {
            return false;
        }
        var nextViewModel = _pageFactory.Create(target);

        CurrentTarget = target;
        CurrentViewModel = nextViewModel;
        Navigated?.Invoke(
            this,
            new NavigationChangedEventArgs(target, nextViewModel));

        return true;
    }
}
