using Capture.Core.Import;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Store;
using Capture.Pdf;
using Capture.Storage;

namespace Capture.Tests;

public class PdfRasterizerTests
{
    [Fact]
    public async Task Import_sample_pdf_creates_page_images()
    {
        var sample = FindSamplePdf();
        if (sample is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), "capture-pdf-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        IDocumentStore store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var importer = new DocumentImporter(paths, store, new PdfiumRasterizer(), new SkiaImagePageImporter(), new NoOpLatticeBuilder(), new PdfPigSubsetWriter());
        var document = await importer.ImportFileAsync(sample, DocumentSource.Import);

        Assert.True(document.Status == DocumentStatus.NeedsReview, document.ErrorMessage);
        Assert.True(document.PageCount >= 1);
        var pages = await store.GetPagesAsync(document.Id);
        Assert.Equal(document.PageCount, pages.Count);
        Assert.All(pages, page => Assert.True(File.Exists(page.ImagePath)));
    }

    private static string? FindSamplePdf()
    {
        var candidates = new[]
        {
            "/Users/craig/Downloads/Sample SCDQ PO.pdf",
            "/Users/craig/Downloads/18.09.2026.SCM.PO2089 SCA Payment August 1st shipment.pdf"
        };

        return candidates.FirstOrDefault(File.Exists);
    }
}
