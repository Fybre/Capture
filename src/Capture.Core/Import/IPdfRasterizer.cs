using Capture.Core.Models;

namespace Capture.Core.Import;

public interface IPdfRasterizer
{
    Task<IReadOnlyList<RasterPage>> RasterizeAsync(
        string pdfPath,
        string outputDirectory,
        int dpi,
        CancellationToken cancellationToken = default);

    Task RasterizePageAsync(
        string pdfPath,
        int pageNumber,
        string outputPath,
        int dpi,
        CancellationToken cancellationToken = default);
}
