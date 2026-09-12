using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Store;
using Capture.Storage;

namespace Capture.Tests;

public sealed class CaptureMaterializationLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialization_extracts_a_batch_barcode_from_the_document_pages(bool reuseBlankOpenBatch)
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-batch-barcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceImage = Path.Combine(root, "student.png");
            await File.WriteAllTextAsync(sourceImage, "image placeholder");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var documents = new SqliteDocumentStore(paths);
            await documents.InitializeAsync();
            var indexes = new JsonIndexValueStore(paths);
            var type = new DocumentTypeDefinition { Name = "Student record" };
            var barcodeField = new IndexField
            {
                Name = "Student number",
                Kind = FieldKind.Barcode,
                ValuePattern = @"^Student\|.*$"
            };
            var profile = new CaptureProfile
            {
                DefaultDocumentTypeId = type.Id,
                DocumentTypes = [type],
                Batch = new BatchDefinition
                {
                    Fields =
                    [
                        barcodeField
                    ]
                }
            };
            CaptureBatch? openBatch = null;
            if (reuseBlankOpenBatch)
            {
                openBatch = await documents.CreateScopedBatchAsync(profile.Id, "manual");
                await indexes.SaveBatchAsync(openBatch.Id,
                [
                    new IndexValue
                    {
                        FieldId = barcodeField.Id,
                        FieldName = barcodeField.Name,
                        Kind = FieldKind.Barcode,
                        Value = string.Empty
                    }
                ]);
            }
            var plannedDocument = new PlannedDocument(Guid.NewGuid(), type, [new SourcePage("input-1", 1)], []);
            var plan = new CapturePlan(
                [new PlannedBatch(Guid.NewGuid(), true, [], [plannedDocument])], [], []);
            var source = new CaptureMaterializationSource(
                "input-1",
                sourceImage,
                DocumentSource.Import,
                [new RasterPage(1, sourceImage, 100, 100, 200)],
                new Dictionary<int, PageLattice>
                {
                    [1] = new() { PageNumber = 1, PixelWidth = 100, PixelHeight = 100, Dpi = 200 }
                });
            var materializer = new CapturePlanMaterializer(
                paths,
                documents,
                indexes,
                new JsonLatticeStore(paths),
                new ProfileApplicator(new FixedBarcodeDecoder()),
                new CopySubsetWriter(),
                new CopyMergedWriter(),
                reuseBlankOpenBatch ? documents : null);

            var result = await materializer.MaterializeAsync(
                profile,
                plan,
                new Dictionary<string, CaptureMaterializationSource> { [source.Id] = source },
                inputChannel: "manual");

            var batchId = openBatch?.Id ?? Assert.Single(result.BatchIds);
            var batchValues = await indexes.GetBatchAsync(batchId);
            Assert.Equal("Student|X00007", Assert.Single(batchValues).Value);
        }
        finally
        {
            // SqliteDocumentStore's connections are pooled (Microsoft.Data.Sqlite default) — on Windows
            // the native file handle behind a pooled connection stays open until the pool is cleared,
            // which fails this directory delete with "capture.db ... being used by another process"
            // even though nothing here holds an active connection anymore. macOS/Linux allow unlinking
            // an open file, so this only ever surfaced on Windows CI.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Materialization_extracts_a_batch_barcode_from_a_removed_separator_page()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-consumed-batch-barcode-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceImage = Path.Combine(root, "source.png");
            var separatorImage = Path.Combine(root, "separator.png");
            var bodyImage = Path.Combine(root, "body.png");
            await File.WriteAllTextAsync(sourceImage, "source placeholder");
            await File.WriteAllTextAsync(separatorImage, "separator placeholder");
            await File.WriteAllTextAsync(bodyImage, "body placeholder");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var documents = new SqliteDocumentStore(paths);
            await documents.InitializeAsync();
            var indexes = new JsonIndexValueStore(paths);
            var type = new DocumentTypeDefinition { Name = "Student record" };
            var barcodeField = new IndexField { Name = "Student number", Kind = FieldKind.Barcode, PageScope = PageScope.First };
            var profile = new CaptureProfile
            {
                DefaultDocumentTypeId = type.Id,
                DocumentTypes = [type],
                Batch = new BatchDefinition { Fields = [barcodeField] }
            };
            var separator = new SourcePage("input-1", 1);
            var plannedDocument = new PlannedDocument(Guid.NewGuid(), type, [new SourcePage("input-1", 2)], []);
            var plan = new CapturePlan(
                [new PlannedBatch(Guid.NewGuid(), false, [], [plannedDocument], [], separator)],
                [],
                [separator]);
            var source = new CaptureMaterializationSource(
                "input-1",
                sourceImage,
                DocumentSource.Import,
                [
                    new RasterPage(1, separatorImage, 100, 100, 200),
                    new RasterPage(2, bodyImage, 100, 100, 200)
                ],
                new Dictionary<int, PageLattice>
                {
                    [1] = new() { PageNumber = 1, PixelWidth = 100, PixelHeight = 100, Dpi = 200 },
                    [2] = new() { PageNumber = 2, PixelWidth = 100, PixelHeight = 100, Dpi = 200 }
                });
            var materializer = new CapturePlanMaterializer(
                paths,
                documents,
                indexes,
                new JsonLatticeStore(paths),
                new ProfileApplicator(new SeparatorOnlyBarcodeDecoder(separatorImage)),
                new CopySubsetWriter(),
                new CopyMergedWriter());

            var result = await materializer.MaterializeAsync(
                profile,
                plan,
                new Dictionary<string, CaptureMaterializationSource> { [source.Id] = source });

            var batchValues = await indexes.GetBatchAsync(Assert.Single(result.BatchIds));
            Assert.Equal("Student|X00007", Assert.Single(batchValues).Value);
            Assert.Equal(1, Assert.Single(result.Documents).PageCount);
        }
        finally
        {
            // SqliteDocumentStore's connections are pooled (Microsoft.Data.Sqlite default) — on Windows
            // the native file handle behind a pooled connection stays open until the pool is cleared,
            // which fails this directory delete with "capture.db ... being used by another process"
            // even though nothing here holds an active connection anymore. macOS/Linux allow unlinking
            // an open file, so this only ever surfaced on Windows CI.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, BatchState.Open)]
    [InlineData(true, BatchState.Closed)]
    public async Task Execution_can_close_the_scoped_batch_when_the_input_set_finishes(
        bool closeWhenFinished,
        BatchState expected)
    {
        var profile = new CaptureProfile();
        var batches = new FakeOpenBatchStore(profile.Id, "watch:invoices");
        var materializer = new CapturePlanMaterializer(
            null!, null!, null!, null!, null!, null!, null!, batches);

        await materializer.MaterializeAsync(
            profile,
            new CapturePlan([], [], []),
            new Dictionary<string, CaptureMaterializationSource>(),
            inputChannel: "watch:invoices",
            closeBatchWhenFinished: closeWhenFinished);

        Assert.Equal(expected, batches.Batch.State);
    }

    private sealed class FakeOpenBatchStore(Guid profileId, string channel) : IOpenBatchStore
    {
        public CaptureBatch Batch { get; } = new()
        {
            CaptureProfileId = profileId,
            InputChannel = channel,
            Number = 1
        };

        public Task<CaptureBatch> CreateScopedBatchAsync(Guid captureProfileId, string inputChannel, CancellationToken cancellationToken = default) =>
            Task.FromResult(Batch);

        public Task<CaptureBatch?> GetOpenBatchAsync(Guid captureProfileId, string inputChannel, CancellationToken cancellationToken = default) =>
            Task.FromResult<CaptureBatch?>(Batch.State == BatchState.Open ? Batch : null);

        public Task SetBatchStateAsync(Guid batchId, BatchState state, CancellationToken cancellationToken = default)
        {
            Batch.State = state;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedBarcodeDecoder : IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            new("Student|X00007", "QR_CODE", 95);
    }

    private sealed class SeparatorOnlyBarcodeDecoder(string separatorPath) : IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            imagePath == separatorPath ? new("Student|X00007", "QR_CODE", 95) : null;
    }

    private sealed class CopySubsetWriter : IPdfSubsetWriter
    {
        public Task WritePagesAsync(string sourcePdfPath, IReadOnlyList<int> pageNumbers, string outputPath, CancellationToken cancellationToken = default)
        {
            File.Copy(sourcePdfPath, outputPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    // Stands in for PdfPigSubsetWriter, which really does throw when handed a non-PDF file (PdfPig's
    // own xref/trailer parser rejects it outright) — used to prove a single-source scan/import no
    // longer reaches the subset-writer branch at all now that it isn't a PDF.
    private sealed class ThrowingSubsetWriter : IPdfSubsetWriter
    {
        public Task WritePagesAsync(string sourcePdfPath, IReadOnlyList<int> pageNumbers, string outputPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Could not find any xref tables or streams in this document and could not resolve brute force positions.");
    }

    private sealed class CopyMergedWriter : IMergedDocumentWriter
    {
        public Task WriteAsync(IReadOnlyList<DocumentPage> pages, string outputPath, CancellationToken cancellationToken = default)
        {
            File.Copy(pages[0].ImagePath, outputPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Materializing_a_single_non_pdf_source_uses_the_merged_writer_and_stores_it_as_pdf()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-single-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var sourceImage = Path.Combine(root, "scan.png");
            await File.WriteAllTextAsync(sourceImage, "image placeholder");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var documents = new SqliteDocumentStore(paths);
            await documents.InitializeAsync();
            var indexes = new JsonIndexValueStore(paths);
            var type = new DocumentTypeDefinition { Name = "Unsorted" };
            var profile = new CaptureProfile { DefaultDocumentTypeId = type.Id, DocumentTypes = [type] };
            var plannedDocument = new PlannedDocument(Guid.NewGuid(), type, [new SourcePage("input-1", 1)], []);
            var plan = new CapturePlan([new PlannedBatch(Guid.NewGuid(), true, [], [plannedDocument])], [], []);
            var source = new CaptureMaterializationSource(
                "input-1",
                sourceImage,
                DocumentSource.Scan,
                [new RasterPage(1, sourceImage, 100, 100, 200)],
                new Dictionary<int, PageLattice> { [1] = new() { PageNumber = 1, PixelWidth = 100, PixelHeight = 100, Dpi = 200 } });
            var materializer = new CapturePlanMaterializer(
                paths,
                documents,
                indexes,
                new JsonLatticeStore(paths),
                new ProfileApplicator(new FixedBarcodeDecoder()),
                new ThrowingSubsetWriter(),
                new CopyMergedWriter());

            var result = await materializer.MaterializeAsync(
                profile,
                plan,
                new Dictionary<string, CaptureMaterializationSource> { [source.Id] = source },
                inputChannel: "manual");

            var document = Assert.Single(result.Documents);
            Assert.True(ImportFormats.IsPdf(document.StoredPath));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }
}
