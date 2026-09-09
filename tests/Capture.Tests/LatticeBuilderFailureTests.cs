using System.Diagnostics;
using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;

namespace Capture.Tests;

/// <summary>Covers BuildDocumentAsync's per-page failure handling — a real extraction failure must be
/// logged (never silently indistinguishable from a genuine "no text on this page" result), while a
/// cancellation must propagate rather than being recorded as a normal empty page.</summary>
public class LatticeBuilderFailureTests
{
    [Fact]
    public async Task A_failed_page_is_logged_and_still_recorded_so_the_document_stays_usable()
    {
        var store = new RecordingLatticeStore();
        var builder = new LatticeBuilder(
            new AppPaths(Path.Combine(Path.GetTempPath(), "capture-lattice-fail-" + Guid.NewGuid().ToString("N"))),
            store,
            new NoOpPdfTextExtractor(),
            new ThrowingOcrEngine("boom: tesseract exploded"),
            new NoOpPdfRasterizer());

        var document = new CaptureDocument
        {
            OriginalFileName = "scan.png",
            StoredPath = "/tmp/does-not-matter.png",
            Source = DocumentSource.Import
        };
        var page = new DocumentPage
        {
            DocumentId = document.Id,
            PageNumber = 1,
            SourcePageNumber = 1,
            ImagePath = "/tmp/does-not-matter.png",
            Width = 100,
            Height = 100,
            Dpi = 150
        };

        var listener = new CapturingTraceListener();
        Trace.Listeners.Add(listener);
        try
        {
            await builder.BuildDocumentAsync(document, [page]);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }

        var saved = Assert.Single(store.Saved);
        Assert.Empty(saved.Words);
        Assert.Contains(listener.Messages, message =>
            message.Contains(document.Id.ToString()) &&
            message.Contains("page 1") &&
            message.Contains("boom: tesseract exploded"));
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_recorded_as_an_empty_page()
    {
        var store = new RecordingLatticeStore();
        var builder = new LatticeBuilder(
            new AppPaths(Path.Combine(Path.GetTempPath(), "capture-lattice-cancel-" + Guid.NewGuid().ToString("N"))),
            store,
            new NoOpPdfTextExtractor(),
            new CancelingOcrEngine(),
            new NoOpPdfRasterizer());

        var document = new CaptureDocument
        {
            OriginalFileName = "scan.png",
            StoredPath = "/tmp/does-not-matter.png",
            Source = DocumentSource.Import
        };
        var page = new DocumentPage
        {
            DocumentId = document.Id,
            PageNumber = 1,
            SourcePageNumber = 1,
            ImagePath = "/tmp/does-not-matter.png",
            Width = 100,
            Height = 100,
            Dpi = 150
        };

        // Not pre-cancelled — the cancellation originates from inside the OCR engine (mirrors a real
        // inner timeout, e.g. TesseractCliOcrEngine's own 60s CancelAfter), not the caller's token.
        await Assert.ThrowsAsync<OperationCanceledException>(() => builder.BuildDocumentAsync(document, [page]));

        Assert.Empty(store.Saved);
    }

    private sealed class ThrowingOcrEngine(string message) : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(message);
    }

    private sealed class CancelingOcrEngine : IOcrEngine
    {
        public Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default) =>
            throw new OperationCanceledException("inner OCR timeout");
    }

    private sealed class NoOpPdfTextExtractor : IPdfTextExtractor
    {
        public Task<IReadOnlyList<LatticeWord>?> TryExtractPageAsync(string pdfPath, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LatticeWord>?>(null);
    }

    private sealed class NoOpPdfRasterizer : IPdfRasterizer
    {
        public Task<IReadOnlyList<RasterPage>> RasterizeAsync(string pdfPath, string outputDirectory, int dpi, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RasterPage>>([]);

        public Task RasterizePageAsync(string pdfPath, int pageNumber, string outputPath, int dpi, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLatticeStore : ILatticeStore
    {
        public List<PageLattice> Saved { get; } = [];

        public Task SaveAsync(Guid documentId, PageLattice lattice, CancellationToken cancellationToken = default)
        {
            Saved.Add(lattice);
            return Task.CompletedTask;
        }

        public Task<PageLattice?> GetAsync(Guid documentId, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult<PageLattice?>(null);
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        public List<string> Messages { get; } = [];

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (message is not null)
                Messages.Add(message);
        }
    }
}
