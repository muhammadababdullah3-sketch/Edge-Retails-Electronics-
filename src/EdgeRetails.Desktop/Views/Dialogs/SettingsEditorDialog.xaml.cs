using System.Windows;
using System.Windows.Controls;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views.Dialogs;

public partial class SettingsEditorDialog : UserControl
{
    public SettingsEditorDialog() => InitializeComponent();

    private void OnPinPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsEditorViewModel viewModel &&
            sender is PasswordBox passwordBox)
        {
            viewModel.NewPin = passwordBox.Password;
        }
    }
}
