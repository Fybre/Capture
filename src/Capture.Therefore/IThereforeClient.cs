namespace Capture.Therefore;

public interface IThereforeClient
{
    Task<bool> TestConnectionAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ThereforeTreeNode>> GetCategoriesTreeAsync(ThereforeConnectionSettings settings, CancellationToken cancellationToken = default);

    Task<ThereforeCategoryInfo> GetCategoryInfoAsync(ThereforeConnectionSettings settings, int categoryNo, CancellationToken cancellationToken = default);

    Task<ThereforeCreateDocumentResult> CreateDocumentAsync(ThereforeConnectionSettings settings, ThereforeCreateDocumentRequest request, CancellationToken cancellationToken = default);
}
