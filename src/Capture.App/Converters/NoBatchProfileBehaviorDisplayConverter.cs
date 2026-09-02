using System.Globalization;
using Avalonia.Data.Converters;
using Capture.Core.Batches;

namespace Capture.App.Converters;

public sealed class NoBatchProfileBehaviorDisplayConverter : IValueConverter
{
    public static readonly NoBatchProfileBehaviorDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            NoBatchProfileBehavior.NewBatchPerFile => "New batch per file",
            NoBatchProfileBehavior.AddToOpenBatch => "Add to open batch",
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
