using Capture.Core.CaptureProfiles;
using Capture.Core.Models;

namespace Capture.Core.Lattice;

public interface ILatticeBuilder
{
    /// <param name="ocr">How to OCR a scanned page; null uses the settings of the capture profile the
    /// document belongs to (built-in only when it has none).</param>
    Task<PageLattice> BuildPageAsync(
        CaptureDocument document,
        DocumentPage page,
        CancellationToken cancellationToken = default,
        OcrOptions? ocr = null);

    Task BuildDocumentAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        CancellationToken cancellationToken = default);
}
