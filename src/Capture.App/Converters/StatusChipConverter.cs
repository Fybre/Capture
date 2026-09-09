using System.Globalization;
using Avalonia.Data.Converters;

namespace Capture.App.Converters;

/// <summary>Maps a DocumentRow.StatusDisplay string to whether a given chip style class applies.</summary>
public sealed class StatusChipConverter : IValueConverter
{
    public static readonly StatusChipConverter IsReady = new(display => display is "Ready" or "Exported");
    public static readonly StatusChipConverter IsReview = new(display => display is "Needs review");
    public static readonly StatusChipConverter IsError = new(display => display is "Error");
    public static readonly StatusChipConverter IsNeutral = new(display => display is "Queued" or "Processing");

    private readonly Func<string, bool> _predicate;

    private StatusChipConverter(Func<string, bool> predicate)
    {
        _predicate = predicate;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text && _predicate(text);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
