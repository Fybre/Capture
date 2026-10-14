using Capture.Core.Models;

namespace Capture.Core.Store;

/// <summary>Every export run for each document — see <see cref="ExportRecord"/>. Removed with the
/// document when it's purged.</summary>
public interface IExportHistoryStore
{
    Task AddAsync(Guid documentId, IReadOnlyList<ExportRecord> records, CancellationToken cancellationToken = default);

    /// <summary>Each document's records, newest first. Documents with none aren't in the result.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<ExportRecord>>> GetAsync(
        IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);
}
