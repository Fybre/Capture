using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Storage;

namespace Capture.Tests;

public class DocumentStoreTests
{
    [Fact]
    public async Task Save_and_roundtrip_document_with_pages()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-store-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var document = new CaptureDocument
        {
            OriginalFileName = "sample.pdf",
            StoredPath = Path.Combine(root, "original.pdf"),
            Source = DocumentSource.Import,
            Status = DocumentStatus.NeedsReview,
            PageCount = 2
        };

        var pages = new[]
        {
            new DocumentPage { DocumentId = document.Id, PageNumber = 1, SourcePageNumber = 1, ImagePath = "1.png", Width = 100, Height = 200, Dpi = 150 },
            new DocumentPage { DocumentId = document.Id, PageNumber = 2, SourcePageNumber = 2, ImagePath = "2.png", Width = 100, Height = 200, Dpi = 150 }
        };

        await store.SaveAsync(document, pages);

        var all = await store.GetAllAsync();
        var loaded = Assert.Single(all);
        Assert.Equal(document.OriginalFileName, loaded.OriginalFileName);
        Assert.Equal(DocumentStatus.NeedsReview, loaded.Status);
        Assert.Equal(2, loaded.PageCount);

        var loadedPages = await store.GetPagesAsync(document.Id);
        Assert.Equal(2, loadedPages.Count);
        Assert.Equal(1, loadedPages[0].PageNumber);
        Assert.Equal("2.png", loadedPages[1].ImagePath);
    }

    [Fact]
    public async Task Delete_removes_document_and_work_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-del-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var document = new CaptureDocument
        {
            Id = id,
            OriginalFileName = "gone.pdf",
            StoredPath = paths.DocumentOriginalPath(id, "gone.pdf"),
            Source = DocumentSource.Import,
            Status = DocumentStatus.NeedsReview,
            PageCount = 1
        };

        Directory.CreateDirectory(paths.DocumentDirectory(id));
        File.WriteAllText(document.StoredPath, "x");

        await store.SaveAsync(document, [
            new DocumentPage
            {
                DocumentId = document.Id,
                PageNumber = 1,
                SourcePageNumber = 1,
                ImagePath = "1.png",
                Width = 10,
                Height = 10,
                Dpi = 72
            }
        ]);

        await store.DeleteAsync(document.Id);

        Assert.Empty(await store.GetAllAsync());
        Assert.Empty(await store.GetPagesAsync(document.Id));
        Assert.False(Directory.Exists(paths.DocumentDirectory(document.Id)));
    }

    [Fact]
    public async Task Create_batch_and_assign_to_document()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-batch-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var batch = await store.CreateBatchAsync();
        var document = new CaptureDocument
        {
            OriginalFileName = "a.pdf",
            StoredPath = "a.pdf",
            Source = DocumentSource.Import,
            BatchId = batch.Id,
            Status = DocumentStatus.NeedsReview,
            PageCount = 0
        };
        await store.SaveAsync(document, []);
        var loaded = Assert.Single(await store.GetAllAsync());
        Assert.Equal(batch.Id, loaded.BatchId);
    }
}
