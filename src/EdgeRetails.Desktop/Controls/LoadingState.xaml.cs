using System.Windows;
using System.Windows.Controls;

namespace EdgeRetails.Desktop.Controls;

public partial class LoadingState : UserControl
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(nameof(Message), typeof(string), typeof(LoadingState), new PropertyMetadata("Loading..."));

    public LoadingState()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
