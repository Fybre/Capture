using Capture.Core.Models;

namespace Capture.Core.Store;

public interface IDocumentStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(CaptureDocument document, CancellationToken cancellationToken = default);

    /// <summary>Every non-trashed document — excludes anything with <see cref="CaptureDocument.DeletedUtc"/>
    /// set. See <see cref="GetTrashedAsync"/> for the mirror query.</summary>
    Task<IReadOnlyList<CaptureDocument>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Every soft-deleted (trashed) document — the Trash view's source.</summary>
    Task<IReadOnlyList<CaptureDocument>> GetTrashedAsync(CancellationToken cancellationToken = default);

    /// <summary>Fetches by id regardless of trash state — unlike <see cref="GetAllAsync"/>, a trashed
    /// document is still individually fetchable (needed by Restore/Purge and internal consistency
    /// checks).</summary>
    Task<CaptureDocument?> GetAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentPage>> GetPagesAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    /// <summary>Every active (non-trashed) document sharing this exact <see cref="CaptureDocument.ContentHash"/>
    /// — used both for the "does this file already exist" check at import time and to derive whether an
    /// already-imported document currently has any duplicates, without persisting a separate flag that
    /// could go stale. A null/empty hash never matches anything (every document with no hash yet, e.g.
    /// a scanned document, is never considered a duplicate of another hashless document).</summary>
    Task<IReadOnlyList<CaptureDocument>> FindByContentHashAsync(string contentHash, CancellationToken cancellationToken = default);

    /// <summary>Reversible removal — sets <see cref="CaptureDocument.DeletedUtc"/>, touches no files.
    /// This is what every reviewer-initiated "delete a document" action should call (Remove,
    /// RemoveAfterExport, both cleanup sweeps) — not <see cref="PurgeAsync"/>.</summary>
    Task SoftDeleteAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Undoes <see cref="SoftDeleteAsync"/>.</summary>
    Task RestoreAsync(Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>The real, permanent removal — deletes the DB rows, the on-disk document directory, and
    /// cascades an empty-batch cleanup. No undo. Reserved for the Trash view's explicit "Delete
    /// permanently" action and internal rollback of a document that was never really "there" from the
    /// user's perspective (e.g. DocumentImporter/PageManagementService cleaning up after a failed
    /// operation) — everything else should call <see cref="SoftDeleteAsync"/> instead.</summary>
    Task PurgeAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<CaptureBatch> CreateBatchAsync(Guid? watchFolderEntryId = null, CancellationToken cancellationToken = default);

    /// <summary>The most recently created batch tagged with this watch folder, or null if none exists yet —
    /// used by a Manual batch policy to resume appending to an already-open batch.</summary>
    Task<CaptureBatch?> GetLatestBatchForFolderAsync(Guid watchFolderEntryId, CancellationToken cancellationToken = default);

    Task DeleteEmptyBatchAsync(Guid batchId, CancellationToken cancellationToken = default);

    Task<int> GetBatchNumberAsync(Guid batchId, CancellationToken cancellationToken = default);

    Task<int> GetDocumentNumberInBatchAsync(Guid batchId, Guid documentId, CancellationToken cancellationToken = default);
}

/// <summary>What MainViewModel's import loop does when a file's content hash already matches an active
/// document — see <c>WatchSettings.DuplicateImportBehavior</c> (the setting) and
/// <see cref="IDocumentStore.FindByContentHashAsync"/> (the lookup it's checked against).</summary>
public enum DuplicateImportBehavior
{
    /// <summary>Import it anyway, exactly as if no match were found — today's behavior, so upgrading
    /// never surprises anyone. The file's hash is still recorded either way.</summary>
    ImportAnyway = 0,

    /// <summary>Don't import the file at all — it's treated as already handled, not as a failure.</summary>
    Skip = 1,

    /// <summary>Import it anyway, but every document sharing that hash (the new one and whatever it
    /// matches) shows a visible "matches an already-imported document" indicator until one of them is
    /// removed or re-hashed differently.</summary>
    FlagForReview = 2
}
