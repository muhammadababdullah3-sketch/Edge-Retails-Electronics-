using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class NavigationService : INavigationService
{
    private readonly IPageViewModelFactory _pageFactory;

    public NavigationService(IPageViewModelFactory pageFactory)
    {
        ArgumentNullException.ThrowIfNull(pageFactory);
        _pageFactory = pageFactory;
    }

    public event EventHandler<NavigationChangedEventArgs>? Navigated;

    public NavigationTarget? CurrentTarget { get; private set; }

    public ViewModelBase? CurrentViewModel { get; private set; }

    public bool Navigate(NavigationTarget target)
    {
        if (CurrentTarget == target && CurrentViewModel is not null)
        {
            return false;
        }

        var nextViewModel = _pageFactory.Create(target);

        CurrentTarget = target;
        CurrentViewModel = nextViewModel;
        Navigated?.Invoke(this, new NavigationChangedEventArgs(target, nextViewModel));
        return true;
    }
}
