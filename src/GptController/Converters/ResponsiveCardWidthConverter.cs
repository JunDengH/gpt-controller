using System;
using System.Globalization;
using System.Windows.Data;

namespace GptController.Converters;

public sealed class ResponsiveCardWidthConverter : IValueConverter
{
    internal const double ThreeColumnThreshold = 1020;
    internal const double TwoColumnThreshold = 720;
    internal const double MinimumWidth = 320;

    public object Convert(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || width <= 0)
        {
            return MinimumWidth;
        }

        var availableWidth = Math.Max(MinimumWidth, width - 2);
        var columns = availableWidth >= ThreeColumnThreshold
            ? 3
            : availableWidth >= TwoColumnThreshold
                ? 2
                : 1;

        return Math.Floor(availableWidth / columns);
    }

    public object ConvertBack(
        object value,
        Type targetType,
        object parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
