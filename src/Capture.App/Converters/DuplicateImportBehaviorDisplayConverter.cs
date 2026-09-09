using System.Globalization;
using Avalonia.Data.Converters;
using Capture.Core.Store;

namespace Capture.App.Converters;

public sealed class DuplicateImportBehaviorDisplayConverter : IValueConverter
{
    public static readonly DuplicateImportBehaviorDisplayConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            DuplicateImportBehavior.ImportAnyway => "Import anyway",
            DuplicateImportBehavior.Skip => "Skip",
            DuplicateImportBehavior.FlagForReview => "Import and flag for review",
            _ => value?.ToString() ?? string.Empty
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
