using Capture.Core.Models;

namespace Capture.Core.Import;

/// <summary>Post-import page editing for already-imported documents: delete, reorder, split, or merge
/// pages. The counterpart to <see cref="IDocumentImporter"/>'s import-time page
/// splitting, operating on documents already sitting in the review inbox.</summary>
public interface IPageManagementService
{
    /// <summary>Removes the given pages from a document, renumbering the remaining pages 1..N and
    /// remapping index/redaction data accordingly. Throws <see cref="InvalidOperationException"/> if
    /// every page would be removed — delete the whole document instead in that case.</summary>
    Task<CaptureDocument> DeletePagesAsync(
        Guid documentId, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default);

    /// <summary>Reorders a document's pages. <paramref name="newPageOrder"/> must be a permutation of
    /// the document's current page numbers — position <c>i</c> is the existing page number that becomes
    /// the new page <c>i + 1</c>.</summary>
    Task<CaptureDocument> ReorderPagesAsync(
        Guid documentId, IReadOnlyList<int> newPageOrder, CancellationToken cancellationToken = default);

    /// <summary>Splits a document into two at <paramref name="splitBeforePageNumber"/>: pages before it
    /// stay on the original document, that page onward moves to a brand-new document (which inherits the
    /// original's profile/batch assignment). Must be strictly between 2 and the document's page count.</summary>
    Task<(CaptureDocument First, CaptureDocument Second)> SplitDocumentAsync(
        Guid documentId, int splitBeforePageNumber, CancellationToken cancellationToken = default);

    /// <summary>Appends all pages from the second and subsequent documents to the first document in
    /// the supplied order, then removes those absorbed documents. The first document retains its
    /// profile, batch, filename, and document-level index values.</summary>
    Task<CaptureDocument> MergeDocumentsAsync(
        IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken = default);
}
