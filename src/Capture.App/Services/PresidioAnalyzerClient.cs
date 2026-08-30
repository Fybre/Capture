using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capture.Core.Redaction;

namespace Capture.App.Services;

/// <summary>Talks to the bundled Presidio sidecar's `/analyze` endpoint. Presidio reports match offsets
/// as Python Unicode code-point indices; .NET strings index by UTF-16 code unit. These only differ for
/// supplementary-plane characters (most emoji, some rare CJK) appearing before a match, but every
/// offset is converted here before being handed back as a <see cref="PiiMatch"/>, so nothing downstream
/// (the detection step, <c>LatticeText</c>) ever needs to know Presidio's wire format uses code points.</summary>
public sealed class PresidioAnalyzerClient : IPiiDetector
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly PresidioSidecarLauncher _launcher;
    private readonly HttpClient _http;

    public PresidioAnalyzerClient(PresidioSidecarLauncher launcher, HttpClient http)
    {
        _launcher = launcher;
        _http = http;
    }

    public bool IsConfigured => _launcher.IsAvailable;

    public async Task<IReadOnlyList<PiiMatch>> AnalyzeAsync(
        string text,
        IReadOnlyList<string> entities,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var baseUrl = await _launcher.GetBaseUrlAsync(cancellationToken).ConfigureAwait(false);
        if (baseUrl is null)
        {
            // The sidecar exists on disk (IsConfigured checked that) but failed to actually start —
            // throwing here, rather than silently returning no matches, makes RedactionDetectionStep's
            // existing catch surface this as RedactionStatus.Failed instead of a false "no PII found".
            throw new InvalidOperationException(
                "The Presidio sidecar did not start in time. Check Trace output for details, or try again.");
        }

        var payload = new
        {
            text,
            language = string.IsNullOrWhiteSpace(language) ? "en" : language,
            entities = entities.Count == 0 ? null : entities
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/analyze")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Presidio analyze failed ({(int)response.StatusCode}).");

        var raw = JsonSerializer.Deserialize<List<RawMatch>>(body, Json) ?? [];
        var results = new List<PiiMatch>(raw.Count);
        foreach (var match in raw)
        {
            var start = PresidioOffsets.CodePointToUtf16Index(text, match.Start);
            var end = PresidioOffsets.CodePointToUtf16Index(text, match.End);
            results.Add(new PiiMatch(match.EntityType ?? string.Empty, start, end, match.Score));
        }

        return results;
    }

    private sealed class RawMatch
    {
        [JsonPropertyName("entity_type")]
        public string? EntityType { get; set; }
        public int Start { get; set; }
        public int End { get; set; }
        public float Score { get; set; }
    }
}
