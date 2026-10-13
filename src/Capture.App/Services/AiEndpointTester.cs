using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Capture.Core.Indexing;

namespace Capture.App.Services;

/// <summary>One small request to the configured AI service, the same shape extraction sends, so
/// Settings can say whether the endpoint, key and model work — and if not, what the service said and
/// which address was called.</summary>
public static class AiEndpointTester
{
    // A 1×1 white PNG: enough to find out whether the model accepts images at all.
    private const string TinyPng =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8/5+hHgAHggJ/PchI7wAAAABJRU5ErkJggg==";

    public sealed record Result(bool Success, string Message);

    public static async Task<Result> TestAsync(
        HttpClient http, string endpoint, string apiKey, string model, bool checkImages, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || !Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out _))
            return new Result(false, "Enter the endpoint address, e.g. https://api.openai.com/v1.");
        if (string.IsNullOrWhiteSpace(apiKey))
            return new Result(false, "Enter the API key.");

        var url = OpenAiEndpoints.CompletionsUrl(endpoint);
        var auth = OpenAiEndpoints.UsesApiKeyHeader(endpoint) ? "api-key header" : "Bearer key";
        var stopwatch = Stopwatch.StartNew();
        var text = await SendAsync(http, endpoint, apiKey, model,
            "Reply with this JSON and nothing else: {\"ok\": true}", null, cancellationToken).ConfigureAwait(false);
        if (text.Error is { } error)
            return new Result(false, $"{error}\n\nCalled {Redact(url)} with model \"{model}\" ({auth}).{Hint(text.Status, error, endpoint)}");

        var seconds = stopwatch.Elapsed.TotalSeconds;
        var message = new StringBuilder($"Connected — \"{text.Model ?? model}\" answered in {seconds:0.0} s.");
        if (checkImages)
        {
            var image = await SendAsync(http, endpoint, apiKey, model,
                "Reply with this JSON and nothing else: {\"ok\": true}", TinyPng, cancellationToken).ConfigureAwait(false);
            message.Append(image.Error is null
                ? " It accepts page images, so AI separation can send them."
                : $" But it refused a page image ({image.Error}) — turn off \"Send page images\" below, or use a model that can see images.");
        }

        return new Result(true, message.ToString());
    }

    private static async Task<OpenAiChatClient.Reply> SendAsync(
        HttpClient http, string endpoint, string apiKey, string model, string prompt, string? image, CancellationToken cancellationToken)
    {
        object content = image is null
            ? prompt
            : new object[] { new { type = "text", text = prompt }, new { type = "image_url", image_url = new { url = image } } };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            return await OpenAiChatClient.SendAsync(http, endpoint, apiKey, model,
                [new { role = "system", content = "You answer in JSON." }, new { role = "user", content }], timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OpenAiChatClient.Reply(null, null, null, "No answer within 60 seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new OpenAiChatClient.Reply(null, null, null, $"Couldn't reach the service: {ex.Message}");
        }
    }

    private static string Hint(int? status, string error, string endpoint)
    {
        var azure = OpenAiEndpoints.UsesApiKeyHeader(endpoint);
        return status switch
        {
            401 or 403 => azure
                ? "\n\nCheck the key: use Key 1 or Key 2 from the resource's Keys and Endpoint page (Azure portal) or the Foundry project overview."
                : "\n\nCheck the API key.",
            404 when azure => "\n\nFor Azure, Model must be your deployment name (Foundry ▸ Models + endpoints), not the model family. Endpoint can be the resource address, e.g. https://my-resource.openai.azure.com or https://my-resource.services.ai.azure.com.",
            404 => "\n\nCheck the endpoint address and the model name.",
            400 when error.Contains("response_format", StringComparison.OrdinalIgnoreCase)
                => "\n\nThis model doesn't support JSON answers (response_format); choose a newer model.",
            429 => "\n\nThe service is rate limiting or out of quota — check the deployment's quota.",
            _ => string.Empty
        };
    }

    private static string Redact(string url)
    {
        var at = url.IndexOf('?');
        return at < 0 ? url : url[..at] + "?" + string.Join('&', url[(at + 1)..].Split('&')
            .Where(part => part.StartsWith("api-version=", StringComparison.OrdinalIgnoreCase)));
    }
}
