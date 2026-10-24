using System.Globalization;
using System.Windows.Data;

namespace EdgeRetails.Desktop.Converters;

/// <summary>
/// Converts an available container height into a height-bounded account viewport.
/// </summary>
/// <remarks>
/// The login card has a fixed cost for its chrome (header, PIN boxes, keypad, sign-in button).
/// Only the account list may give up space when the window is short, so the viewport follows the
/// available height and is clamped between <see cref="MinimumHeight"/> and the converter maximum.
/// That keeps the PIN keypad and sign-in button on screen at every supported window size instead
/// of letting the whole login surface scroll.
///
/// The converter parameter is <c>"offset:max"</c>, both in device-independent pixels and parsed
/// with the invariant culture so a non-dot decimal separator can never produce a different layout.
/// </remarks>
public sealed class HeightOffsetConverter : IValueConverter
{
    /// <summary>
    /// Smallest viewport height. One complete account card is
    /// <c>Padding(10.5*2) + Avatar(31.5) + card margin(7) = 59.5</c>px; the floor is set slightly
    /// below that so the card always fits inside the smallest supported window while the keypad
    /// and sign-in button remain visible.
    /// </summary>
    public const double MinimumHeight = 44;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || double.IsInfinity(available))
        {
            return MinimumHeight;
        }

        double offset = 0;
        double maximum = double.MaxValue;

        if (parameter is string text && !string.IsNullOrWhiteSpace(text))
        {
            var parts = text.Split(':');
            if (parts.Length > 0 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedOffset))
            {
                offset = parsedOffset;
            }

            if (parts.Length > 1 &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMaximum))
            {
                maximum = parsedMaximum;
            }
        }

        var bounded = Math.Clamp(available - offset, MinimumHeight, Math.Max(MinimumHeight, maximum));
        return Math.Round(bounded, 2);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
