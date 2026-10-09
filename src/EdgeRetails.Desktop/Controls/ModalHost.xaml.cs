using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using EdgeRetails.Desktop.Services;

namespace EdgeRetails.Desktop.Controls;

public partial class ModalHost : UserControl
{
    private IInputElement? _previouslyFocusedElement;

    public ModalHost()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        KeyDown += OnKeyDown;
        LostKeyboardFocus += OnLostKeyboardFocus;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            _previouslyFocusedElement = Keyboard.FocusedElement;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (!IsVisible)
                {
                    return;
                }

                FocusFirstContentControl();
            }));
        }
        else
        {
            var elementToRestore = _previouslyFocusedElement;
            _previouslyFocusedElement = null;
            if (elementToRestore is UIElement uiElement && uiElement.IsVisible && uiElement.Focusable)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    elementToRestore.Focus();
                }));
            }
        }
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (DataContext is IDialogService dialogService)
            {
                dialogService.Close();
            }
        }
    }

    private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsVisible && !IsKeyboardFocusWithin)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                if (IsVisible && !IsKeyboardFocusWithin)
                {
                    FocusFirstContentControl();
                }
            }));
        }
    }

    private void FocusFirstContentControl()
    {
        ModalPresenter.UpdateLayout();
        var control = FindFocusableControl(ModalPresenter);
        if (control is not null)
        {
            Keyboard.Focus(control);
        }
    }

    private static Control? FindFocusableControl(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Control { Focusable: true, IsTabStop: true, IsEnabled: true, IsVisible: true } control)
            {
                return control;
            }

            var descendant = FindFocusableControl(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
