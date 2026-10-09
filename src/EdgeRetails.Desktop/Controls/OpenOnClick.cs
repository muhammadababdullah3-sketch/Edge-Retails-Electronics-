using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace EdgeRetails.Desktop.Controls;

/// <summary>Shared card/row activation that leaves nested input and action controls alone.</summary>
public static class OpenOnClick
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(OpenOnClick), new PropertyMetadata(null, OnCommandChanged));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.RegisterAttached(
        "CommandParameter", typeof(object), typeof(OpenOnClick));
    private static readonly DependencyProperty PressPointProperty = DependencyProperty.RegisterAttached(
        "PressPoint", typeof(Point?), typeof(OpenOnClick));

    public static ICommand? GetCommand(DependencyObject element) => (ICommand?)element.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject element, ICommand? value) => element.SetValue(CommandProperty, value);
    public static object? GetCommandParameter(DependencyObject element) => element.GetValue(CommandParameterProperty);
    public static void SetCommandParameter(DependencyObject element, object? value) => element.SetValue(CommandParameterProperty, value);

    private static void OnCommandChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }
        element.PreviewMouseLeftButtonDown -= OnMouseDown;
        element.PreviewMouseLeftButtonUp -= OnMouseUp;
        element.KeyUp -= OnKeyUp;
        if (args.NewValue is ICommand)
        {
            element.SetCurrentValue(UIElement.FocusableProperty, true);
            element.SetCurrentValue(FrameworkElement.CursorProperty, Cursors.Hand);
            element.PreviewMouseLeftButtonDown += OnMouseDown;
            element.PreviewMouseLeftButtonUp += OnMouseUp;
            element.KeyUp += OnKeyUp;
        }
    }

    private static bool IsNestedInput(DependencyObject? source, FrameworkElement owner)
    {
        while (source is not null && source != owner)
        {
            if (source is ButtonBase or TextBoxBase or PasswordBox or Selector or Thumb or ScrollBar)
            {
                return true;
            }
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private static void OnMouseDown(object sender, MouseButtonEventArgs args)
    {
        var element = (FrameworkElement)sender;
        element.SetValue(PressPointProperty, IsNestedInput(args.OriginalSource as DependencyObject, element)
            ? null : args.GetPosition(element));
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs args)
    {
        var element = (FrameworkElement)sender;
        var pressed = (Point?)element.GetValue(PressPointProperty);
        element.ClearValue(PressPointProperty);
        if (pressed is Point origin && (args.GetPosition(element) - origin).Length < 4
            && !IsNestedInput(args.OriginalSource as DependencyObject, element))
        {
            Execute(element);
        }
    }

    private static void OnKeyUp(object sender, KeyEventArgs args)
    {
        var element = (FrameworkElement)sender;
        if (Keyboard.FocusedElement == element && args.Key is Key.Enter or Key.Space)
        {
            args.Handled = Execute(element);
        }
    }

    private static bool Execute(FrameworkElement element)
    {
        var command = GetCommand(element);
        var parameter = GetCommandParameter(element);
        if (command?.CanExecute(parameter) != true)
        {
            return false;
        }
        command.Execute(parameter);
        return true;
    }
}
