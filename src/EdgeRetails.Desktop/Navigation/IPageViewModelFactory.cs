using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public interface IPageViewModelFactory
{
    ViewModelBase Create(NavigationTarget target);
}
