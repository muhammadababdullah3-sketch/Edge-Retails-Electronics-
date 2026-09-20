using System.Windows;
using System.Windows.Controls;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views;

public partial class FirstSetupView : UserControl
{
    public FirstSetupView() => InitializeComponent();

    private void OnOwnerPinChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is FirstSetupViewModel viewModel &&
            sender is PasswordBox passwordBox)
        {
            viewModel.SetOwnerPin(passwordBox.Password);
        }
    }
}
