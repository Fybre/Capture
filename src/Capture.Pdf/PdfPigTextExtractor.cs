using Capture.Core.Lattice;
using UglyToad.PdfPig;

namespace Capture.Pdf;

public sealed class PdfPigTextExtractor : IPdfTextExtractor
{
    public Task<IReadOnlyList<LatticeWord>?> TryExtractPageAsync(
        string pdfPath,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);

        return Task.Run(() => Extract(pdfPath, pageNumber, cancellationToken), cancellationToken);
    }

    private static IReadOnlyList<LatticeWord>? Extract(string pdfPath, int pageNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = PdfDocument.Open(pdfPath);
        if (pageNumber < 1 || pageNumber > document.NumberOfPages)
            return null;

        var page = document.GetPage(pageNumber);
        var pageWidth = (double)page.Width;
        var pageHeight = (double)page.Height;
        if (pageWidth <= 0 || pageHeight <= 0)
            return null;

        var words = new List<LatticeWord>();
        foreach (var word in page.GetWords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(word.Text))
                continue;

            var box = word.BoundingBox;
            var width = (float)(box.Width / pageWidth);
            var height = (float)(box.Height / pageHeight);
            if (width <= 0 || height <= 0)
                continue;

            words.Add(new LatticeWord
            {
                Text = word.Text,
                Confidence = 100,
                X = Math.Clamp((float)(box.Left / pageWidth), 0, 1),
                Y = Math.Clamp((float)((pageHeight - box.Top) / pageHeight), 0, 1),
                Width = Math.Clamp(width, 0, 1),
                Height = Math.Clamp(height, 0, 1)
            });
        }

        return LatticeQuality.LooksLikeRealText(words) ? words : null;
    }
}
