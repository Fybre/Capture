using Capture.Core.Batches;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Import;

/// <summary>One already-rasterized page from a scan job — see <see cref="IDocumentImporter.ImportScannedPagesAsync"/>.
/// Unlike <see cref="IDocumentImporter.ImportAsync"/>'s path-based overload, there's no single source
/// file to rasterize (a multi-page ADF scan arrives as several independent per-page images), so the
/// caller (the scan source) supplies each page's already-known image path/dimensions/DPI directly.</summary>
public sealed record ScannedPageInfo(string ImagePath, int Width, int Height, int Dpi);

public interface IDocumentImporter
{
    Task<CaptureDocument> ImportFileAsync(
        string path,
        DocumentSource source,
        CancellationToken cancellationToken = default,
        int? imageDpiOverride = null,
        ImportProfile? importProfile = null);

    Task<IReadOnlyList<ImportedDocument>> ImportAsync(
        string path,
        DocumentSource source,
        IndexingProfile? profile = null,
        BatchProfile? batchProfile = null,
        ImportProfile? importProfile = null,
        CancellationToken cancellationToken = default,
        int? imageDpiOverride = null);

    Task<IReadOnlyList<CaptureDocument>> ImportFolderAsync(
        string folder,
        DocumentSource source,
        CancellationToken cancellationToken = default);

    /// <summary>Imports a set of already-scanned page images as a single logical scan job — a multi-page
    /// ADF scan becomes one multi-page document (or, if the profile/batch profile split on separator
    /// pages, several), the same way a multi-page PDF or TIFF already does via <see cref="ImportAsync"/>,
    /// rather than one document per physical page.</summary>
    Task<IReadOnlyList<ImportedDocument>> ImportScannedPagesAsync(
        IReadOnlyList<ScannedPageInfo> pages,
        DocumentSource source,
        IndexingProfile? profile = null,
        BatchProfile? batchProfile = null,
        ImportProfile? importProfile = null,
        CancellationToken cancellationToken = default);
}
