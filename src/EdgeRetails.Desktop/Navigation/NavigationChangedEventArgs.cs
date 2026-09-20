using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class NavigationChangedEventArgs : EventArgs
{
    public NavigationChangedEventArgs(
        NavigationTarget target,
        ViewModelBase viewModel)
    {
        Target = target;
        ViewModel = viewModel;
    }

    public NavigationTarget Target { get; }

    public ViewModelBase ViewModel { get; }
}
