using System.Text.RegularExpressions;

namespace Capture.Core.Profiles;

public static class BarcodePatterns
{
    /// <summary>The symbologies <c>ZxingBarcodeDecoder</c> is configured to scan for — shared with UI so a
    /// barcode-format picker (e.g. a batch profile's optional format filter) stays in sync with what the
    /// decoder can actually return.</summary>
    public static readonly IReadOnlyList<string> KnownFormats =
    [
        "QR_CODE", "CODE_128", "CODE_39", "CODE_93", "EAN_13", "EAN_8",
        "UPC_A", "UPC_E", "ITF", "CODABAR", "DATA_MATRIX", "PDF_417", "AZTEC"
    ];

    public static string DisplayType(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            return string.Empty;
        return format.Replace('_', ' ');
    }

    public static bool Matches(IndexField field, string text) => Matches(field.ValuePattern, text);

    public static bool Matches(string? pattern, string text)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return true;

        try
        {
            return Regex.IsMatch(text, pattern);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
