using System.Windows.Media;
using EdgeRetails.Desktop.Services;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Navigation;

public sealed class NavigationItemViewModel : ViewModelBase
{
    private bool _isSelected;
    private bool _isVisible = true;

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

    public static NavigationItemViewModel CreateForSession(
        string title,
        NavigationTarget target,
        Geometry iconGeometry,
        ISessionContext sessionContext,
        IFrontendPermissionService permissionService)
    {
        ArgumentNullException.ThrowIfNull(sessionContext);
        ArgumentNullException.ThrowIfNull(permissionService);

        var item = new NavigationItemViewModel(title, target, iconGeometry);
        item.IsVisible = permissionService.CanNavigate(sessionContext, target, out _);
        return item;
    }

    public string Title { get; }

    public NavigationTarget Target { get; }

    public Geometry IconGeometry { get; }

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public bool IsVisible
    {
        get => _isVisible;
        internal set => SetProperty(ref _isVisible, value);
    }
}
