using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Capture.App.ViewModels;

namespace Capture.App.Converters;

/// <summary>Row-wide background wash for a <see cref="BatchDividerRow"/> — bound to the whole row item
/// (not a specific property), so a <see cref="DocumentRow"/> sharing the same DataGridRow style always
/// resolves to Transparent rather than depending on a missing-property binding failing safely. Safe now
/// that dividers are standalone rows (see MainViewModel.BuildDisplayRows) — no document data shares the
/// row being tinted. Reuses the same colour resources as BatchAccentBrushConverter's wash mode.</summary>
public sealed class DividerRowBackgroundConverter : IValueConverter
{
    public static readonly DividerRowBackgroundConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not BatchDividerRow divider)
            return Brushes.Transparent;

        var resourceKey = divider.Accent ? "BatchABrush" : "BatchBBrush";
        if (Application.Current?.TryFindResource(resourceKey, out var resource) == true
            && resource is SolidColorBrush brush)
        {
            return new SolidColorBrush(brush.Color, 0.12);
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
