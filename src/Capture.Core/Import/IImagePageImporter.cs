using Capture.Core.Models;

namespace Capture.Core.Import;

public interface IImagePageImporter
{
    Task<IReadOnlyList<RasterPage>> ImportAsync(
        string imagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        int? dpiOverride = null);
}
