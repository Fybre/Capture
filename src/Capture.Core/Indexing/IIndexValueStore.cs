using Capture.Core.Models;

namespace Capture.Core.Indexing;

public interface IIndexValueStore
{
    Task SaveAsync(Guid documentId, IReadOnlyList<IndexValue> values, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexValue>> GetAsync(Guid documentId, CancellationToken cancellationToken = default);

    Task SaveBatchAsync(Guid batchId, IReadOnlyList<IndexValue> values, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<IndexValue>> GetBatchAsync(Guid batchId, CancellationToken cancellationToken = default);
}
