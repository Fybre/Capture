using System.Globalization;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public static class IndexFormat
{
    public static string? Validate(string? value, FieldFormat format, string? locale)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var culture = Culture(locale);
        return format switch
        {
            FieldFormat.Integer => long.TryParse(value, NumberStyles.Integer, culture, out _)
                ? null
                : "Not an integer",
            FieldFormat.Money => decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, culture, out _)
                ? null
                : "Not a money amount",
            FieldFormat.Date => DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out _)
                ? null
                : "Not a date",
            FieldFormat.DateTime => DateTime.TryParse(value, culture, DateTimeStyles.AllowWhiteSpaces, out _)
                ? null
                : "Not a date/time",
            FieldFormat.Boolean => TryParseBoolean(value, out _)
                ? null
                : "Not yes/no",
            _ => null
        };
    }

    public static DocumentStatus StatusFor(IEnumerable<IndexValue> values, int threshold)
    {
        foreach (var value in values)
        {
            if (value.HideFromIndexing || value.IsConditionInactive)
                continue;
            // A read-only value cannot be corrected in the review editor, but a format/configuration
            // error must still surface as NeedsReview instead of silently marking the document Ready.
            if (value.ValidationError is not null)
                return DocumentStatus.NeedsReview;
            if (value.IsReadOnly)
                continue;
            if (value.IsMissing || value.IsLowConfidence(threshold))
                return DocumentStatus.NeedsReview;
        }

        return DocumentStatus.Ready;
    }

    private static CultureInfo Culture(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale))
            return CultureInfo.CurrentCulture;

        try
        {
            return CultureInfo.GetCultureInfo(locale);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentCulture;
        }
    }

    /// <summary>The one place a Boolean-format value's text is interpreted: true/false, yes/no, 1/0
    /// (case-insensitive, surrounding whitespace ignored). Anything else, including blank, is not a
    /// yes/no value.</summary>
    public static bool TryParseBoolean(string? value, out bool result)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1":
                result = true;
                return true;
            case "false" or "no" or "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
