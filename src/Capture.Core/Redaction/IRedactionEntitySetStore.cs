namespace Capture.Core.Redaction;

/// <summary>Persists user-created redaction sets only — see <see cref="BuiltInRedactionSets"/> for the
/// predefined ones, which never pass through this store.</summary>
public interface IRedactionEntitySetStore
{
    Task<IReadOnlyList<RedactionEntitySet>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<RedactionEntitySet?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveAsync(RedactionEntitySet set, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
