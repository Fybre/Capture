using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

/// <summary>Keeps what a reviewer typed when a document is re-extracted under another document type:
/// a manually entered value moves to the new type's field of the same name, and everything else is
/// what the new type extracted.</summary>
public static class ManualValueCarryOver
{
    public static void Apply(IReadOnlyList<IndexValue> extracted, IReadOnlyList<IndexValue> previous, string? locale)
    {
        var manual = previous
            .Where(value => value.IsManual && !string.IsNullOrWhiteSpace(value.Value))
            .GroupBy(value => value.FieldName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var value in extracted)
        {
            if (value.IsReadOnly || !manual.TryGetValue(value.FieldName.Trim(), out var typed))
                continue;
            value.Value = typed.Value;
            value.Confidence = 100;
            value.IsManual = true;
            value.PageNumber = typed.PageNumber;
            value.Bounds = typed.Bounds;
            value.ValidationError = IndexFormat.Validate(value.Value, value.Format, locale);
        }
    }
}
