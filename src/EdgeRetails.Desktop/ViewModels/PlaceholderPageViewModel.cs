using EdgeRetails.Desktop.Navigation;

namespace EdgeRetails.Desktop.ViewModels;

public sealed class PlaceholderPageViewModel : ViewModelBase
{
    public PlaceholderPageViewModel(NavigationTarget target, string title)
    {
        Target = target;
        Title = title;
        Subtitle = "Sprint 1 shell placeholder. The production screen is implemented in its feature sprint.";
    }

    public NavigationTarget Target { get; }

    public string Title { get; }

    public string Subtitle { get; }
}
