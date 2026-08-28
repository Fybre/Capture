using System.Diagnostics;
using System.Text.RegularExpressions;
using Capture.Core.Lattice;

namespace Capture.Ocr;

public sealed partial class TesseractCliOcrEngine : IOcrEngine
{
    [GeneratedRegex("^[A-Za-z0-9_+]+$")]
    private static partial Regex LanguageCodePattern();

    public static string? ResolveExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CAPTURE_TESSERACT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var names = OperatingSystem.IsWindows()
            ? new[] { "tesseract.exe", "tesseract" }
            : new[] { "tesseract" };

        foreach (var name in names)
        {
            var fromPath = FindOnPath(name);
            if (fromPath is not null)
                return fromPath;
        }

        var extras = new[]
        {
            "/opt/homebrew/bin/tesseract",
            "/usr/local/bin/tesseract",
            @"C:\Program Files\Tesseract-OCR\tesseract.exe"
        };

        return extras.FirstOrDefault(File.Exists);
    }

    public async Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Image not found.", imagePath);

        var executable = ResolveExecutable()
            ?? throw new InvalidOperationException("Tesseract was not found. Install Tesseract OCR and ensure it is on PATH.");

        var language = Environment.GetEnvironmentVariable("CAPTURE_OCR_LANG");
        if (string.IsNullOrWhiteSpace(language))
            language = "eng";
        else if (!LanguageCodePattern().IsMatch(language))
            throw new InvalidOperationException(
                $"CAPTURE_OCR_LANG value '{language}' is invalid; expected a Tesseract language code (letters, digits, '_' and '+' only).");

        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(imagePath);
        start.ArgumentList.Add("stdout");
        start.ArgumentList.Add("-l");
        start.ArgumentList.Add(language);
        start.ArgumentList.Add("--psm");
        start.ArgumentList.Add("6");
        start.ArgumentList.Add("tsv");

        using var process = new Process { StartInfo = start };
        if (!process.Start())
            throw new InvalidOperationException("Unable to start Tesseract.");

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Tesseract timed out.");
        }

        var stdout = await outputTask.ConfigureAwait(false);
        var stderr = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Tesseract failed ({process.ExitCode}): {stderr}".Trim());

        return TesseractTsvParser.Parse(stdout);
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
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
}
