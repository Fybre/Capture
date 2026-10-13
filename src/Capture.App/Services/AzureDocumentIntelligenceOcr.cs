using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Capture.Core.Lattice;
using Capture.Core.Watch;
using Capture.Pdf;

namespace Capture.App.Services;

/// <summary>External OCR through Azure AI Document Intelligence's Read model, which also reads
/// handwriting. Analysis is asynchronous: the page is submitted, then the operation it returns is polled
/// until it finishes. Rate limits (429) are waited out as Azure asks.</summary>
public sealed class AzureDocumentIntelligenceOcr : IExternalOcrEngine
{
    private const string Model = "prebuilt-read";
    // Resources from before the "documentintelligence" API path still answer on the older one.
    private const string LegacyApiVersion = "2023-07-31";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(2);
    private const int MaxRateLimitRetries = 6;

    private readonly IWatchSettingsStore _settings;
    private readonly HttpClient _http;

    // Capture OCRs every page at once; keep Azure to a few at a time so a long scan doesn't trip the
    // resource's rate limit on every page.
    private readonly SemaphoreSlim _gate = new(4);

    public AzureDocumentIntelligenceOcr(IWatchSettingsStore settings, HttpClient http)
    {
        _settings = settings;
        _http = http;
    }

    public async Task<OcrResult> RecognizeAsync(Guid? connectionId, string imagePath, CancellationToken cancellationToken = default)
    {
        var settings = await _settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var connection = settings.FindConnection(connectionId, ConnectionType.AzureDocumentIntelligence)
            ?? throw new InvalidOperationException("there's no Azure Document Intelligence connection — add one in Settings ▸ Connections");
        if (!connection.IsConfigured)
            throw new InvalidOperationException($"the connection \"{connection.Name}\" isn't fully set up");

        var image = PageImageEncoder.ToJpegBytes(imagePath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = await SubmitAsync(connection, image, $"documentintelligence/documentModels/{Model}:analyze?api-version={ConnectionRequests.AzureApiVersion}", cancellationToken).ConfigureAwait(false)
                ?? await SubmitAsync(connection, image, $"formrecognizer/documentModels/{Model}:analyze?api-version={LegacyApiVersion}", cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("this endpoint doesn't offer Document Intelligence (404)");
            using var result = await PollAsync(connection, operation, cancellationToken).ConfigureAwait(false);
            return Read(result.RootElement);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Returns the operation URL to poll, or null when this API path doesn't exist on the resource.
    private async Task<string?> SubmitAsync(ConnectionDefinition connection, byte[] image, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = ConnectionRequests.Create(connection, HttpMethod.Post, path);
            request.Content = new ByteArrayContent(image);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;
            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxRateLimitRetries)
            {
                await Task.Delay(RetryAfter(response, TimeSpan.FromSeconds(2)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw await FailureAsync(response, cancellationToken).ConfigureAwait(false);
            return response.Headers.TryGetValues("Operation-Location", out var values) && values.FirstOrDefault() is { Length: > 0 } location
                ? location
                : throw new InvalidOperationException("Azure accepted the page but didn't say where to collect the result");
        }
    }

    private async Task<JsonDocument> PollAsync(ConnectionDefinition connection, string operation, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var rateLimited = 0;
        while (true)
        {
            using var request = ConnectionRequests.Create(connection, HttpMethod.Get, operation);
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && rateLimited++ < MaxRateLimitRetries)
            {
                await Task.Delay(RetryAfter(response, TimeSpan.FromSeconds(2)), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
                throw await FailureAsync(response, cancellationToken).ConfigureAwait(false);

            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            var status = json.RootElement.TryGetProperty("status", out var element) ? element.GetString() : null;
            if (string.Equals(status, "succeeded", StringComparison.OrdinalIgnoreCase))
                return json;
            var error = string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ? ErrorMessage(json.RootElement) : null;
            json.Dispose();
            if (status is not null && string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Azure couldn't read the page{(error is null ? string.Empty : ": " + error)}");
            if (timer.Elapsed > PollTimeout)
                throw new TimeoutException("Azure didn't finish reading the page within 2 minutes");

            await Task.Delay(RetryAfter(response, TimeSpan.FromSeconds(1)), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Converts Azure's first page into OCR words: each word's polygon becomes its bounding box,
    /// and confidence goes from 0–1 to 0–100. Positions are kept in Azure's unit, scaled up when that's
    /// inches so the whole-number page size keeps its precision.</summary>
    internal static OcrResult Read(JsonElement root)
    {
        if (!root.TryGetProperty("analyzeResult", out var analyze)
            || !analyze.TryGetProperty("pages", out var pages)
            || pages.ValueKind != JsonValueKind.Array
            || pages.GetArrayLength() == 0)
        {
            return new OcrResult();
        }

        var page = pages[0];
        var unit = page.TryGetProperty("unit", out var unitElement) ? unitElement.GetString() : "pixel";
        var scale = string.Equals(unit, "pixel", StringComparison.OrdinalIgnoreCase) ? 1f : 1000f;
        var width = page.TryGetProperty("width", out var widthElement) ? widthElement.GetSingle() * scale : 0;
        var height = page.TryGetProperty("height", out var heightElement) ? heightElement.GetSingle() * scale : 0;

        var words = new List<OcrWord>();
        if (page.TryGetProperty("words", out var wordElements) && wordElements.ValueKind == JsonValueKind.Array)
        {
            foreach (var word in wordElements.EnumerateArray())
            {
                var text = word.TryGetProperty("content", out var content) ? content.GetString() : null;
                if (string.IsNullOrWhiteSpace(text) || !word.TryGetProperty("polygon", out var polygon) || polygon.ValueKind != JsonValueKind.Array)
                    continue;
                var points = polygon.EnumerateArray().Select(value => value.GetSingle() * scale).ToList();
                if (points.Count < 4)
                    continue;
                var xs = points.Where((_, index) => index % 2 == 0).ToList();
                var ys = points.Where((_, index) => index % 2 == 1).ToList();
                words.Add(new OcrWord
                {
                    Text = text,
                    Confidence = word.TryGetProperty("confidence", out var confidence) ? confidence.GetSingle() * 100 : 0,
                    X = xs.Min(),
                    Y = ys.Min(),
                    Width = xs.Max() - xs.Min(),
                    Height = ys.Max() - ys.Min()
                });
            }
        }

        return new OcrResult { Width = (int)Math.Round(width), Height = (int)Math.Round(height), Words = words };
    }

    private static TimeSpan RetryAfter(HttpResponseMessage response, TimeSpan fallback)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (response.Headers.TryGetValues("retry-after-ms", out var ms) && int.TryParse(ms.FirstOrDefault(), out var milliseconds))
            return TimeSpan.FromMilliseconds(milliseconds);
        return fallback;
    }

    private static async Task<Exception> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        string? detail = null;
        try
        {
            using var json = JsonDocument.Parse(body);
            detail = ErrorMessage(json.RootElement);
        }
        catch (JsonException)
        {
        }

        var status = (int)response.StatusCode;
        return new InvalidOperationException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            ? $"Azure refused the key ({status})"
            : $"Azure returned {status}{(detail is null ? string.Empty : ": " + detail)}");
    }

    private static string? ErrorMessage(JsonElement root) =>
        root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            ? (error.TryGetProperty("innererror", out var inner) && inner.TryGetProperty("message", out var innerMessage)
                ? innerMessage.GetString()
                : error.TryGetProperty("message", out var message) ? message.GetString() : null)
            : null;
}
