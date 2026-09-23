using System.Windows;
using System.Windows.Controls;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views;

public partial class FirstSetupView : UserControl
{
    public FirstSetupView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is FirstSetupViewModel viewModel)
        {
            viewModel.OwnerPinClearRequested += OnOwnerPinClearRequested;
        }
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is FirstSetupViewModel viewModel)
        {
            viewModel.OwnerPinClearRequested -= OnOwnerPinClearRequested;
        }
    }

    private void OnOwnerPinClearRequested(object? sender, EventArgs e)
    {
        OwnerPinInput.Clear();
    }

    private void OnOwnerPinChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is FirstSetupViewModel viewModel &&
            sender is PasswordBox passwordBox)
        {
            viewModel.SetOwnerPin(passwordBox.Password);
        }
    }
}
