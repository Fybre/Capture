using System.Collections;
using System.Globalization;
using Avalonia.Data.Converters;
using Capture.Core.Redaction;

namespace Capture.App.Converters;

/// <summary>Renders a redaction set's raw entity type codes as a comma-separated list of friendly
/// names, e.g. for a tooltip showing what a set actually covers.</summary>
public sealed class EntityListSummaryConverter : IValueConverter
{
    public static readonly EntityListSummaryConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable entities
            ? string.Join(", ", entities.Cast<string>().Select(PresidioEntityNames.Describe))
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
