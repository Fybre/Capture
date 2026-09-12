using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Capture.App.Converters;

/// <summary>Turns a 0..1 <c>StatBar.Fraction</c> (see <c>Capture.App.ViewModels.StatBar</c>) into a star-sized
/// <see cref="GridLength"/> — used to grow a bar's fill row from the bottom of a fixed-height track
/// without any pixel math, by giving the empty row above it <c>1 - fraction</c> stars and the fill row
/// below it <c>fraction</c> stars (a zero-star row collapses to nothing, so a 0 or 1 fraction still
/// renders correctly at the extremes).</summary>
public sealed class FractionToStarConverter : IValueConverter
{
    public static readonly FractionToStarConverter Fill = new(invert: false);
    public static readonly FractionToStarConverter Empty = new(invert: true);

    private readonly bool _invert;

    private FractionToStarConverter(bool invert) => _invert = invert;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var fraction = value is double d ? Math.Clamp(d, 0, 1) : 0;
        var stars = _invert ? 1 - fraction : fraction;
        // A GridLength can't be exactly zero stars (Avalonia treats 0 as "auto"), so floor it just above
        // zero — visually indistinguishable from empty, but keeps the row a real star row.
        return new GridLength(Math.Max(stars, 0.001), GridUnitType.Star);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
