using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Capture.Core.Indexing;

namespace Capture.App.Services;

/// <summary>Sends chat completions to an OpenAI-compatible service. Asks for deterministic JSON
/// (temperature 0, JSON response format), and when a model refuses either — reasoning models such as
/// GPT-5 and o-series only allow the default temperature — retries without it and remembers that for
/// the model. No token limit is sent: reasoning models count their thinking against it and take a
/// different parameter name.</summary>
public static class OpenAiChatClient
{
    public sealed record Reply(int? Status, string? Content, string? Model, string? Error);

    private static readonly ConcurrentDictionary<string, Unsupported> Learned = new(StringComparer.OrdinalIgnoreCase);

    [Flags]
    private enum Unsupported { None = 0, Temperature = 1, ResponseFormat = 2 }

    public static async Task<Reply> SendAsync(
        HttpClient http, string endpoint, string? apiKey, string model, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        var url = OpenAiEndpoints.CompletionsUrl(endpoint);
        var key = url + "|" + model;
        var unsupported = Learned.GetValueOrDefault(key);
        while (true)
        {
            var payload = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages };
            if (!unsupported.HasFlag(Unsupported.Temperature))
                payload["temperature"] = 0;
            if (!unsupported.HasFlag(Unsupported.ResponseFormat))
                payload["response_format"] = new { type = "json_object" };

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            OpenAiEndpoints.Authorize(request, endpoint, apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                var detail = OpenAiExtractor.ErrorDetail(body);
                var retryWithout = status == 400 ? Refused(detail) & ~unsupported : Unsupported.None;
                if (retryWithout != Unsupported.None)
                {
                    unsupported |= retryWithout;
                    Learned[key] = unsupported;
                    continue;
                }

                return new Reply(status, null, null, $"The service returned {status} {response.ReasonPhrase}{(detail.Length > 0 ? detail : Snippet(body))}");
            }

            try
            {
                using var json = JsonDocument.Parse(body);
                var root = json.RootElement;
                var answeredModel = root.TryGetProperty("model", out var modelElement) && modelElement.ValueKind == JsonValueKind.String
                    ? modelElement.GetString()
                    : null;
                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
                    return new Reply(status, null, answeredModel, "The service answered, but not with a chat completion — check the endpoint address.");
                var content = choices.GetArrayLength() > 0
                              && choices[0].TryGetProperty("message", out var message)
                              && message.TryGetProperty("content", out var text)
                              && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? string.Empty
                    : string.Empty;
                return new Reply(status, content, answeredModel, null);
            }
            catch (JsonException)
            {
                return new Reply(status, null, null, "The service answered with something that isn't JSON — check the endpoint address.");
            }
        }
    }

    /// <summary>The content of a successful reply, or an exception with the service's error.</summary>
    public static async Task<string> CompleteAsync(
        HttpClient http, string endpoint, string? apiKey, string model, IReadOnlyList<object> messages, CancellationToken cancellationToken)
    {
        var reply = await SendAsync(http, endpoint, apiKey, model, messages, cancellationToken).ConfigureAwait(false);
        return reply.Error is { } error
            ? throw new InvalidOperationException(char.ToLowerInvariant(error[0]) + error[1..])
            : reply.Content ?? string.Empty;
    }

    // "Unsupported value: 'temperature' does not support 0…", "Unsupported parameter: 'response_format'…"
    private static Unsupported Refused(string detail)
    {
        var result = Unsupported.None;
        if (detail.Contains("temperature", StringComparison.OrdinalIgnoreCase))
            result |= Unsupported.Temperature;
        if (detail.Contains("response_format", StringComparison.OrdinalIgnoreCase))
            result |= Unsupported.ResponseFormat;
        return result;
    }

    private static string Snippet(string body)
    {
        var text = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0 || text.StartsWith('<'))
            return string.Empty;
        return " — " + (text.Length > 200 ? text[..200] + "…" : text);
    }
}
