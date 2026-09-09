using Capture.Core.Lattice;
using Capture.Core.Models;

namespace Capture.Tests;

internal sealed class NoOpLatticeBuilder : ILatticeBuilder
{
    public Task<PageLattice> BuildPageAsync(
        CaptureDocument document,
        DocumentPage page,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new PageLattice
        {
            PageNumber = page.PageNumber,
            PixelWidth = page.Width,
            PixelHeight = page.Height,
            Dpi = page.Dpi,
            Source = LatticeSource.Ocr,
            Words = []
        });
    }

    public Task BuildDocumentAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
