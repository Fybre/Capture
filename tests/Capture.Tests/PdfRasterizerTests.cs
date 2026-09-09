using Capture.Core.Import;
using Capture.Core.Paths;
using Capture.Pdf;

namespace Capture.Tests;

public class PdfRasterizerTests
{
    [Fact]
    public async Task Rasterize_sample_pdf_creates_page_images()
    {
        var sample = FindSamplePdf();
        if (sample is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), "capture-pdf-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var pages = await new PdfiumRasterizer().RasterizeAsync(sample, paths.WorkDirectory, 200);
        Assert.NotEmpty(pages);
        Assert.All(pages, page => Assert.True(File.Exists(page.ImagePath)));
    }

    private static string? FindSamplePdf()
    {
        var sample = Environment.GetEnvironmentVariable("CAPTURE_TEST_SAMPLE_PDF");
        return !string.IsNullOrWhiteSpace(sample) && File.Exists(sample) ? sample : null;
    }
}
