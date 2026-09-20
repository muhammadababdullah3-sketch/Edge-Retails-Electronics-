using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views.Dialogs;

/// <summary>
/// Interaction logic for CompleteSaleDialog.xaml.
/// Follows 0-Canvas rule and strict MVVM architecture.
/// </summary>
public partial class CompleteSaleDialog : UserControl
{
    public CompleteSaleDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public CompleteSaleDialog(CompleteSaleViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Automatically focus amount received input for rapid POS entry
        AmountReceivedTextBox.Focus();
        AmountReceivedTextBox.SelectAll();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CompleteSaleViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && !vm.IsProcessing && vm.CanComplete)
        {
            e.Handled = true;
            if (vm.CompleteSaleCommand.CanExecute(null))
            {
                vm.CompleteSaleCommand.Execute(null);
            }
        }
        else if (e.Key == Key.Escape && !vm.IsProcessing)
        {
            e.Handled = true;
            if (vm.CancelCommand.CanExecute(null))
            {
                vm.CancelCommand.Execute(null);
            }
        }
    }
}
