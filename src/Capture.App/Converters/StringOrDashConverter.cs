using System.Globalization;
using Avalonia.Data.Converters;

namespace Capture.App.Converters;

public sealed class StringOrDashConverter : IValueConverter
{
    public static readonly StringOrDashConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && !string.IsNullOrWhiteSpace(text) ? text : "—";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
