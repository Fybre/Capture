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

                var pdfPage = builder.AddPage(bitmap.Width, bitmap.Height);
                var rectangle = new PdfRectangle(0, 0, bitmap.Width, bitmap.Height);
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
