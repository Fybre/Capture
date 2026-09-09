using Capture.Core.Models;

namespace Capture.Core.Import;

/// <summary>Writes a PDF containing the supplied rasterized document pages in order.</summary>
public interface IMergedDocumentWriter
{
    Task WriteAsync(
        IReadOnlyList<DocumentPage> pages,
        string outputPath,
        CancellationToken cancellationToken = default);
}
