using Capture.Core.Models;
using Capture.Core.Store;

namespace Capture.Core.Batches;

/// <summary>
/// Decides which <see cref="CaptureBatch"/> a newly materialized document belongs to, according to a
/// <see cref="BatchProfile"/>. One allocator is created per import operation and asked once per document.
/// A null profile behaves like <see cref="BatchTrigger.NewBatchPerFile"/> — today's default.
/// </summary>
public sealed class BatchAllocator
{
    private readonly IDocumentStore _store;
    private readonly BatchTrigger _trigger;
    private readonly int _pageThreshold;
    private readonly Guid? _watchFolderEntryId;
    private int _pagesInCurrentBatch;

    private BatchAllocator(IDocumentStore store, BatchProfile? profile, Guid? watchFolderEntryId, CaptureBatch? seed)
    {
        _store = store;
        _trigger = profile?.Trigger ?? BatchTrigger.NewBatchPerFile;
        _pageThreshold = Math.Max(1, profile?.PageCount ?? 1);
        _watchFolderEntryId = watchFolderEntryId;
        Current = seed;
    }

    /// <summary>The batch most recently handed out — the one still open at the end of the import call.</summary>
    public CaptureBatch? Current { get; private set; }

    /// <summary>
    /// <paramref name="resumeBatch"/> lets a caller seed continuation for Manual policy without a watch
    /// folder (e.g. a manual toolbar import "append to current batch" toggle). When absent and the policy
    /// is Manual with a watch folder, the most recently created batch for that folder is resumed instead.
    /// </summary>
    public static async Task<BatchAllocator> CreateAsync(
        IDocumentStore store,
        BatchProfile? profile,
        Guid? watchFolderEntryId,
        CaptureBatch? resumeBatch = null,
        CancellationToken cancellationToken = default)
    {
        var seed = resumeBatch;
        if (seed is null && profile?.Trigger == BatchTrigger.Manual && watchFolderEntryId is { } id)
            seed = await store.GetLatestBatchForFolderAsync(id, cancellationToken).ConfigureAwait(false);

        return new BatchAllocator(store, profile, watchFolderEntryId, seed);
    }

    /// <summary>
    /// Returns the batch a newly materialized document should be filed into, allocating a new one first
    /// if the policy calls for it.
    /// </summary>
    /// <param name="isFirstDocumentOfFile">True only for the first document produced from a given source
    /// file — a file split into several documents by page separation still counts as one file for
    /// <see cref="BatchTrigger.NewBatchPerFile"/>.</param>
    /// <param name="batchTriggerHit">Whether this document's first triggering page (if any) matched the
    /// policy's <see cref="BatchTrigger.Barcode"/>/<see cref="BatchTrigger.RegexMatch"/> detector — computed
    /// independently via <c>BatchSeparator</c>, not from document-level separator values.</param>
    /// <param name="pageCount">The document's page count, accumulated for <see cref="BatchTrigger.EveryNPages"/>.</param>
    public async Task<CaptureBatch> NextAsync(
        bool isFirstDocumentOfFile,
        bool batchTriggerHit,
        int pageCount,
        CancellationToken cancellationToken = default)
    {
        var needsNew = Current is null
            || (_trigger == BatchTrigger.NewBatchPerFile && isFirstDocumentOfFile)
            || (_trigger is BatchTrigger.Barcode or BatchTrigger.RegexMatch && batchTriggerHit && _pagesInCurrentBatch > 0)
            || (_trigger == BatchTrigger.EveryNPages && _pagesInCurrentBatch >= _pageThreshold);

        if (needsNew)
        {
            Current = await _store.CreateBatchAsync(_watchFolderEntryId, cancellationToken).ConfigureAwait(false);
            _pagesInCurrentBatch = 0;
        }

        _pagesInCurrentBatch += pageCount;
        return Current!;
    }
}
