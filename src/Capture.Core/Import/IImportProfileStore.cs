namespace Capture.Core.Import;

public interface IImportProfileStore
{
    Task<IReadOnlyList<ImportProfile>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ImportProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(ImportProfile profile, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
