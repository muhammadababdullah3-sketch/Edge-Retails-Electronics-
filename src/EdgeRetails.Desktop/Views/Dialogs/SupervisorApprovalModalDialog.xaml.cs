using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using EdgeRetails.Desktop.ViewModels;

namespace EdgeRetails.Desktop.Views.Dialogs;

public partial class SupervisorApprovalModalDialog : UserControl
{
    private SupervisorApprovalModalViewModel? _viewModel;

    public SupervisorApprovalModalDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as SupervisorApprovalModalViewModel;
        SupervisorPinBox.Clear();

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SupervisorApprovalModalViewModel.SupervisorPin) &&
            string.IsNullOrEmpty(_viewModel?.SupervisorPin))
        {
            SupervisorPinBox.Clear();
        }
    }

    private void OnSupervisorPinChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SupervisorApprovalModalViewModel viewModel &&
            sender is PasswordBox passwordBox)
        {
            viewModel.SupervisorPin = passwordBox.Password;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ClearSecret();
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        SupervisorPinBox.Clear();
    }
}
