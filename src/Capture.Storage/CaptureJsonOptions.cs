using System.Text.Json;
using System.Text.Json.Serialization;

namespace Capture.Storage;

/// <summary>The JSON shape used for on-disk profile/settings persistence — camelCase, indented, string
/// enums. Also used for user-facing export/import files, so an exported file is byte-for-byte the same
/// shape the corresponding store already reads from disk.</summary>
public static class CaptureJsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
