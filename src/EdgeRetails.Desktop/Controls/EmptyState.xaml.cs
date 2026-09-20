using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EdgeRetails.Desktop.Controls;

public partial class EmptyState : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(EmptyState), new PropertyMetadata("Nothing here yet"));

    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(EmptyState), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconGeometryProperty =
        DependencyProperty.Register(nameof(IconGeometry), typeof(Geometry), typeof(EmptyState), new PropertyMetadata(null));

    public EmptyState()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public Geometry? IconGeometry
    {
        get => (Geometry?)GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }
}
