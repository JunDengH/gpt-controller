using System;
using System.Globalization;
using System.Windows.Data;

namespace GptController.Converters;

public sealed class AdaptiveSizeConverter : IValueConverter
{
    private const double CompactWindowWidth = 960;
    private const double ExpandedWindowWidth = 1440;

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        var (minimum, maximum) = ParseRange(parameter, culture);
        if (value is not double width || double.IsNaN(width))
        {
            return minimum;
        }

        var progress = Math.Clamp(
            (width - CompactWindowWidth) / (ExpandedWindowWidth - CompactWindowWidth),
            0,
            1);

        return Math.Round(minimum + ((maximum - minimum) * progress), 1);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();

    private static (double Minimum, double Maximum) ParseRange(
        object parameter,
        CultureInfo culture)
    {
        var parts = parameter?.ToString()?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], NumberStyles.Float, culture, out var minimum)
            || !double.TryParse(parts[1], NumberStyles.Float, culture, out var maximum)
            || maximum < minimum)
        {
            throw new ArgumentException(
                "Converter parameter must be a comma-separated minimum and maximum.",
                nameof(parameter));
        }

        return (minimum, maximum);
    }
}
