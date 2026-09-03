using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Pdf;
using Capture.Storage;

namespace Capture.Tests;

public class DocumentImporterScannedPagesTests
{
    // Regression coverage for "multi page adf scan should be one multi page document" — a multi-page
    // ADF/feeder scan arrives as several independent per-page images (no single source file), and
    // should land as one multi-page document, not one document per physical page.
    [Fact]
    public async Task ImportScannedPagesAsync_with_no_splitting_produces_a_single_multi_page_document()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-scan-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pages = CreateScannedPages(root, count: 3);

        var paths = new AppPaths(Path.Combine(root, "data"));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var importer = new DocumentImporter(
            paths, store, new PdfiumRasterizer(), new SkiaImagePageImporter(), new NoOpLatticeBuilder(), new PdfPigSubsetWriter());

        var imported = await importer.ImportScannedPagesAsync(pages, DocumentSource.Scan);

        var document = Assert.Single(imported);
        Assert.Equal(3, document.Document.PageCount);

        var stored = await store.GetAllAsync();
        var onlyDocument = Assert.Single(stored);
        Assert.Equal(3, onlyDocument.PageCount);
    }

    [Fact]
    public async Task ImportScannedPagesAsync_splits_into_multiple_documents_when_profile_separates_every_page()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-scan-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pages = CreateScannedPages(root, count: 3);

        var paths = new AppPaths(Path.Combine(root, "data"));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var importer = new DocumentImporter(
            paths, store, new PdfiumRasterizer(), new SkiaImagePageImporter(), new NoOpLatticeBuilder(), new PdfPigSubsetWriter());

        var profile = new IndexingProfile { Name = "Splits every page" };
        var importProfile = new ImportProfile
        {
            Name = "Splits every page",
            Trigger = ImportSeparationTrigger.EveryNPages,
            PageCount = 1
        };

        var imported = await importer.ImportScannedPagesAsync(pages, DocumentSource.Scan, profile, batchProfile: null, importProfile);

        Assert.Equal(3, imported.Count);
        Assert.All(imported, item => Assert.Equal(1, item.Document.PageCount));

        var stored = await store.GetAllAsync();
        Assert.Equal(3, stored.Count);
    }

    private static List<ScannedPageInfo> CreateScannedPages(string root, int count)
    {
        var pages = new List<ScannedPageInfo>();
        for (var i = 0; i < count; i++)
        {
            var path = Path.Combine(root, $"page-{i + 1}.png");
            File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]); // minimal placeholder content; never decoded
            pages.Add(new ScannedPageInfo(path, 850, 1100, 200));
        }

        return pages;
    }

    private sealed class NoOpLatticeBuilder : ILatticeBuilder
    {
        public Task<PageLattice> BuildPageAsync(CaptureDocument document, DocumentPage page, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new PageLattice
            {
                PageNumber = page.PageNumber,
                PixelWidth = page.Width,
                PixelHeight = page.Height,
                Dpi = page.Dpi,
                Source = LatticeSource.Ocr,
                Words = []
            });
        }

        public Task BuildDocumentAsync(CaptureDocument document, IReadOnlyList<DocumentPage> pages, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
