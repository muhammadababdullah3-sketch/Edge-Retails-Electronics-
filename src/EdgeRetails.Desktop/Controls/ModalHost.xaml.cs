using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

                ModalPresenter.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                if (!ModalPresenter.IsKeyboardFocusWithin)
                {
                    ModalPresenter.Focus();
                }
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
                    ModalPresenter.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
                }
            }));
        }
    }
}
