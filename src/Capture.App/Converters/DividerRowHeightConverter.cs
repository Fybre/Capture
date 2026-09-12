using System.Globalization;
using Avalonia.Data.Converters;
using Capture.App.ViewModels;

namespace Capture.App.Converters;

/// <summary>Explicit per-row height for a <see cref="BatchDividerRow"/> — Avalonia's DataGrid computes
/// one shared "auto" row height from the tallest row's content rather than sizing each row
/// independently, so a divider row's own (shorter) content never actually shrinks it on its own; it
/// just ends up vertically centered inside whatever height the document rows need. Binding this
/// directly on the row overrides that for just the divider rows, leaving every DocumentRow's height
/// (NaN — auto/shared, as it was before) untouched.</summary>
public sealed class DividerRowHeightConverter : IValueConverter
{
    public static readonly DividerRowHeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is BatchDividerRow ? 26.0 : double.NaN;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
