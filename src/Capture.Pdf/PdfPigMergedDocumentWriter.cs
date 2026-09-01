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

                var pdfPage = builder.AddPage(bitmap.Width, bitmap.Height);
                pdfPage.AddPng(encoded.ToArray(), new PdfRectangle(0, 0, bitmap.Width, bitmap.Height));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, builder.Build());
        }, cancellationToken);
    }
}
