using Capture.Core.Models;

namespace Capture.Core.Import;

/// <summary>Writes an ad hoc export PDF from already-captured page images — the "Export selected to
/// PDF" action, not the capture pipeline's own merged-document writer. Deliberately separate from
/// <see cref="IMergedDocumentWriter"/> so this feature can add a compression option without touching
/// the writer every import already depends on.</summary>
public interface IPdfExportWriter
{
    Task WriteAsync(
        IReadOnlyList<DocumentPage> pages,
        string outputPath,
        bool compress,
        CancellationToken cancellationToken = default);
}
