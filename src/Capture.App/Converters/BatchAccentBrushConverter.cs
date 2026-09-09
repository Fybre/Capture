using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Capture.App.Converters;

public sealed class BatchAccentBrushConverter : IValueConverter
{
    public static readonly BatchAccentBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var bar = parameter is string text && text == "bar";
        var accent = value is true;
        var resourceKey = accent ? "BatchABrush" : "BatchBBrush";

        if (Application.Current?.TryFindResource(resourceKey, out var resource) == true
            && resource is IBrush brush)
        {
            return bar ? brush : new SolidColorBrush(((SolidColorBrush)brush).Color, 0.12);
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
