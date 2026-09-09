using Capture.Core.Models;

namespace Capture.Core.Lattice;

public interface ILatticeBuilder
{
    Task<PageLattice> BuildPageAsync(
        CaptureDocument document,
        DocumentPage page,
        CancellationToken cancellationToken = default);

    Task BuildDocumentAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        CancellationToken cancellationToken = default);
}
