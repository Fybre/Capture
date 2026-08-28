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

    Task<IReadOnlyList<CaptureDocument>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentPage>> GetPagesAsync(
        Guid documentId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task<CaptureBatch> CreateBatchAsync(Guid? watchFolderEntryId = null, CancellationToken cancellationToken = default);

    /// <summary>The most recently created batch tagged with this watch folder, or null if none exists yet —
    /// used by a Manual batch policy to resume appending to an already-open batch.</summary>
    Task<CaptureBatch?> GetLatestBatchForFolderAsync(Guid watchFolderEntryId, CancellationToken cancellationToken = default);

    Task DeleteEmptyBatchAsync(Guid batchId, CancellationToken cancellationToken = default);

    Task<int> GetBatchNumberAsync(Guid batchId, CancellationToken cancellationToken = default);

    Task<int> GetDocumentNumberInBatchAsync(Guid batchId, Guid documentId, CancellationToken cancellationToken = default);
}
