namespace Capture.Core.Import;

/// <summary>Writes a new PDF containing only the given pages of a source PDF, in the given order.</summary>
public interface IPdfSubsetWriter
{
    Task WritePagesAsync(
        string sourcePdfPath,
        IReadOnlyList<int> pageNumbers,
        string outputPath,
        CancellationToken cancellationToken = default);
}
