using System.Text;
using System.Text.Json;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public sealed record AiExtractedValue(string Value, float Confidence);

public static class AiExtractPrompt
{
    public const int MaxDocumentChars = 80_000;

    public static string SystemMessage() =>
        "You extract structured index fields from business documents. "
        + "Return JSON only. Use an object named values whose keys are the field ids. "
        + "Each value is {\"value\":\"...\",\"confidence\":0-100}. "
        + "If a field is not present, use an empty value and confidence 0. "
        + "Do not invent values. Keep dates and money as they appear.";

    public static string UserMessage(string documentText, IReadOnlyList<IndexField> fields, int maxDocumentChars = MaxDocumentChars)
    {
        if (maxDocumentChars <= 0)
            maxDocumentChars = MaxDocumentChars;

        var text = documentText.Length > maxDocumentChars
            ? documentText[..maxDocumentChars] + "\n[truncated]"
            : documentText;
        var builder = new StringBuilder();
        builder.AppendLine("Extract these fields:");
        foreach (var field in fields)
        {
            var type = AiFieldCatalog.Find(field.AiTypeId);
            builder.Append("- id=").Append(field.Id.ToString("N"));
            builder.Append(" name=\"").Append(field.Name).Append('"');
            builder.Append(" format=").Append(field.Format);
            if (type is not null && !string.IsNullOrWhiteSpace(type.Hint))
                builder.Append(" meaning=\"").Append(type.Hint).Append('"');
            if (!string.IsNullOrWhiteSpace(field.AiPrompt))
                builder.Append(" extra=\"").Append(field.AiPrompt.Trim()).Append('"');
            else if (type is null || string.IsNullOrWhiteSpace(type.Hint))
                builder.Append(" meaning=\"").Append(field.Name).Append('"');
            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine("Document text:");
        builder.Append(text);
        return builder.ToString();
    }

    public static IReadOnlyDictionary<Guid, AiExtractedValue> Parse(string json, IReadOnlyList<IndexField> fields)
    {
        var results = new Dictionary<Guid, AiExtractedValue>();
        json = StripFence(json);
        if (string.IsNullOrWhiteSpace(json))
            return results;

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("values", out var values))
                ReadObject(values, fields, results);
            else
                ReadObject(root, fields, results);
        }
        catch (JsonException)
        {
        }

        return results;
    }

    private static void ReadObject(
        JsonElement element,
        IReadOnlyList<IndexField> fields,
        Dictionary<Guid, AiExtractedValue> results)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in element.EnumerateObject())
        {
            var field = Match(fields, property.Name);
            if (field is null)
                continue;
            results[field.Id] = ReadValue(property.Value);
        }
    }

    private static AiExtractedValue ReadValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return new AiExtractedValue(element.GetString() ?? string.Empty, 80);

        var value = element.TryGetProperty("value", out var text) ? text.GetString() ?? string.Empty : string.Empty;
        var confidence = 80f;
        if (element.TryGetProperty("confidence", out var score) && score.TryGetSingle(out var parsed))
            confidence = Math.Clamp(parsed, 0, 100);
        return new AiExtractedValue(value.Trim(), confidence);
    }

    private static IndexField? Match(IReadOnlyList<IndexField> fields, string key)
    {
        if (Guid.TryParse(key, out var id))
            return fields.FirstOrDefault(field => field.Id == id);
        return fields.FirstOrDefault(field => string.Equals(field.Name, key, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripFence(string json)
    {
        json = json.Trim();
        if (!json.StartsWith("```", StringComparison.Ordinal))
            return json;
        var start = json.IndexOf('\n');
        var end = json.LastIndexOf("```", StringComparison.Ordinal);
        if (start < 0 || end <= start)
            return json;
        return json[(start + 1)..end].Trim();
    }
}
