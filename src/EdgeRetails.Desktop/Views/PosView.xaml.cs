using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views;

/// <summary>
/// Interaction logic for PosView.xaml
/// </summary>
public partial class PosView : UserControl
{
    public PosView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm)
        {
            vm.FocusSearchRequested += OnFocusSearchRequested;
        }

        var window = Window.GetWindow(this);
        if (window != null)
        {
            window.PreviewKeyDown += OnWindowPreviewKeyDown;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PosViewModel vm)
        {
            vm.FocusSearchRequested -= OnFocusSearchRequested;
        }

        var window = Window.GetWindow(this);
        if (window != null)
        {
            window.PreviewKeyDown -= OnWindowPreviewKeyDown;
        }
    }

    private void OnFocusSearchRequested(object? sender, EventArgs e)
    {
        FocusSearchBox();
    }

    private void FocusSearchBox()
    {
        SearchInput.Focus();
        SearchInput.SelectAll();
    }

    private void OnCatalogRowPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGridRow row ||
            row.DataContext is not PosProductItemViewModel product ||
            DataContext is not PosViewModel vm)
        {
            return;
        }

        if (vm.AddToCartCommand.CanExecute(product))
        {
            vm.AddToCartCommand.Execute(product);
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not PosViewModel vm)
        {
            return;
        }

        if (e.Key == Key.F2)
        {
            FocusSearchBox();
            e.Handled = true;
        }
        else if (e.Key == Key.F10)
        {
            if (vm.CompleteSaleCommand.CanExecute(null))
            {
                vm.CompleteSaleCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.F4)
        {
            if (vm.ChangeCustomerCommand.CanExecute(null))
            {
                vm.ChangeCustomerCommand.Execute(null);
                e.Handled = true;
            }
        }
    }
}
