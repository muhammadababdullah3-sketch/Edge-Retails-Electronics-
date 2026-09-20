using System.Windows.Media;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class NavigationItemViewModel : ViewModelBase
{
    private bool _isSelected;

    public NavigationItemViewModel(
        string title,
        NavigationTarget target,
        Geometry iconGeometry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(iconGeometry);

        Title = title;
        Target = target;
        IconGeometry = iconGeometry;
    }

    public string Title { get; }

    public NavigationTarget Target { get; }

    public Geometry IconGeometry { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }
}
