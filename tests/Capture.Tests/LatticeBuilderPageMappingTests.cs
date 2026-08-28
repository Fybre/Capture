using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;

namespace Capture.Tests;

// Regression coverage for a real bug: DocumentImporter.MaterializeSplitAsync copies the *whole* original
// multi-page file to a split document's StoredPath (never a per-document trimmed copy), but renumbers
// DocumentPage.PageNumber to 1..N for that document alone. LatticeBuilder used to pass PageNumber straight
// through to PDF text extraction / OCR rasterization against StoredPath, silently reading the wrong page
// of the original file — and since every document in a batch shares the same StoredPath, every document
// with the same PageNumber ended up reading the exact same (wrong) page. SourcePageNumber fixes this.
public class LatticeBuilderPageMappingTests
{
    [Fact]
    public async Task BuildPageAsync_reads_pdf_text_from_SourcePageNumber_not_the_renumbered_PageNumber()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-lattice-map-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var pdfText = new RecordingPdfTextExtractor();
        var builder = new LatticeBuilder(paths, new NoopLatticeStore(), pdfText, new NoopOcrEngine(), new NoopRasterizer());

        var document = new CaptureDocument { OriginalFileName = "original.pdf", StoredPath = "/tmp/original.pdf", Source = DocumentSource.Import };
        var page = new DocumentPage
        {
            DocumentId = document.Id,
            PageNumber = 1,       // this document's own first page...
            SourcePageNumber = 4, // ...but it's actually page 4 of the original multi-page file
            ImagePath = "0001.png"
        };

        await builder.BuildPageAsync(document, page);

        Assert.Equal(4, pdfText.RequestedPageNumber);
    }

    [Fact]
    public async Task BuildPageAsync_rasterizes_for_ocr_from_SourcePageNumber_not_the_renumbered_PageNumber()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-lattice-map-ocr-" + Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        var rasterizer = new RecordingRasterizer();
        var builder = new LatticeBuilder(paths, new NoopLatticeStore(), new EmptyPdfTextExtractor(), new NoopOcrEngine(), rasterizer);

        var document = new CaptureDocument { OriginalFileName = "original.pdf", StoredPath = "/tmp/original.pdf", Source = DocumentSource.Import };
        var page = new DocumentPage
        {
            DocumentId = document.Id,
            PageNumber = 1,
            SourcePageNumber = 4,
            ImagePath = "0001.png"
        };

        await builder.BuildPageAsync(document, page);

        Assert.Equal(4, rasterizer.RequestedPageNumber);
    }

    private sealed class RecordingPdfTextExtractor : IPdfTextExtractor
    {
        public int? RequestedPageNumber { get; private set; }

        public Task<IReadOnlyList<LatticeWord>?> TryExtractPageAsync(string pdfPath, int pageNumber, CancellationToken cancellationToken = default)
        {
            RequestedPageNumber = pageNumber;
            // LatticeQuality.LooksLikeRealText requires enough letters/length to count as "real" text
            // rather than junk — a short single word wouldn't clear that bar.
            var words = new LatticeWord[]
            {
                new() { Text = "Invoice Number Total Amount Due", Confidence = 99, X = 0.1f, Y = 0.1f, Width = 0.2f, Height = 0.05f }
            };
            return Task.FromResult<IReadOnlyList<LatticeWord>?>(words);
        }
    }

    private sealed class EmptyPdfTextExtractor : IPdfTextExtractor
    {
        public Task<IReadOnlyList<LatticeWord>?> TryExtractPageAsync(string pdfPath, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LatticeWord>?>(null);
    }

    private sealed class RecordingRasterizer : IPdfRasterizer
    {
        public int? RequestedPageNumber { get; private set; }

        public Task<IReadOnlyList<RasterPage>> RasterizeAsync(string pdfPath, string outputDirectory, int dpi, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RasterizePageAsync(string pdfPath, int pageNumber, string outputPath, int dpi, CancellationToken cancellationToken = default)
        {
            RequestedPageNumber = pageNumber;
            File.WriteAllBytes(outputPath, [0]);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopRasterizer : IPdfRasterizer
    {
        public Task<IReadOnlyList<RasterPage>> RasterizeAsync(string pdfPath, string outputDirectory, int dpi, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RasterizePageAsync(string pdfPath, int pageNumber, string outputPath, int dpi, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopOcrEngine : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OcrResult { Words = [] });
    }

    private sealed class NoopLatticeStore : ILatticeStore
    {
        public Task SaveAsync(Guid documentId, PageLattice lattice, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PageLattice?> GetAsync(Guid documentId, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<PageLattice?>(null);
    }
}
