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
    public async Task Purge_removes_document_and_work_files()
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

        await store.PurgeAsync(document.Id);

        Assert.Empty(await store.GetAllAsync());
        Assert.Empty(await store.GetPagesAsync(document.Id));
        Assert.False(Directory.Exists(paths.DocumentDirectory(document.Id)));
    }

    [Fact]
    public async Task SoftDelete_hides_from_GetAll_and_moves_it_to_GetTrashed_without_touching_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-softdel-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var id = Guid.NewGuid();
        var document = new CaptureDocument
        {
            Id = id,
            OriginalFileName = "trashed.pdf",
            StoredPath = paths.DocumentOriginalPath(id, "trashed.pdf"),
            Source = DocumentSource.Import,
            Status = DocumentStatus.Exported,
            PageCount = 1
        };
        Directory.CreateDirectory(paths.DocumentDirectory(id));
        File.WriteAllText(document.StoredPath, "x");
        await store.SaveAsync(document, []);

        await store.SoftDeleteAsync(id);

        Assert.Empty(await store.GetAllAsync());
        var trashed = Assert.Single(await store.GetTrashedAsync());
        Assert.Equal(id, trashed.Id);
        Assert.NotNull(trashed.DeletedUtc);
        Assert.True(Directory.Exists(paths.DocumentDirectory(id))); // soft delete never touches files

        await store.RestoreAsync(id);

        var restored = Assert.Single(await store.GetAllAsync());
        Assert.Equal(id, restored.Id);
        Assert.Null(restored.DeletedUtc);
        Assert.Empty(await store.GetTrashedAsync());
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

    [Fact]
    public async Task ContentHash_roundtrips_through_save_and_update()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-hash-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var document = new CaptureDocument
        {
            OriginalFileName = "hashed.pdf",
            StoredPath = Path.Combine(root, "hashed.pdf"),
            Source = DocumentSource.Import,
            Status = DocumentStatus.NeedsReview,
            PageCount = 1,
            ContentHash = "AAAABBBBCCCCDDDD"
        };
        await store.SaveAsync(document, []);

        var saved = Assert.Single(await store.GetAllAsync());
        Assert.Equal("AAAABBBBCCCCDDDD", saved.ContentHash);

        saved.ContentHash = "1111222233334444";
        await store.UpdateAsync(saved);

        var updated = await store.GetAsync(document.Id);
        Assert.NotNull(updated);
        Assert.Equal("1111222233334444", updated!.ContentHash);
    }

    [Fact]
    public async Task FindByContentHashAsync_finds_active_matches_and_excludes_trashed_ones()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-hashfind-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        const string hash = "SHAREDHASHVALUE";

        var active = new CaptureDocument
        {
            OriginalFileName = "active.pdf",
            StoredPath = Path.Combine(root, "active.pdf"),
            Source = DocumentSource.Import,
            Status = DocumentStatus.NeedsReview,
            PageCount = 1,
            ContentHash = hash
        };
        var trashed = new CaptureDocument
        {
            OriginalFileName = "trashed.pdf",
            StoredPath = Path.Combine(root, "trashed.pdf"),
            Source = DocumentSource.Import,
            Status = DocumentStatus.NeedsReview,
            PageCount = 1,
            ContentHash = hash
        };
        var unrelated = new CaptureDocument
        {
            OriginalFileName = "unrelated.pdf",
            StoredPath = Path.Combine(root, "unrelated.pdf"),
            Source = DocumentSource.Import,
            Status = DocumentStatus.NeedsReview,
            PageCount = 1,
            ContentHash = "SOMETHINGELSE"
        };

        await store.SaveAsync(active, []);
        await store.SaveAsync(trashed, []);
        await store.SaveAsync(unrelated, []);
        await store.SoftDeleteAsync(trashed.Id);

        var matches = await store.FindByContentHashAsync(hash);

        var match = Assert.Single(matches);
        Assert.Equal(active.Id, match.Id);
    }

    [Fact]
    public async Task FindByContentHashAsync_returns_empty_for_a_null_or_empty_hash()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-hashempty-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        Assert.Empty(await store.FindByContentHashAsync(string.Empty));
    }
}
