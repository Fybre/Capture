using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Capture.App.ViewModels;
using Capture.Core.Models;

namespace Capture.App.Converters;

internal static class IndexCellLookup
{
    public static IndexValue? Find(DocumentRow row, string fieldName) =>
        row.Indexes.FirstOrDefault(value =>
            !value.HideFromIndexing && string.Equals(value.FieldName, fieldName, StringComparison.OrdinalIgnoreCase));

    public static IBrush ResolveBrush(string resourceKey)
    {
        if (Application.Current?.TryFindResource(resourceKey, out var resource) == true && resource is IBrush brush)
            return brush;
        return Brushes.Gray;
    }
}

/// <summary>Displays the value of a single dynamic index-field column. ConverterParameter is the field name.</summary>
public sealed class IndexCellTextConverter : IValueConverter
{
    public static readonly IndexCellTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DocumentRow row || parameter is not string fieldName)
            return "—";

        var match = IndexCellLookup.Find(row, fieldName);
        return match is null || string.IsNullOrWhiteSpace(match.Value) ? "—" : match.Value;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colors a dynamic index-field cell by state (empty / flagged / normal). ConverterParameter is the field name.</summary>
public sealed class IndexCellForegroundConverter : IValueConverter
{
    public static readonly IndexCellForegroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DocumentRow row || parameter is not string fieldName)
            return IndexCellLookup.ResolveBrush("InkSoftBrush");

        var match = IndexCellLookup.Find(row, fieldName);
        var key = match is null || string.IsNullOrWhiteSpace(match.Value)
            ? "FaintBrush"
            : match.IsMissing || match.ValidationError is not null || match.IsLowConfidence(row.ConfidenceThreshold)
                ? "WarnBrush"
                : "InkSoftBrush";
        return IndexCellLookup.ResolveBrush(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
