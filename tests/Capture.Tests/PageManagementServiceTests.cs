using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Pdf;
using Capture.Storage;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Capture.Tests;

public class PageManagementServiceTests
{
    [Fact]
    public async Task DeletePagesAsync_removes_a_page_and_renumbers_the_rest_for_a_pdf_document()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 4, isPdf: true);

        var result = await env.Service.DeletePagesAsync(document.Id, [2]);

        Assert.Equal(3, result.PageCount);
        var pages = await env.Store.GetPagesAsync(document.Id);
        Assert.Equal([1, 2, 3], pages.Select(p => p.PageNumber).OrderBy(n => n));

        using var pdf = PdfDocument.Open(result.StoredPath);
        Assert.Equal(3, pdf.NumberOfPages);
        Assert.Equal(3, Directory.GetFiles(env.Paths.DocumentPagesDirectory(document.Id)).Length);
    }

    [Fact]
    public async Task DeletePagesAsync_refuses_to_delete_every_page()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 2, isPdf: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => env.Service.DeletePagesAsync(document.Id, [1, 2]));
    }

    [Fact]
    public async Task DeletePagesAsync_drops_zonal_index_values_and_redaction_candidates_on_the_removed_page()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 3, isPdf: true);

        await env.IndexValues.SaveAsync(document.Id,
        [
            new IndexValue { FieldName = "Zonal", PageNumber = 2, Bounds = new ZoneRect { PageNumber = 2, X = 0.1f, Y = 0.1f, Width = 0.2f, Height = 0.1f } },
            new IndexValue { FieldName = "NonZonal", PageNumber = 2 },
            new IndexValue { FieldName = "SurvivingZonal", PageNumber = 3, Bounds = new ZoneRect { PageNumber = 3, X = 0.1f, Y = 0.1f, Width = 0.2f, Height = 0.1f } }
        ]);
        await env.RedactionCandidates.SaveAsync(document.Id,
        [
            new RedactionCandidate { PageNumber = 2, Width = 0.1f, Height = 0.1f },
            new RedactionCandidate { PageNumber = 3, Width = 0.1f, Height = 0.1f }
        ]);

        await env.Service.DeletePagesAsync(document.Id, [2]);

        var values = await env.IndexValues.GetAsync(document.Id);
        Assert.DoesNotContain(values, v => v.FieldName == "Zonal");
        var nonZonal = Assert.Single(values, v => v.FieldName == "NonZonal");
        Assert.Equal(1, nonZonal.PageNumber); // reattached to page 1 rather than left dangling
        var survivingZonal = Assert.Single(values, v => v.FieldName == "SurvivingZonal");
        Assert.Equal(2, survivingZonal.PageNumber); // old page 3 -> new page 2
        Assert.Equal(2, survivingZonal.Bounds!.PageNumber);

        var candidates = await env.RedactionCandidates.GetAsync(document.Id);
        var candidate = Assert.Single(candidates);
        Assert.Equal(2, candidate.PageNumber); // old page 3 -> new page 2
    }

    [Fact]
    public async Task ReorderPagesAsync_moves_page_content_to_match_the_requested_order()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 3, isPdf: true);

        // newPageOrder = [3, 1, 2]: new page 1 <- old page 3, new page 2 <- old page 1, new page 3 <- old page 2.
        var result = await env.Service.ReorderPagesAsync(document.Id, [3, 1, 2]);

        Assert.Equal(3, result.PageCount);
        var pages = (await env.Store.GetPagesAsync(document.Id)).OrderBy(p => p.PageNumber).ToList();
        Assert.Equal(env.MarkerFor(3), File.ReadAllBytes(pages[0].ImagePath));
        Assert.Equal(env.MarkerFor(1), File.ReadAllBytes(pages[1].ImagePath));
        Assert.Equal(env.MarkerFor(2), File.ReadAllBytes(pages[2].ImagePath));

        using var pdf = PdfDocument.Open(result.StoredPath);
        Assert.Equal(3, pdf.NumberOfPages);
    }

    [Fact]
    public async Task ReorderPagesAsync_rejects_a_list_that_is_not_a_permutation()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 3, isPdf: true);

        await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.ReorderPagesAsync(document.Id, [1, 2]));
        await Assert.ThrowsAsync<ArgumentException>(
            () => env.Service.ReorderPagesAsync(document.Id, [1, 2, 4]));
    }

    [Fact]
    public async Task SplitDocumentAsync_produces_two_documents_with_correct_page_counts_and_inherited_assignment()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 5, isPdf: true);
        document.ProfileId = Guid.NewGuid();
        document.BatchId = Guid.NewGuid();
        await env.Store.SaveAsync(document, await env.Store.GetPagesAsync(document.Id));

        var (first, second) = await env.Service.SplitDocumentAsync(document.Id, splitBeforePageNumber: 3);

        Assert.Equal(2, first.PageCount);
        Assert.Equal(3, second.PageCount);
        Assert.Equal(document.ProfileId, second.ProfileId);
        Assert.Equal(document.BatchId, second.BatchId);
        Assert.Equal(DocumentStatus.NeedsReview, second.Status);

        using (var firstPdf = PdfDocument.Open(first.StoredPath))
            Assert.Equal(2, firstPdf.NumberOfPages);
        using (var secondPdf = PdfDocument.Open(second.StoredPath))
            Assert.Equal(3, secondPdf.NumberOfPages);

        var secondPages = (await env.Store.GetPagesAsync(second.Id)).OrderBy(p => p.PageNumber).ToList();
        Assert.Equal([1, 2, 3], secondPages.Select(p => p.PageNumber));
        Assert.Equal(env.MarkerFor(3), File.ReadAllBytes(secondPages[0].ImagePath));
    }

    [Fact]
    public async Task SplitDocumentAsync_rejects_an_out_of_range_split_point()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 3, isPdf: true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => env.Service.SplitDocumentAsync(document.Id, splitBeforePageNumber: 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => env.Service.SplitDocumentAsync(document.Id, splitBeforePageNumber: 4));
    }

    [Fact]
    public async Task DeletePagesAsync_on_an_image_sourced_document_leaves_StoredPath_and_SourcePageNumber_untouched()
    {
        var env = await TestEnv.CreateAsync();
        var document = await env.CreateDocumentAsync(pageCount: 3, isPdf: false);
        var originalStoredPath = document.StoredPath;
        var originalBytes = File.ReadAllBytes(originalStoredPath);

        await env.Service.DeletePagesAsync(document.Id, [2]);

        Assert.True(File.Exists(originalStoredPath));
        Assert.Equal(originalBytes, File.ReadAllBytes(originalStoredPath));

        var pages = (await env.Store.GetPagesAsync(document.Id)).OrderBy(p => p.PageNumber).ToList();
        Assert.Equal([1, 2], pages.Select(p => p.PageNumber));
        // Old page 3 survives as new page 2; its SourcePageNumber (meaningless for image OCR routing,
        // per LatticeBuilder.BuildPageAsync) is left as its original value rather than reset to match
        // the new PageNumber.
        Assert.Equal(3, pages[1].SourcePageNumber);
    }

    [Fact]
    public async Task MergeDocumentsAsync_appends_pages_to_the_first_document_and_removes_the_rest()
    {
        var env = await TestEnv.CreateAsync();
        var first = await env.CreateDocumentAsync(pageCount: 2, isPdf: true, markerOffset: 0);
        var second = await env.CreateDocumentAsync(pageCount: 3, isPdf: true, markerOffset: 10);
        first.ProfileId = Guid.NewGuid();
        first.BatchId = Guid.NewGuid();
        await env.Store.SaveAsync(first, await env.Store.GetPagesAsync(first.Id));

        var result = await env.Service.MergeDocumentsAsync([first.Id, second.Id]);

        Assert.Equal(first.Id, result.Id);
        Assert.Equal(5, result.PageCount);
        Assert.Equal(first.ProfileId, result.ProfileId);
        Assert.Equal(first.BatchId, result.BatchId);
        Assert.Equal(DocumentStatus.NeedsReview, result.Status);
        Assert.Null(await env.Store.GetAsync(second.Id));

        var pages = (await env.Store.GetPagesAsync(first.Id)).OrderBy(page => page.PageNumber).ToList();
        Assert.Equal([1, 2, 11, 12, 13], pages.Select(page => File.ReadAllBytes(page.ImagePath)[0]));
        Assert.All(pages, page => Assert.Equal(first.Id, page.DocumentId));
        Assert.Equal([1, 2, 3, 4, 5], pages.Select(page => page.SourcePageNumber));
        using var pdf = PdfDocument.Open(result.StoredPath);
        Assert.Equal(5, pdf.NumberOfPages);
    }

    [Fact]
    public async Task MergeDocumentsAsync_offsets_redaction_candidates_from_appended_documents()
    {
        var env = await TestEnv.CreateAsync();
        var first = await env.CreateDocumentAsync(pageCount: 2, isPdf: true);
        var second = await env.CreateDocumentAsync(pageCount: 2, isPdf: true);
        await env.RedactionCandidates.SaveAsync(first.Id,
            [new RedactionCandidate { PageNumber = 2, Width = 0.1f, Height = 0.1f }]);
        await env.RedactionCandidates.SaveAsync(second.Id,
            [new RedactionCandidate { PageNumber = 1, Width = 0.1f, Height = 0.1f }]);

        await env.Service.MergeDocumentsAsync([first.Id, second.Id]);

        var candidates = await env.RedactionCandidates.GetAsync(first.Id);
        Assert.Equal([2, 3], candidates.Select(candidate => candidate.PageNumber));
    }

    [Fact]
    public async Task MergeDocumentsAsync_remaps_lattices_from_appended_documents()
    {
        var env = await TestEnv.CreateAsync();
        var first = await env.CreateDocumentAsync(pageCount: 2, isPdf: true);
        var second = await env.CreateDocumentAsync(pageCount: 1, isPdf: true);
        await env.LatticeStore.SaveAsync(second.Id, new PageLattice
        {
            PageNumber = 1,
            PixelWidth = 100,
            PixelHeight = 100,
            Dpi = 96,
            Words = [new LatticeWord { Text = "appended" }]
        });

        await env.Service.MergeDocumentsAsync([first.Id, second.Id]);

        var lattice = await env.LatticeStore.GetAsync(first.Id, 3);
        Assert.NotNull(lattice);
        Assert.Equal("appended", Assert.Single(lattice.Words).Text);
    }

    private sealed class TestEnv
    {
        public required IAppPaths Paths { get; init; }
        public required Storage.SqliteDocumentStore Store { get; init; }
        public required Storage.JsonIndexValueStore IndexValues { get; init; }
        public required Storage.JsonRedactionCandidateStore RedactionCandidates { get; init; }
        public required Storage.JsonLatticeStore LatticeStore { get; init; }
        public required PageManagementService Service { get; init; }

        public static async Task<TestEnv> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "capture-page-mgmt-" + Guid.NewGuid().ToString("N"));
            var paths = new AppPaths(Path.Combine(root, "data"));
            var store = new SqliteDocumentStore(paths);
            await store.InitializeAsync();
            var indexValues = new JsonIndexValueStore(paths);
            var redactionCandidates = new JsonRedactionCandidateStore(paths);
            var latticeStore = new JsonLatticeStore(paths);
            var service = new PageManagementService(
                paths,
                store,
                new NoOpLatticeBuilder(),
                latticeStore,
                new PdfPigSubsetWriter(),
                new TestMergedDocumentWriter(),
                indexValues,
                redactionCandidates);

            return new TestEnv
            {
                Paths = paths,
                Store = store,
                IndexValues = indexValues,
                RedactionCandidates = redactionCandidates,
                LatticeStore = latticeStore,
                Service = service
            };
        }

        /// <summary>The byte "content" written into a page's original image file at import — lets tests
        /// confirm a specific old page's bytes ended up at the expected new location after a
        /// reorder/split, distinguishing pages from each other despite otherwise-identical dummy content.</summary>
        public byte[] MarkerFor(int oldPageNumber) => [(byte)oldPageNumber];

        public async Task<CaptureDocument> CreateDocumentAsync(int pageCount, bool isPdf, int markerOffset = 0)
        {
            var id = Guid.NewGuid();
            Directory.CreateDirectory(Paths.DocumentPagesDirectory(id));
            var storedPath = Paths.DocumentOriginalPath(id, isPdf ? "source.pdf" : "source.png");

            if (isPdf)
            {
                using var builder = new PdfDocumentBuilder();
                for (var i = 0; i < pageCount; i++)
                    builder.AddPage(595, 842);
                File.WriteAllBytes(storedPath, builder.Build());
            }
            else
            {
                File.WriteAllBytes(storedPath, [0xFF]);
            }

            var pages = new List<DocumentPage>(pageCount);
            for (var n = 1; n <= pageCount; n++)
            {
                var imagePath = Path.Combine(Paths.DocumentPagesDirectory(id), $"{n:D4}.png");
                File.WriteAllBytes(imagePath, MarkerFor(n + markerOffset));
                pages.Add(new DocumentPage
                {
                    DocumentId = id,
                    PageNumber = n,
                    SourcePageNumber = n,
                    ImagePath = imagePath,
                    Width = 100,
                    Height = 100,
                    Dpi = 96
                });
            }

            var document = new CaptureDocument
            {
                Id = id,
                OriginalFileName = isPdf ? "source.pdf" : "source.png",
                StoredPath = storedPath,
                Source = DocumentSource.Import,
                Status = DocumentStatus.NeedsReview,
                PageCount = pageCount
            };
            await Store.SaveAsync(document, pages);
            return document;
        }

        private sealed class TestMergedDocumentWriter : IMergedDocumentWriter
        {
            public Task WriteAsync(
                IReadOnlyList<DocumentPage> pages,
                string outputPath,
                CancellationToken cancellationToken = default)
            {
                using var builder = new PdfDocumentBuilder();
                foreach (var _ in pages)
                    builder.AddPage(595, 842);
                File.WriteAllBytes(outputPath, builder.Build());
                return Task.CompletedTask;
            }
        }
    }
}
