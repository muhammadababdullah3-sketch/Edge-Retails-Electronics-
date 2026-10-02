using System.Windows;
using System.Windows.Controls;
using EdgeRetails.Desktop.ViewModels;
using System.ComponentModel;

namespace EdgeRetails.Desktop.Views.Dialogs;

public partial class SettingsEditorDialog : UserControl
{
    private SettingsEditorViewModel? _editor;
    public SettingsEditorDialog() => InitializeComponent();

    private void OnEditorDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_editor is not null)
        {
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        }
        _editor = e.NewValue as SettingsEditorViewModel;
        PinInput.Clear();
        if (_editor is not null)
        {
            _editor.PropertyChanged += OnEditorPropertyChanged;
        }
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsEditorViewModel.NewPin) && string.IsNullOrEmpty(_editor?.NewPin))
        {
            PinInput.Clear();
        }
    }

    private void OnEditorUnloaded(object sender, RoutedEventArgs e)
    {
        if (_editor is not null)
        {
            _editor.NewPin = string.Empty;
            _editor.PropertyChanged -= OnEditorPropertyChanged;
        }
        PinInput.Clear();
    }

    private void OnPinPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsEditorViewModel viewModel &&
            sender is PasswordBox passwordBox)
        {
            viewModel.NewPin = passwordBox.Password;
        }
    }
}
