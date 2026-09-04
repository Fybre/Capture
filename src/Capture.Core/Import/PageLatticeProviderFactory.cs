using Capture.Core.Lattice;
using Capture.Core.Models;

namespace Capture.Core.Import;

/// <summary>
/// Builds a page's <see cref="PageLattice"/> (OCR/PDF-text word boxes) before any real
/// <c>CaptureDocument</c> exists — used during document splitting (<see cref="PageSeparator"/>'s
/// Regex/OcrZone strategies) and batch-boundary detection (<c>BatchSeparator</c>'s regex trigger),
/// both of which need page text ahead of import actually materializing anything. Wraps a bare
/// <see cref="RasterPage"/> in a throwaway, never-persisted <c>CaptureDocument</c>/<c>DocumentPage</c>
/// pair and runs it through <see cref="ILatticeBuilder.BuildPageAsync"/> — nothing here is saved to
/// <c>ILatticeStore</c>.
/// </summary>
internal static class PageLatticeProviderFactory
{
    public static Func<RasterPage, CancellationToken, Task<PageLattice>> Create(ILatticeBuilder latticeBuilder, string sourcePath) =>
        async (raster, ct) =>
        {
            var throwawayId = Guid.NewGuid();
            var throwawayDocument = new CaptureDocument { OriginalFileName = string.Empty, StoredPath = sourcePath };
            var throwawayPage = new DocumentPage
            {
                DocumentId = throwawayId,
                PageNumber = raster.PageNumber,
                SourcePageNumber = raster.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            };
            return await latticeBuilder.BuildPageAsync(throwawayDocument, throwawayPage, ct).ConfigureAwait(false);
        };
}
