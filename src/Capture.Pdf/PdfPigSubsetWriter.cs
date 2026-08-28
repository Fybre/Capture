using Capture.Core.Import;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Capture.Pdf;

public sealed class PdfPigSubsetWriter : IPdfSubsetWriter
{
    public Task WritePagesAsync(
        string sourcePdfPath,
        IReadOnlyList<int> pageNumbers,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        return Task.Run(() => Write(sourcePdfPath, pageNumbers, outputPath, cancellationToken), cancellationToken);
    }

    private static void Write(
        string sourcePdfPath,
        IReadOnlyList<int> pageNumbers,
        string outputPath,
        CancellationToken cancellationToken)
    {
        using var source = PdfDocument.Open(sourcePdfPath);
        using var builder = new PdfDocumentBuilder();
        foreach (var pageNumber in pageNumbers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.AddPage(source, pageNumber);
        }

        var bytes = builder.Build();
        File.WriteAllBytes(outputPath, bytes);
    }
}
