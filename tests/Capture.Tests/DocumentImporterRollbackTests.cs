using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Pdf;
using Capture.Storage;
using UglyToad.PdfPig.Writer;

namespace Capture.Tests;

public class DocumentImporterRollbackTests
{
    // Regression coverage for a bug where a split failing partway through ImportAsync (indexing
    // profile splitting every page) left every split materialized before the failure permanently
    // committed in the store as an orphaned "ghost" document — never surfaced to the caller, and
    // duplicated if the same source file was retried.
    [Fact]
    public async Task ImportAsync_rolls_back_earlier_splits_when_a_later_split_fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-import-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pdfPath = Path.Combine(root, "source.pdf");
        WriteBlankPdf(pdfPath, pageCount: 3);

        var paths = new AppPaths(Path.Combine(root, "data"));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var latticeBuilder = new FailOnNthDocumentLatticeBuilder(failOnCall: 2);
        var importer = new DocumentImporter(
            paths, store, new PdfiumRasterizer(), new SkiaImagePageImporter(), latticeBuilder, new PdfPigSubsetWriter());

        var profile = new IndexingProfile
        {
            Name = "Splits every page",
            Separation = new DocumentSeparation { Trigger = DocumentSeparationTrigger.EveryNPages, PageCount = 1 }
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => importer.ImportAsync(pdfPath, DocumentSource.Import, profile));

        var remaining = await store.GetAllAsync();
        Assert.Empty(remaining);
    }

    private static void WriteBlankPdf(string path, int pageCount)
    {
        using var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
            builder.AddPage(595, 842);
        File.WriteAllBytes(path, builder.Build());
    }

    private sealed class FailOnNthDocumentLatticeBuilder : ILatticeBuilder
    {
        private readonly int _failOnCall;
        private int _calls;

        public FailOnNthDocumentLatticeBuilder(int failOnCall) => _failOnCall = failOnCall;

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
        {
            _calls++;
            if (_calls == _failOnCall)
                throw new InvalidOperationException("Simulated failure partway through import.");
            return Task.CompletedTask;
        }
    }
}
