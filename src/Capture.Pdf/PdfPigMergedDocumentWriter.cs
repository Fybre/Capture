using Capture.Core.Import;
using Capture.Core.Models;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Capture.Pdf;

/// <summary>Builds a portable merged PDF from the captured page images, allowing PDF-, image-, and
/// scanner-sourced documents to be combined without depending on their original file formats.</summary>
public sealed class PdfPigMergedDocumentWriter : IMergedDocumentWriter
{
    public Task WriteAsync(
        IReadOnlyList<DocumentPage> pages,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            using var builder = new PdfDocumentBuilder();
            foreach (var page in pages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = SKBitmap.Decode(page.ImagePath)
                    ?? throw new InvalidOperationException($"Unable to decode page image '{page.ImagePath}'.");
                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);

                // AddPage's MediaBox is in PDF points (1/72"), not pixels — a 300 DPI scan passed
                // straight through as bitmap.Width/Height came out ~4x too large per dimension (~17x
                // too large in area) before this conversion, since points assume 72 DPI. Falls back to
                // 96 (matching LatticeBuilder's own OCR-DPI fallback) for the rare case a scanner/import
                // path reports no DPI at all, rather than dividing by zero.
                var dpi = page.Dpi > 0 ? page.Dpi : 96;
                var widthPoints = bitmap.Width * 72.0 / dpi;
                var heightPoints = bitmap.Height * 72.0 / dpi;

                var pdfPage = builder.AddPage(widthPoints, heightPoints);
                pdfPage.AddPng(encoded.ToArray(), new PdfRectangle(0, 0, widthPoints, heightPoints));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, builder.Build());
        }, cancellationToken);
    }
}
