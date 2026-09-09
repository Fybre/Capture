using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Capture.App.Services;

/// <summary>Compares the running build's version against Fybre/Capture's latest GitHub release.
/// Uses the unauthenticated GitHub REST API — no token, since this only reads one public release
/// and unauthenticated requests get 60/hour per IP, far more than "once per app launch" needs.</summary>
public sealed class GitHubUpdateCheckService : IUpdateCheckService
{
    private const string LatestReleaseApiUrl = "https://api.github.com/repos/Fybre/Capture/releases/latest";

    private readonly HttpClient _http;

    public GitHubUpdateCheckService(HttpClient http)
    {
        _http = http;
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApiUrl);
            // GitHub's REST API rejects requests with no User-Agent header (403).
            request.Headers.UserAgent.ParseAdd("Capture-App-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return NoUpdate;

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() : null;
            var releaseUrl = root.TryGetProperty("html_url", out var urlProp) ? urlProp.GetString() : null;
            var latest = ParseVersion(tag);
            var current = ParseVersion(GetCurrentVersion());

            if (latest is null || current is null || latest <= current)
                return NoUpdate;

            return new UpdateCheckResult(true, tag!.TrimStart('v', 'V'), releaseUrl);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Trace.TraceWarning($"Update check failed: {ex.Message}");
            return NoUpdate;
        }
    }

    private static readonly UpdateCheckResult NoUpdate = new(false, null, null);

    private static string? GetCurrentVersion()
    {
        var assembly = typeof(GitHubUpdateCheckService).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return informationalVersion?.Split('+', 2)[0] ?? assembly.GetName().Version?.ToString(3);
    }

    // Release tags look like "v0.1.1"; local/dev builds report "0.1.0-ci.4". Strip both the leading
    // "v" and any prerelease suffix before handing off to System.Version, which understands neither.
    private static Version? ParseVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.TrimStart('v', 'V').Split('-', 2)[0];
        return Version.TryParse(trimmed, out var version) ? version : null;
    }
}
