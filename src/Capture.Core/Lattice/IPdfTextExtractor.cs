namespace Capture.Core.Lattice;

public interface IPdfTextExtractor
{
    Task<IReadOnlyList<LatticeWord>?> TryExtractPageAsync(
        string pdfPath,
        int pageNumber,
        CancellationToken cancellationToken = default);
}
