using Capture.Core.Paths;
using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Storage;

namespace Capture.Tests;

public class BatchAllocatorTests
{
    private static async Task<SqliteDocumentStore> CreateStoreAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-batch-allocator-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        paths.EnsureCreated();
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();
        return store;
    }

    [Fact]
    public async Task NewBatchPerFile_starts_a_new_batch_only_for_the_first_document_of_each_file()
    {
        var store = await CreateStoreAsync();
        var policy = new BatchProfile { Mode = BatchMode.NewBatchPerFile };
        var allocator = await BatchAllocator.CreateAsync(store, policy, watchFolderEntryId: null);

        var fileOneDoc1 = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 1);
        var fileOneDoc2 = await allocator.NextAsync(isFirstDocumentOfFile: false, batchTriggerHit: false, pageCount: 1);
        var fileTwoDoc1 = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 1);

        Assert.Equal(fileOneDoc1.Id, fileOneDoc2.Id);
        Assert.NotEqual(fileOneDoc1.Id, fileTwoDoc1.Id);
    }

    [Fact]
    public async Task No_profile_keeps_documents_from_every_file_in_one_batch()
    {
        var store = await CreateStoreAsync();
        var allocator = await BatchAllocator.CreateAsync(store, profile: null, watchFolderEntryId: null);

        var fileOne = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 1);
        var fileTwo = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 1);

        Assert.Equal(fileOne.Id, fileTwo.Id);
    }

    [Fact]
    public async Task No_profile_with_a_watch_folder_resumes_its_latest_batch()
    {
        var store = await CreateStoreAsync();
        var folderId = Guid.NewGuid();
        var existing = await store.CreateBatchAsync(folderId);
        var allocator = await BatchAllocator.CreateAsync(store, profile: null, watchFolderEntryId: folderId);

        var next = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 1);

        Assert.Equal(existing.Id, next.Id);
    }

    [Fact]
    public async Task UseStrategies_starts_a_new_batch_only_when_the_trigger_fires_after_the_first_document()
    {
        // BatchAllocator itself is strategy-agnostic now — it just reacts to the batchTriggerHit flag
        // BatchSeparator computed upstream, regardless of which strategy (Barcode here) produced it.
        var store = await CreateStoreAsync();
        var policy = new BatchProfile { Mode = BatchMode.UseStrategies, Strategies = [new SeparationStrategy { Type = SeparationStrategyType.Barcode }] };
        var allocator = await BatchAllocator.CreateAsync(store, policy, watchFolderEntryId: null);

        var first = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: true, pageCount: 1);
        var second = await allocator.NextAsync(isFirstDocumentOfFile: false, batchTriggerHit: false, pageCount: 1);
        var third = await allocator.NextAsync(isFirstDocumentOfFile: false, batchTriggerHit: true, pageCount: 1);

        Assert.Equal(first.Id, second.Id);
        Assert.NotEqual(first.Id, third.Id);
    }

    [Fact]
    public async Task UseStrategies_ignores_a_trigger_hit_on_the_very_first_document_of_a_batch()
    {
        // A hit that lands before any pages have accumulated in the current batch shouldn't
        // immediately re-split it — mirrors the old EveryNPages threshold test's shape; the actual
        // per-strategy counting that produces this hit is covered by
        // BatchSeparatorTests.EveryNPages_strategy_hits_once_the_threshold_is_reached.
        var store = await CreateStoreAsync();
        var policy = new BatchProfile { Mode = BatchMode.UseStrategies, Strategies = [new SeparationStrategy { Type = SeparationStrategyType.EveryNPages, PageCount = 3 }] };
        var allocator = await BatchAllocator.CreateAsync(store, policy, watchFolderEntryId: null);

        var doc1 = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 2);
        var doc2 = await allocator.NextAsync(isFirstDocumentOfFile: false, batchTriggerHit: false, pageCount: 1);
        var doc3 = await allocator.NextAsync(isFirstDocumentOfFile: false, batchTriggerHit: true, pageCount: 1);

        Assert.Equal(doc1.Id, doc2.Id);
        Assert.NotEqual(doc2.Id, doc3.Id);
    }

    [Fact]
    public async Task Manual_never_auto_creates_and_keeps_appending_to_a_single_batch()
    {
        var store = await CreateStoreAsync();
        var policy = new BatchProfile { Mode = BatchMode.Manual };
        var allocator = await BatchAllocator.CreateAsync(store, policy, watchFolderEntryId: null);

        var doc1 = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: true, pageCount: 500);
        var doc2 = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: true, pageCount: 500);

        Assert.Equal(doc1.Id, doc2.Id);
    }

    [Fact]
    public async Task Manual_with_a_watch_folder_resumes_the_most_recently_created_batch_for_that_folder()
    {
        var store = await CreateStoreAsync();
        var folderId = Guid.NewGuid();
        var existing = await store.CreateBatchAsync(folderId);

        var policy = new BatchProfile { Mode = BatchMode.Manual };
        var allocator = await BatchAllocator.CreateAsync(store, policy, folderId);

        var next = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 1);

        Assert.Equal(existing.Id, next.Id);
    }

    [Fact]
    public async Task Manual_with_an_explicit_resume_batch_takes_priority_over_the_folder_lookup()
    {
        var store = await CreateStoreAsync();
        var folderId = Guid.NewGuid();
        await store.CreateBatchAsync(folderId);
        var resumeBatch = await store.CreateBatchAsync();

        var policy = new BatchProfile { Mode = BatchMode.Manual };
        var allocator = await BatchAllocator.CreateAsync(store, policy, folderId, resumeBatch);

        var next = await allocator.NextAsync(isFirstDocumentOfFile: true, batchTriggerHit: false, pageCount: 1);

        Assert.Equal(resumeBatch.Id, next.Id);
    }
}
