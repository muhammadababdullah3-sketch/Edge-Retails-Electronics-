using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EdgeRetails.Desktop.Controls;

public partial class KpiCard : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(KpiCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(KpiCard), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(nameof(Tone), typeof(KpiTone), typeof(KpiCard), new PropertyMetadata(KpiTone.Brand));

    public static readonly DependencyProperty IconGeometryProperty =
        DependencyProperty.Register(nameof(IconGeometry), typeof(Geometry), typeof(KpiCard), new PropertyMetadata(null));

    public KpiCard()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public KpiTone Tone
    {
        get => (KpiTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public Geometry? IconGeometry
    {
        get => (Geometry?)GetValue(IconGeometryProperty);
        set => SetValue(IconGeometryProperty, value);
    }
}
