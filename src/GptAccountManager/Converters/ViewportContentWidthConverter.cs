using System;
using System.Globalization;
using System.Windows.Data;

namespace GptAccountManager.Converters;

public sealed class ViewportContentWidthConverter : IValueConverter
{
    private const double DefaultReservedWidth = 16;

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || width <= 0)
        {
            return 0d;
        }

        var reservedWidth = DefaultReservedWidth;
        if (parameter is not null
            && double.TryParse(
                parameter.ToString(),
                NumberStyles.Float,
                culture,
                out var parsedReservedWidth))
        {
            reservedWidth = Math.Max(0, parsedReservedWidth);
        }

        return Math.Max(0, width - reservedWidth);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
