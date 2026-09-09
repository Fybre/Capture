using System.Diagnostics;

namespace Capture.App.Services;

/// <summary>Owns the bundled Presidio sidecar's child-process lifecycle: locates the executable
/// (dropped next to the app by the Capture.Presidio.Binaries package's build-transitive target),
/// starts it lazily on first use, reads its `READY &lt;port&gt;` stdout line, waits for `/health`, and
/// kills it on app shutdown (via DI singleton disposal — see App.axaml.cs). If the executable isn't
/// present (package not yet referenced, or an unsupported platform), every call here degrades to
/// "unavailable" rather than throwing — Presidio-backed redaction just doesn't run.</summary>
public sealed class PresidioSidecarLauncher : IDisposable
{
    // The very first launch of a freshly-installed copy of this binary can take a genuinely long time
    // before it even prints its READY line — macOS verifying several hundred bundled .dylib/.so files
    // for the first time measured at ~20s alone here, before Python/spaCy/Presidio have done anything.
    // Later launches are fast (OS-level verification results are cached), but the timeout has to
    // tolerate the worst case — a cold start — or a first-run user silently gets no redaction at all.
    private static readonly TimeSpan ReadyLineTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private string? _baseUrl;
    private bool _disposed;

    public bool IsAvailable => ResolveExecutablePath() is not null;

    /// <summary>Fires with a human-readable status while starting the sidecar — a cold start can take
    /// over a minute, so callers (MainViewModel) surface this via StatusText rather than leaving the
    /// user looking at a generic busy spinner with no explanation. May fire on a background thread —
    /// subscribers must marshal to the UI thread themselves.</summary>
    public event Action<string>? StatusChanged;

    private void ReportStatus(string message) => StatusChanged?.Invoke(message);

    /// <summary>Starts the sidecar if it isn't already running and returns its base URL, or null if it
    /// can't be started/isn't bundled. Safe to call repeatedly — once it succeeds the same process is
    /// reused for the rest of the app's lifetime. A failed attempt is *not* cached: it retries fresh on
    /// the next call rather than permanently giving up for the whole session — a slow/failed cold start
    /// (see the timeout comments below) shouldn't require restarting the app to ever work.</summary>
    public async Task<string?> GetBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        if (_baseUrl is not null)
            return _baseUrl;
        if (_disposed)
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_baseUrl is not null)
                return _baseUrl;
            if (_disposed)
                return null;

            var url = await StartAsync(cancellationToken).ConfigureAwait(false);
            if (url is null)
                return null;

            _baseUrl = url;
            return _baseUrl;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string? ResolveExecutablePath()
    {
        var name = OperatingSystem.IsWindows() ? "presidio-sidecar.exe" : "presidio-sidecar";
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    private async Task<string?> StartAsync(CancellationToken cancellationToken)
    {
        var exePath = ResolveExecutablePath();
        if (exePath is null)
            return null;

        ReportStatus("Starting Presidio (first launch can take a minute or two — later ones are fast)…");

        var start = new ProcessStartInfo(exePath)
        {
            ArgumentList = { "--port", "0" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var process = Process.Start(start);
        if (process is null)
        {
            ReportStatus("Presidio sidecar failed to start.");
            return null;
        }

        int port;
        try
        {
            using var readyTimeoutSource = new CancellationTokenSource(ReadyLineTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readyTimeoutSource.Token);
            var line = await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false);
            if (line is null
                || !line.StartsWith("READY ", StringComparison.Ordinal)
                || !int.TryParse(line.AsSpan("READY ".Length), out port))
            {
                ReportStatus("Presidio sidecar failed to start in time.");
                TryKill(process);
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            ReportStatus("Presidio sidecar failed to start in time.");
            TryKill(process);
            return null;
        }

        var baseUrl = $"http://127.0.0.1:{port}";
        _process = process;
        ReportStatus("Presidio is loading its detection model…");

        if (!await WaitForHealthyAsync(baseUrl, cancellationToken).ConfigureAwait(false))
        {
            ReportStatus("Presidio's detection model failed to finish loading in time.");
            TryKill(process);
            _process = null;
            return null;
        }

        ReportStatus("Presidio ready.");
        return baseUrl;
    }

    private static async Task<bool> WaitForHealthyAsync(string baseUrl, CancellationToken cancellationToken)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + ModelLoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await http.GetAsync($"{baseUrl}/health", cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (HttpRequestException)
            {
                // Not listening yet — retry until the deadline.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_process is { } process)
            TryKill(process);

        _gate.Dispose();
    }
}
