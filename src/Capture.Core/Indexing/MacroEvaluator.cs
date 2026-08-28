using System.Globalization;
using System.Text;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public static class MacroEvaluator
{
    public static string Evaluate(IEnumerable<MacroSegment> segments, MacroContext context)
    {
        var result = new StringBuilder();
        foreach (var segment in segments)
        {
            result.Append(segment.Kind switch
            {
                MacroSegmentKind.Literal => segment.Text ?? string.Empty,
                MacroSegmentKind.DocumentCounter => FormatCounter(context.DocumentNumber, segment.CounterWidth),
                MacroSegmentKind.BatchCounter => FormatCounter(context.BatchNumber, segment.CounterWidth),
                MacroSegmentKind.DateTime => FormatTimestamp(context.Timestamp, segment.Text),
                MacroSegmentKind.Field => ResolveField(segment.Text, context),
                MacroSegmentKind.ProfileName => context.ProfileName ?? string.Empty,
                _ => string.Empty
            });
        }

        return result.ToString();
    }

    private static string FormatCounter(int value, int width)
    {
        return width > 0
            ? value.ToString(new string('0', width), CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

    private static string FormatTimestamp(DateTimeOffset timestamp, string? format)
    {
        var local = timestamp.DateTime;
        return string.IsNullOrWhiteSpace(format)
            ? local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : local.ToString(format, CultureInfo.InvariantCulture);
    }

    private static string ResolveField(string? name, MacroContext context)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        return context.Fields.TryGetValue(name, out var value) ? value : string.Empty;
    }
}
