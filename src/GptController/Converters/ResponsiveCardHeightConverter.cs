using System;
using System.Globalization;
using System.Windows.Data;

namespace GptController.Converters;

public sealed class ResponsiveCardHeightConverter : IValueConverter
{
    private const double MinimumHeight = 412;
    private const double MaximumHeight = 620;
    private const double ViewportFillRatio = 0.92;

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is not double viewportHeight
            || double.IsNaN(viewportHeight)
            || viewportHeight <= 0)
        {
            return MinimumHeight;
        }

        return Math.Clamp(
            Math.Floor(viewportHeight * ViewportFillRatio),
            MinimumHeight,
            MaximumHeight);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
