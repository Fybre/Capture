namespace Capture.Core.Profiles;

public interface IProfileStore
{
    Task<IReadOnlyList<IndexingProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<IndexingProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(IndexingProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
