using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Pdf;
using Capture.Storage;
using UglyToad.PdfPig.Writer;

namespace Capture.Tests;

public class DocumentImporterBatchBoundaryTests
{
    // Regression coverage for a bug where a batch-trigger page (e.g. a barcode separator) that is
    // ALSO its own single-page document split — because the indexing profile splits every page — got
    // discarded to zero pages and silently dropped the whole BatchTriggerHit, instead of carrying the
    // batch boundary and its captured value forward to the next document.
    [Fact]
    public async Task ImportAsync_carries_batch_boundary_forward_when_the_trigger_page_is_its_own_discarded_split()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-batch-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pdfPath = Path.Combine(root, "source.pdf");
        WriteBlankPdf(pdfPath, pageCount: 6);

        var paths = new AppPaths(Path.Combine(root, "data"));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        // Barcode pages: 1 and 4 (matches the batch-separation-test.pdf layout — barcode, 2 content
        // pages, barcode, 2 content pages).
        var barcodes = new FakeBarcodeDecoder(new Dictionary<int, string>
        {
            [1] = "BATCH-0001",
            [4] = "BATCH-0002"
        });

        var importer = new DocumentImporter(
            paths, store, new PdfiumRasterizer(), new SkiaImagePageImporter(), new NoOpLatticeBuilder(),
            new PdfPigSubsetWriter(), barcodes);

        var indexingProfile = new IndexingProfile { Name = "Splits every page" };

        var importProfile = new ImportProfile
        {
            Name = "Splits every page",
            Strategies = [new SeparationStrategy { Type = SeparationStrategyType.EveryNPages, PageCount = 1 }]
        };

        var batchProfile = new BatchProfile
        {
            Name = "Barcode batches",
            Mode = BatchMode.UseStrategies,
            Strategies = [new SeparationStrategy { Type = SeparationStrategyType.Barcode, DiscardSeparatorPage = true }]
        };

        var results = await importer.ImportAsync(pdfPath, DocumentSource.Import, indexingProfile, batchProfile, importProfile);

        // Barcode pages (1 and 4) are discarded entirely — only the 4 content pages become documents.
        Assert.Equal(4, results.Count);
        Assert.All(results, item => Assert.Equal(1, item.Document.PageCount));

        Assert.True(results[0].StartsNewBatch);
        Assert.Equal("BATCH-0001", results[0].BatchSeparatorValue);

        Assert.False(results[1].StartsNewBatch);

        Assert.True(results[2].StartsNewBatch);
        Assert.Equal("BATCH-0002", results[2].BatchSeparatorValue);

        Assert.False(results[3].StartsNewBatch);
    }

    private static void WriteBlankPdf(string path, int pageCount)
    {
        using var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
            builder.AddPage(595, 842);
        File.WriteAllBytes(path, builder.Build());
    }

    private sealed class FakeBarcodeDecoder : IBarcodeDecoder
    {
        private readonly IReadOnlyDictionary<int, string> _valuesByPage;

        public FakeBarcodeDecoder(IReadOnlyDictionary<int, string> valuesByPage) => _valuesByPage = valuesByPage;

        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone)
        {
            var fileName = Path.GetFileNameWithoutExtension(imagePath);
            if (!int.TryParse(fileName, out var pageNumber))
                return null;

            return _valuesByPage.TryGetValue(pageNumber, out var value)
                ? new BarcodeReadResult(value, "CODE_128", 1f)
                : null;
        }
    }
}
