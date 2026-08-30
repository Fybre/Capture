using System.Text;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Export;

/// <summary>Resolves a per-document export filename pattern and makes the result safe to use as a
/// filename on all supported platforms. The returned value is a base name; the writer adds the
/// appropriate extension.</summary>
public static class ExportFileNameTemplate
{
    private static readonly HashSet<char> PortableInvalidCharacters =
        [.. Path.GetInvalidFileNameChars(), '<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Resolve(
        string? pattern,
        CaptureDocument document,
        string? profileName,
        IReadOnlyList<IndexField> profileFields,
        IReadOnlyList<IndexValue> indexValues,
        DateTimeOffset timestamp)
    {
        var valuesById = indexValues.ToDictionary(value => value.FieldId, value => value.Value);
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in profileFields)
            tokens[field.Name] = valuesById.GetValueOrDefault(field.Id, string.Empty);

        tokens["OriginalFileName"] = Path.GetFileNameWithoutExtension(document.OriginalFileName);
        tokens["DocumentId"] = document.Id.ToString("N");

        var resolved = DefaultValueTemplateEvaluator.Evaluate(pattern, new DefaultValueContext
        {
            Timestamp = timestamp,
            ProfileName = profileName,
            Fields = tokens
        });

        return Sanitize(resolved, document.Id.ToString("N"));
    }

    public static string Sanitize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var result = new StringBuilder(value.Length);
        foreach (var character in value)
            result.Append(char.IsControl(character) || PortableInvalidCharacters.Contains(character) ? '_' : character);

        var sanitized = result.ToString().TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
            return fallback;

        // Windows reserves these device names even when an extension is present. Prefixing rather
        // than replacing keeps the user's resolved value recognizable.
        var nameBeforeFirstDot = sanitized.Split('.', 2)[0];
        if (WindowsReservedNames.Contains(nameBeforeFirstDot))
            sanitized = "_" + sanitized;

        return sanitized;
    }
}
