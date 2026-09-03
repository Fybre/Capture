using Capture.Core.Models;

namespace Capture.Core.Store;

/// <summary>Picks which documents Settings' "Clean up now" action is allowed to delete — see
/// SettingsViewModel.CleanUpOldDocumentsAsync. Pulled out as a pure function so the selection rule
/// (never touch anything still needing attention) is unit-testable without the ViewModel's DI graph.</summary>
public static class DocumentCleanup
{
    /// <summary>Only a document that has actually finished its journey — Exported — and was imported
    /// more than <paramref name="olderThanDays"/> days ago is eligible. Anything still Queued,
    /// Processing, NeedsReview, Ready-but-not-yet-exported, or Error is never included, regardless of
    /// age — those represent work the reviewer hasn't finished, or a failure they haven't seen.</summary>
    public static IReadOnlyList<CaptureDocument> SelectStale(
        IEnumerable<CaptureDocument> documents,
        int olderThanDays,
        DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Max(1, olderThanDays));
        return documents
            .Where(document => document.Status == DocumentStatus.Exported && document.CreatedUtc <= cutoff)
            .ToList();
    }

    /// <summary>Every exported document, regardless of age — Settings' "Clean up now" button. Still
    /// never touches anything that isn't Exported, for the same reason <see cref="SelectStale"/>
    /// doesn't.</summary>
    public static IReadOnlyList<CaptureDocument> SelectExported(IEnumerable<CaptureDocument> documents) =>
        documents.Where(document => document.Status == DocumentStatus.Exported).ToList();

    /// <summary>Trashed documents (<see cref="CaptureDocument.DeletedUtc"/> set) whose retention window
    /// has passed — the automatic sweep that actually purges (permanently) rather than just trashing.
    /// Only ever called against the result of <c>IDocumentStore.GetTrashedAsync</c>, so every candidate
    /// already has <see cref="CaptureDocument.DeletedUtc"/> set; a document without it is ignored here
    /// rather than throwing, so this stays safe to call against any document list.</summary>
    public static IReadOnlyList<CaptureDocument> SelectExpiredTrash(
        IEnumerable<CaptureDocument> documents,
        int retentionDays,
        DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        return documents
            .Where(document => document.DeletedUtc is { } deletedUtc && deletedUtc <= cutoff)
            .ToList();
    }
}
