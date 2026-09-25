namespace Capture.Therefore;

public interface IThereforeClient
{
    Task<bool> TestConnectionAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThereforeTreeNode>> GetCategoriesTreeAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default);

    Task<ThereforeCategoryInfo> GetCategoryInfoAsync(ThereforeConnectionSettings settings, int categoryNo, CancellationToken cancellationToken = default);

    Task<ThereforeCreateDocumentResult> CreateDocumentAsync(ThereforeConnectionSettings settings, ThereforeCreateDocumentRequest request, CancellationToken cancellationToken = default);

    /// <summary>True if a document with this DocNo exists — used to recover from a CreateDocument
    /// failure that's actually a race with a Therefore workflow (e.g. one triggered by the save itself,
    /// moving the document to a different category before CreateDocument's own follow-up finishes),
    /// not a genuine create failure. See ThereforeExportWriter's CreateDocument catch block.</summary>
    Task<bool> DocumentExistsAsync(ThereforeConnectionSettings settings, int docNo, CancellationToken cancellationToken = default);
}
