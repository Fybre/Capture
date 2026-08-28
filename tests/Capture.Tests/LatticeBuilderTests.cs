using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Ocr;
using Capture.Pdf;
using Capture.Storage;

namespace Capture.Tests;

public class LatticeBuilderTests
{
    [Fact]
    public async Task Raster_page_uses_ocr_and_vector_page_uses_pdf_text()
    {
        var sample = "/Users/craig/Downloads/18.09.2026.SCM.PO2089 SCA Payment August 1st shipment.pdf";
        if (!File.Exists(sample) || TesseractCliOcrEngine.ResolveExecutable() is null)
            return;

        var root = Path.Combine(Path.GetTempPath(), "capture-lattice-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var latticeStore = new JsonLatticeStore(paths);
        var rasterizer = new PdfiumRasterizer();
        var builder = new LatticeBuilder(
            paths,
            latticeStore,
            new PdfPigTextExtractor(),
            new TesseractCliOcrEngine(),
            rasterizer);

        var document = new CaptureDocument
        {
            OriginalFileName = Path.GetFileName(sample),
            StoredPath = sample,
            Source = DocumentSource.Import
        };

        var previewDir = paths.DocumentPagesDirectory(document.Id);
        Directory.CreateDirectory(previewDir);
        await rasterizer.RasterizePageAsync(sample, 1, Path.Combine(previewDir, "0001.png"), 150);
        await rasterizer.RasterizePageAsync(sample, 2, Path.Combine(previewDir, "0002.png"), 150);

        var page1 = new DocumentPage
        {
            DocumentId = document.Id,
            PageNumber = 1,
            SourcePageNumber = 1,
            ImagePath = Path.Combine(previewDir, "0001.png"),
            Width = 100,
            Height = 100,
            Dpi = 150
        };
        var page2 = new DocumentPage
        {
            DocumentId = document.Id,
            PageNumber = 2,
            SourcePageNumber = 2,
            ImagePath = Path.Combine(previewDir, "0002.png"),
            Width = 100,
            Height = 100,
            Dpi = 150
        };

        var lattice1 = await builder.BuildPageAsync(document, page1);
        Assert.Equal(LatticeSource.Ocr, lattice1.Source);
        Assert.Contains(lattice1.Words, word =>
            word.Text.Contains("Payment", StringComparison.OrdinalIgnoreCase)
            || word.Text.Contains("METAL", StringComparison.OrdinalIgnoreCase)
            || word.Text.Contains("Slip", StringComparison.OrdinalIgnoreCase));

        var lattice2 = await builder.BuildPageAsync(document, page2);
        Assert.Equal(LatticeSource.PdfText, lattice2.Source);
        Assert.Contains(lattice2.Words, word => word.Text.Contains("INVOICE", StringComparison.OrdinalIgnoreCase));
    }
}
