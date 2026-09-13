using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Store;
using Capture.Pdf;
using Capture.Storage;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Capture.Tests;

public class PdfExportAttachmentProcessorTests
{
    [Fact]
    public async Task Returns_the_plain_resolved_path_unchanged_when_neither_flag_is_set()
    {
        var env = new TestEnv();
        try
        {
            var (document, pages) = env.MakeDocumentWithPages(1);
            var definition = new ExportDefinition { FileMode = ExportFileMode.Original };

            var resolved = await env.Processor.ResolveAsync(definition, document);

            Assert.Equal(document.StoredPath, resolved);
        }
        finally { env.Dispose(); }
    }

    [Fact]
    public async Task SearchablePdf_makes_the_ocr_words_extractable_from_the_rebuilt_pdf()
    {
        var env = new TestEnv();
        try
        {
            var (document, pages) = env.MakeDocumentWithPages(1);
            await env.Lattice.SaveAsync(document.Id, new PageLattice
            {
                PageNumber = 1,
                PixelWidth = 200,
                PixelHeight = 260,
                Dpi = 150,
                Source = LatticeSource.Ocr,
                Words = [new LatticeWord { Text = "Invoice", X = 10, Y = 10, Width = 80, Height = 20 }]
            });
            var definition = new ExportDefinition { FileMode = ExportFileMode.Original, SearchablePdf = true };

            var resolved = await env.Processor.ResolveAsync(definition, document);

            Assert.NotEqual(document.StoredPath, resolved);
            using var pdf = PdfDocument.Open(resolved);
            var text = pdf.GetPage(1).Text;
            Assert.Contains("Invoice", text);
        }
        finally { env.Dispose(); }
    }

    [Fact]
    public async Task SearchablePdf_skips_words_inside_a_confirmed_redaction_on_the_redacted_copy()
    {
        var env = new TestEnv();
        try
        {
            var (document, pages) = env.MakeDocumentWithPages(1);
            var redactedPath = Path.Combine(env.Root, "redacted.pdf");
            File.WriteAllText(redactedPath, "redacted-stub");
            document.RedactedPath = redactedPath;
            document.RedactionStatus = RedactionStatus.Applied;

            await env.Lattice.SaveAsync(document.Id, new PageLattice
            {
                PageNumber = 1,
                PixelWidth = 200,
                PixelHeight = 260,
                Dpi = 150,
                Source = LatticeSource.Ocr,
                Words =
                [
                    new LatticeWord { Text = "Secret", X = 10, Y = 10, Width = 80, Height = 20 },
                    new LatticeWord { Text = "Visible", X = 10, Y = 100, Width = 80, Height = 20 }
                ]
            });
            // Normalized 0-1 box covering the "Secret" word's pixel box (10..90, 10..30 of 200x260).
            await env.RedactionCandidates.SaveAsync(document.Id,
            [
                new RedactionCandidate
                {
                    Decision = RedactionDecision.Confirmed,
                    PageNumber = 1,
                    X = 0.0f, Y = 0.0f, Width = 0.6f, Height = 0.2f
                }
            ]);
            var definition = new ExportDefinition { FileMode = ExportFileMode.Redacted, SearchablePdf = true };

            var resolved = await env.Processor.ResolveAsync(definition, document);

            using var pdf = PdfDocument.Open(resolved);
            var text = pdf.GetPage(1).Text;
            Assert.DoesNotContain("Secret", text);
            Assert.Contains("Visible", text);
        }
        finally { env.Dispose(); }
    }

    [Fact]
    public async Task Pdfa_produces_a_document_with_xmp_metadata()
    {
        var env = new TestEnv();
        try
        {
            var (document, pages) = env.MakeDocumentWithPages(1);
            var definition = new ExportDefinition { FileMode = ExportFileMode.Original, Pdfa = true };

            var resolved = await env.Processor.ResolveAsync(definition, document);

            using var stream = File.OpenRead(resolved);
            using var pdf = PdfDocument.Open(stream);
            Assert.True(pdf.TryGetXmpMetadata(out _));
        }
        finally { env.Dispose(); }
    }

    private sealed class TestEnv : IDisposable
    {
        public string Root { get; }
        public AppPaths Paths { get; }
        public FakeDocumentStore Documents { get; } = new();
        public JsonLatticeStore Lattice { get; }
        public JsonRedactionCandidateStore RedactionCandidates { get; }
        public PdfExportAttachmentProcessor Processor { get; }

        public TestEnv()
        {
            Root = Directory.CreateTempSubdirectory().FullName;
            Paths = new AppPaths(Root);
            Lattice = new JsonLatticeStore(Paths);
            RedactionCandidates = new JsonRedactionCandidateStore(Paths);
            Processor = new PdfExportAttachmentProcessor(Documents, Lattice, RedactionCandidates, Paths);
        }

        public (CaptureDocument Document, IReadOnlyList<DocumentPage> Pages) MakeDocumentWithPages(int pageCount)
        {
            var documentId = Guid.NewGuid();
            var storedPath = Path.Combine(Root, "source.pdf");
            File.WriteAllText(storedPath, "source-stub");
            var pages = new List<DocumentPage>();
            for (var number = 1; number <= pageCount; number++)
            {
                var imagePath = Path.Combine(Root, $"page-{number}.png");
                using (var bitmap = new SKBitmap(200, 260))
                {
                    bitmap.Erase(SKColors.White);
                    using var image = SKImage.FromBitmap(bitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                    using var fileStream = File.OpenWrite(imagePath);
                    data.SaveTo(fileStream);
                }
                pages.Add(new DocumentPage
                {
                    DocumentId = documentId, PageNumber = number, SourcePageNumber = number,
                    ImagePath = imagePath, Width = 200, Height = 260, Dpi = 150
                });
            }
            var document = new CaptureDocument { Id = documentId, OriginalFileName = "source.pdf", StoredPath = storedPath };
            Documents.Pages[documentId] = pages;
            return (document, pages);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class FakeDocumentStore : IDocumentStore
    {
        public Dictionary<Guid, IReadOnlyList<DocumentPage>> Pages { get; } = new();

        public Task<IReadOnlyList<DocumentPage>> GetPagesAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Pages.GetValueOrDefault(documentId, []));

        public Task InitializeAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SaveAsync(CaptureDocument document, IReadOnlyList<DocumentPage> pages, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task UpdateAsync(CaptureDocument document, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CaptureDocument>> GetAllAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CaptureDocument>> GetTrashedAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CaptureDocument?> GetAsync(Guid documentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CaptureDocument>> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<CaptureDocument>> FindByContentHashesAsync(IReadOnlyCollection<string> contentHashes, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task SoftDeleteAsync(Guid documentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task RestoreAsync(Guid documentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task PurgeAsync(Guid documentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CaptureBatch> CreateBatchAsync(Guid? watchFolderEntryId = null, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<CaptureBatch?> GetLatestBatchForFolderAsync(Guid watchFolderEntryId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task DeleteEmptyBatchAsync(Guid batchId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> GetBatchNumberAsync(Guid batchId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<int> GetDocumentNumberInBatchAsync(Guid batchId, Guid documentId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<IReadOnlyDictionary<Guid, CaptureBatch>> GetBatchesAsync(IReadOnlyCollection<Guid> batchIds, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
