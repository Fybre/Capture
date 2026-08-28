namespace Capture.Core.Batches;

public interface IBatchProfileStore
{
    Task<IReadOnlyList<BatchProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<BatchProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(BatchProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
