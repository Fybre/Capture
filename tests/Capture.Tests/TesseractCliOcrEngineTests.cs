using Capture.Ocr;

namespace Capture.Tests;

public sealed class TesseractCliOcrEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"capture-tesseract-{Guid.NewGuid():N}");

    [Fact]
    public void ResolveExecutable_prefers_configured_then_bundled_then_path_then_well_known()
    {
        var configured = CreateFile("configured/tesseract");
        var bundled = CreateFile($"app/{ExecutableName}");
        var fromPath = CreateFile($"path/{ExecutableName}");
        var wellKnown = CreateFile("well-known/tesseract");
        var appDirectory = Path.GetDirectoryName(bundled)!;
        var pathDirectory = Path.GetDirectoryName(fromPath)!;

        Assert.Equal(configured, Resolve(appDirectory, configured, pathDirectory, wellKnown));

        File.Delete(configured);
        Assert.Equal(bundled, Resolve(appDirectory, configured, pathDirectory, wellKnown));

        File.Delete(bundled);
        Assert.Equal(fromPath, Resolve(appDirectory, configured, pathDirectory, wellKnown));

        File.Delete(fromPath);
        Assert.Equal(wellKnown, Resolve(appDirectory, configured, pathDirectory, wellKnown));
    }

    [Fact]
    public void ResolveTessdataDir_returns_only_an_existing_sibling_directory()
    {
        var executable = CreateFile($"app/{ExecutableName}");

        Assert.Null(TesseractCliOcrEngine.ResolveTessdataDir(executable));

        var tessdata = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(executable)!, "tessdata"));

        Assert.Equal(tessdata.FullName, TesseractCliOcrEngine.ResolveTessdataDir(executable));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static string ExecutableName => OperatingSystem.IsWindows() ? "tesseract.exe" : "tesseract";

    private static string? Resolve(string appDirectory, string configured, string pathDirectory, string wellKnown) =>
        TesseractCliOcrEngine.ResolveExecutable(
            appDirectory,
            configured,
            pathDirectory,
            new[] { wellKnown });

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
