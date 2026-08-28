using Capture.Core.Batches;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Import;

public interface IDocumentImporter
{
    Task<CaptureDocument> ImportFileAsync(
        string path,
        DocumentSource source,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImportedDocument>> ImportAsync(
        string path,
        DocumentSource source,
        IndexingProfile? profile = null,
        BatchProfile? batchProfile = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CaptureDocument>> ImportFolderAsync(
        string folder,
        DocumentSource source,
        CancellationToken cancellationToken = default);
}
