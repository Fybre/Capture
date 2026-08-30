using Capture.Core.Models;
using Capture.Core.Redaction;
using SkiaSharp;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Writer;

namespace Capture.Pdf;

/// <summary>Produces a truly-redacted PDF: each page's raster image gets the confirmed redaction boxes
/// burned in as solid black fills (destroying the underlying pixels, not just covering them), and the
/// output PDF is rebuilt entirely from those images — never from the source pages' own text/vector
/// content — so nothing redacted survives underneath for copy-paste or text extraction to recover.</summary>
public sealed class SkiaPdfRedactor : IRedactedDocumentWriter
{
    public Task<string> WriteAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        IReadOnlyList<RedactionCandidate> confirmedCandidates,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            var byPage = confirmedCandidates
                .GroupBy(candidate => candidate.PageNumber)
                .ToDictionary(group => group.Key, group => group.ToList());

            using var builder = new PdfDocumentBuilder();
            foreach (var page in pages.OrderBy(item => item.PageNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var bitmap = SKBitmap.Decode(page.ImagePath)
                    ?? throw new InvalidOperationException($"Unable to decode page image '{page.ImagePath}'.");

                if (byPage.TryGetValue(page.PageNumber, out var candidates))
                {
                    using var canvas = new SKCanvas(bitmap);
                    using var paint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Fill };
                    foreach (var candidate in candidates)
                    {
                        canvas.DrawRect(
                            candidate.X * bitmap.Width,
                            candidate.Y * bitmap.Height,
                            candidate.Width * bitmap.Width,
                            candidate.Height * bitmap.Height,
                            paint);
                    }
                }

                using var image = SKImage.FromBitmap(bitmap);
                using var encoded = image.Encode(SKEncodedImageFormat.Png, 90);
                var pngBytes = encoded.ToArray();

                var pageBuilder = builder.AddPage(bitmap.Width, bitmap.Height);
                pageBuilder.AddPng(pngBytes, new PdfRectangle(0, 0, bitmap.Width, bitmap.Height));
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            File.WriteAllBytes(outputPath, builder.Build());
            return outputPath;
        }, cancellationToken);
    }
}
