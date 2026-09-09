using System.Globalization;
using Avalonia.Data.Converters;
using Capture.Core.Watch;

namespace Capture.App.Converters;

public sealed class InboxOrderDisplayConverter : IValueConverter
{
    public static readonly InboxOrderDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            InboxOrder.NewestFirst => "Newest batches first",
            InboxOrder.OldestFirst => "Oldest batches first",
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
