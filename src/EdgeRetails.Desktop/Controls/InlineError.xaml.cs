using System.Windows;
using System.Windows.Controls;

namespace EdgeRetails.Desktop.Controls;

public partial class InlineError : UserControl
{
    public static readonly DependencyProperty MessageProperty =
        DependencyProperty.Register(
            nameof(Message),
            typeof(string),
            typeof(InlineError),
            new PropertyMetadata("Something went wrong."));

    public InlineError()
    {
        InitializeComponent();
    }

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }
}
