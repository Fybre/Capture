using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Capture.Core.Indexing;
using Capture.Core.Profiles;
using Capture.Core.Watch;

namespace Capture.App.Services;

public sealed class OpenAiExtractor : IAiExtractor
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IWatchSettingsStore _settings;
    private readonly HttpClient _http;

    public OpenAiExtractor(IWatchSettingsStore settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
    }

    public bool IsConfigured => Task.Run(() => _settings.LoadAsync()).GetAwaiter().GetResult().AiConfigured;

    public async Task<IReadOnlyDictionary<Guid, AiExtractedValue>> ExtractAsync(
        string documentText,
        IReadOnlyList<IndexField> fields,
        CancellationToken cancellationToken = default)
    {
        if (fields.Count == 0)
            return new Dictionary<Guid, AiExtractedValue>();

        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.AiConfigured)
            return new Dictionary<Guid, AiExtractedValue>();

        var content = await OpenAiChatClient.CompleteAsync(
            _http,
            settings.AiEndpoint!,
            settings.AiApiKey,
            string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel,
            [
                new { role = "system", content = AiExtractPrompt.SystemMessage() },
                new { role = "user", content = AiExtractPrompt.UserMessage(documentText, fields, settings.AiMaxDocumentChars) }
            ],
            cancellationToken).ConfigureAwait(false);
        return AiExtractPrompt.Parse(content, fields);
    }

    // OpenAI-style errors carry {"error":{"message":"..."}}; anything else is left out rather than
    // putting a page of HTML into a field's error.
    internal static string ErrorDetail(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.GetString() is { Length: > 0 } text)
            {
                return " — " + (text.Length > 200 ? text[..200] + "…" : text);
            }
        }
        catch (JsonException)
        {
        }

        return string.Empty;
    }
}
