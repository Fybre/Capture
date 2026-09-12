using Capture.Core.Import;
using Capture.Core.Models;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Capture.Pdf;

/// <summary>Builds an ad hoc export PDF from already-captured page images, for the "Export selected to
/// PDF" action. Kept separate from <see cref="PdfPigMergedDocumentWriter"/> (used by every capture
/// import) so the optional compression path here can never affect the capture pipeline.</summary>
public sealed class PdfPigExportWriter : IPdfExportWriter
{
    private const float CompressedScale = 0.65f;
    private const int CompressedJpegQuality = 55;

    public Task WriteAsync(
        IReadOnlyList<DocumentPage> pages,
        string outputPath,
        bool compress,
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

                // AddPage's MediaBox is in PDF points (1/72"), not pixels — see the identical comment in
                // PdfPigMergedDocumentWriter. Computed from the original bitmap's pixel size regardless
                // of whether the embedded image itself gets downscaled below (compress=true) — the page's
                // physical size should always match the original scan, not whichever image variant fills it.
                var dpi = page.Dpi > 0 ? page.Dpi : 96;
                var widthPoints = bitmap.Width * 72.0 / dpi;
                var heightPoints = bitmap.Height * 72.0 / dpi;

                var pdfPage = builder.AddPage(widthPoints, heightPoints);
                var rectangle = new PdfRectangle(0, 0, widthPoints, heightPoints);
                if (compress)
                {
                    using var scaled = Downscale(bitmap, CompressedScale);
                    using var image = SKImage.FromBitmap(scaled);
                    using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, CompressedJpegQuality);
                    pdfPage.AddJpeg(encoded.ToArray(), rectangle);
                }
                else
                {
                    using var image = SKImage.FromBitmap(bitmap);
                    using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
                    pdfPage.AddPng(encoded.ToArray(), rectangle);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, builder.Build());
        }, cancellationToken);
    }

    private static SKBitmap Downscale(SKBitmap source, float scale)
    {
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));
        return source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidOperationException("Unable to downscale page image.");
    }
}
