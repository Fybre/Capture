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

        var url = OpenAiEndpoints.CompletionsUrl(settings.AiEndpoint!);
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AiApiKey);
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(settings.AiModel) ? "gpt-4o-mini" : settings.AiModel,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = AiExtractPrompt.SystemMessage() },
                new { role = "user", content = AiExtractPrompt.UserMessage(documentText, fields, settings.AiMaxDocumentChars) }
            }
        };
        request.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI extract failed ({(int)response.StatusCode}).");

        var parsed = JsonSerializer.Deserialize<ChatResponse>(body, Json);
        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content ?? string.Empty;
        return AiExtractPrompt.Parse(content, fields);
    }

    private sealed class ChatResponse
    {
        public List<Choice>? Choices { get; set; }
    }

    private sealed class Choice
    {
        public ChatMessage? Message { get; set; }
    }

    private sealed class ChatMessage
    {
        public string? Content { get; set; }
    }
}
