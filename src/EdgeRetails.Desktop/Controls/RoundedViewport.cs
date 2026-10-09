using System.Windows;
using System.Windows.Media;

namespace EdgeRetails.Desktop.Controls;

/// <summary>Keeps table headers and drawer content inside the rounded outer viewport.</summary>
public static class RoundedViewport
{
    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(double), typeof(RoundedViewport), new PropertyMetadata(0d, OnRadiusChanged));

    public static double GetRadius(DependencyObject element) => (double)element.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject element, double value) => element.SetValue(RadiusProperty, value);

    private static void OnRadiusChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        element.SizeChanged -= OnSizeChanged;
        if ((double)args.NewValue > 0)
        {
            element.Loaded += OnLoaded;
            element.SizeChanged += OnSizeChanged;
            UpdateClip(element);
        }
        else
        {
            element.ClearValue(UIElement.ClipProperty);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs args) => UpdateClip((FrameworkElement)sender);
    private static void OnSizeChanged(object sender, SizeChangedEventArgs args) => UpdateClip((FrameworkElement)sender);

    private static void UpdateClip(FrameworkElement element)
    {
        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return;
        }

        var radius = Math.Min(GetRadius(element), Math.Min(element.ActualWidth, element.ActualHeight) / 2);
        var clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
        clip.Freeze();
        element.SetCurrentValue(UIElement.ClipProperty, clip);
    }
}
