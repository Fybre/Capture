using System.Diagnostics;
using System.Text.Json;

namespace Capture.Scanner;

/// <summary>Talks to the bundled CaptureScanHelperMac native helper (see native/CaptureScanHelperMac in
/// the repo) for real scanner access on macOS — there is no public .NET binding for Apple's
/// ImageCaptureCore framework, so this launches the helper as a subprocess per operation and reads its
/// JSON-on-stdout result, the same bundled-native-helper pattern already used for the Presidio
/// sidecar.</summary>
public sealed class MacScanSource : IScanSource
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public bool IsAvailable => OperatingSystem.IsMacOS() && File.Exists(HelperExecutablePath());

    public async Task<IReadOnlyList<ScanDevice>> ListDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            return [];

        var (exitCode, stdout, stderr) = await RunAsync(["list-devices"], cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
            throw new InvalidOperationException($"Listing scanners failed: {DescribeError(stdout, stderr)}");

        var raw = JsonSerializer.Deserialize<List<RawDevice>>(stdout, Json) ?? [];
        return raw.Select(device => new ScanDevice(
            device.Id ?? "",
            device.Name ?? "Unknown scanner",
            device.SupportedDpis,
            device.SupportsFlatbed,
            device.SupportsFeeder,
            device.SupportsDuplex,
            device.SupportsColor,
            device.SupportsGrayscale)).ToList();
    }

    public async IAsyncEnumerable<ScannedPage> ScanAsync(
        ScanOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException("The macOS scan helper is not available.");

        var outputPath = Path.Combine(Path.GetTempPath(), $"capture-scan-{Guid.NewGuid():N}.png");
        var transferredPaths = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var colorArgument = options.ColorMode == ScanColorMode.Grayscale ? "gray" : "color";
            var sourceArgument = options.Source == ScanSourceKind.Feeder ? "feeder" : "flatbed";
            var duplexArgument = options.Duplex ? "duplex" : "simplex";
            var (exitCode, stdout, stderr) = await RunAsync(
                ["scan", options.DeviceId, options.Dpi.ToString(), outputPath, colorArgument, sourceArgument, duplexArgument],
                cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
                throw new InvalidOperationException($"Scan failed: {DescribeError(stdout, stderr)}");

            var result = JsonSerializer.Deserialize<ScanResult>(stdout, Json)
                ?? throw new InvalidOperationException("The scan helper returned no result.");
            foreach (var page in result.Pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                transferredPaths.Add(page.Path); // ownership transfers with the yield
                yield return new ScannedPage(page.Path, page.Width, page.Height, page.Dpi);
            }
        }
        finally
        {
            // Once the helper completes successfully, yielded files belong to the caller. On any
            // earlier failure/cancellation, reclaim all files created under this job's unique prefix.
            // Delete failed or unconsumed pages, but not files already handed to the caller.
            var directory = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
            var prefix = Path.GetFileNameWithoutExtension(outputPath);
            foreach (var path in Directory.EnumerateFiles(directory, prefix + "*.png"))
            {
                if (transferredPaths.Contains(path))
                    continue;
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }
    }

    private static string DescribeError(string stdout, string stderr)
    {
        try
        {
            var error = JsonSerializer.Deserialize<RawError>(stdout, Json);
            if (!string.IsNullOrWhiteSpace(error?.Error))
                return error.Error;
        }
        catch (JsonException)
        {
            // Fall through to raw stderr/stdout below.
        }

        return string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo(HelperExecutablePath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start the macOS scan helper.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        return (process.ExitCode, (await stdoutTask.ConfigureAwait(false)).Trim(), (await stderrTask.ConfigureAwait(false)).Trim());
    }

    private static string HelperExecutablePath() =>
        Path.Combine(AppContext.BaseDirectory, "CaptureScanHelperMac.app", "Contents", "MacOS", "CaptureScanHelperMac");

    private sealed class RawDevice
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<int> SupportedDpis { get; set; } = [];
        public bool SupportsFlatbed { get; set; } = true;
        public bool SupportsFeeder { get; set; }
        public bool SupportsDuplex { get; set; }
        public bool SupportsColor { get; set; } = true;
        public bool SupportsGrayscale { get; set; } = true;
    }

    private sealed class RawError
    {
        public string? Error { get; set; }
    }

    private sealed class ScanResult
    {
        public List<ScanPageResult> Pages { get; set; } = [];
    }

    private sealed class ScanPageResult
    {
        public string Path { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public int Dpi { get; set; }
    }
}
