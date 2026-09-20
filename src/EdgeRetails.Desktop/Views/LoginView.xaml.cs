using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views;

/// <summary>
/// Interaction logic for LoginView.xaml.
/// Supports physical keyboard numeric entry, Backspace, Escape, and Enter key shortcuts.
/// </summary>
public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not LoginViewModel vm)
        {
            return;
        }

        // Physical keyboard numeric keys: top row 0-9
        if (e.Key >= Key.D0 && e.Key <= Key.D9)
        {
            int digit = e.Key - Key.D0;
            vm.AppendDigit(digit.ToString());
            e.Handled = true;
        }
        // Physical keyboard numeric keys: numpad 0-9
        else if (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
        {
            int digit = e.Key - Key.NumPad0;
            vm.AppendDigit(digit.ToString());
            e.Handled = true;
        }
        // Backspace removes last entered PIN digit
        else if (e.Key == Key.Back)
        {
            vm.Backspace();
            e.Handled = true;
        }
        // Escape clears PIN
        else if (e.Key == Key.Escape)
        {
            vm.ClearPin();
            e.Handled = true;
        }
        // Enter triggers sign in if PIN is complete
        else if (e.Key == Key.Enter)
        {
            if (vm.CanSignIn)
            {
                _ = vm.AuthenticateAsync();
                e.Handled = true;
            }
        }
    }
}
