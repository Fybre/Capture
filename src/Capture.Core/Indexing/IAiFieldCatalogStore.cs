namespace Capture.Core.Indexing;

public interface IAiFieldCatalogStore
{
    /// <summary>
    /// Loads the AI field catalog from disk, creating it from <see cref="AiFieldCatalog.DefaultTypes"/>
    /// on first run. Falls back to the built-in defaults (without touching the file) if the
    /// on-disk catalog can't be parsed.
    /// </summary>
    Task<IReadOnlyList<AiFieldType>> LoadAsync(CancellationToken cancellationToken = default);
}
