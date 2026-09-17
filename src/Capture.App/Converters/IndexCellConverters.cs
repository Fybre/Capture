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
    public static IndexValue? Find(DocumentRow row, IndexCellBinding request) =>
        (request.IsBatchField ? row.BatchIndexes : row.DocumentIndexes).FirstOrDefault(value =>
            !value.HideFromIndexing && string.Equals(value.FieldName, request.FieldName, StringComparison.OrdinalIgnoreCase));

    public static IBrush ResolveBrush(string resourceKey)
    {
        if (Application.Current?.TryFindResource(resourceKey, out var resource) == true && resource is IBrush brush)
            return brush;
        return Brushes.Gray;
    }
}

public sealed record IndexCellBinding(string FieldName, bool IsBatchField);

/// <summary>Toggles a Table mode cell between its read-only display and its inline editor — bound
/// against <see cref="DocumentRow.EditingField"/> with the cell's own <see cref="IndexCellBinding"/> as
/// ConverterParameter. <see cref="Editing"/> is true only for the one field currently being edited on
/// that row; <see cref="NotEditing"/> is its inverse, used by the read-only TextBlock so exactly one of
/// the two is ever visible for a given cell. See MainWindow.axaml.cs's BuildIndexColumn.</summary>
public sealed class IndexCellIsEditingConverter : IValueConverter
{
    public static readonly IndexCellIsEditingConverter Editing = new(invert: false);
    public static readonly IndexCellIsEditingConverter NotEditing = new(invert: true);

    private readonly bool _invert;

    private IndexCellIsEditingConverter(bool invert) => _invert = invert;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isThisCell = value is IndexCellBinding current && parameter is IndexCellBinding request && current == request;
        return _invert ? !isThisCell : isThisCell;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Displays the value of a single dynamic index-field column. ConverterParameter is the field name.</summary>
public sealed class IndexCellTextConverter : IValueConverter
{
    public static readonly IndexCellTextConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DocumentRow row || parameter is not IndexCellBinding request)
            return "—";

        var match = IndexCellLookup.Find(row, request);
        return match is null || string.IsNullOrWhiteSpace(match.Value)
            ? "—"
            : request.IsBatchField && match.Sensitive ? "••••••" : match.Value;
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
        if (value is not DocumentRow row || parameter is not IndexCellBinding request)
            return IndexCellLookup.ResolveBrush("InkSoftBrush");

        var match = IndexCellLookup.Find(row, request);
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
