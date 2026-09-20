using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public interface INavigationService
{
    event EventHandler<NavigationChangedEventArgs>? Navigated;

    NavigationTarget? CurrentTarget { get; }

    ViewModelBase? CurrentViewModel { get; }

    bool Navigate(NavigationTarget target);
}
