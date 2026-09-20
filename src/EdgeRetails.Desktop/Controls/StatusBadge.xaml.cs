using System.Windows;
using System.Windows.Controls;

namespace EdgeRetails.Desktop.Controls;

public partial class StatusBadge : UserControl
{
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(StatusBadge),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ToneProperty =
        DependencyProperty.Register(
            nameof(Tone),
            typeof(BadgeTone),
            typeof(StatusBadge),
            new PropertyMetadata(BadgeTone.Neutral));

    public StatusBadge()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BadgeTone Tone
    {
        get => (BadgeTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }
}
