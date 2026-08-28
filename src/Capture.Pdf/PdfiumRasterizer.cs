using Capture.Core.Import;
using Capture.Core.Models;
using PDFtoImage;
using SkiaSharp;

namespace Capture.Pdf;

public sealed class PdfiumRasterizer : IPdfRasterizer
{
    public Task<IReadOnlyList<RasterPage>> RasterizeAsync(
        string pdfPath,
        string outputDirectory,
        int dpi,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        return Task.Run(() =>
        {
            var pdfBytes = File.ReadAllBytes(pdfPath);
            var options = new RenderOptions { Dpi = dpi };
            var pageCount = Conversion.GetPageCount(pdfBytes);
            if (pageCount <= 0)
                throw new InvalidOperationException("The PDF has no pages.");

            var pages = new List<RasterPage>(pageCount);
            for (var i = 0; i < pageCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var imagePath = Path.Combine(outputDirectory, $"{i + 1:D4}.png");
                Conversion.SavePng(imagePath, pdfBytes, i, password: null, options: options);

                using var stream = File.OpenRead(imagePath);
                using var codec = SKCodec.Create(stream)
                    ?? throw new InvalidOperationException($"Unable to read rendered page {i + 1}.");
                pages.Add(new RasterPage(i + 1, imagePath, codec.Info.Width, codec.Info.Height, dpi));
            }

            return (IReadOnlyList<RasterPage>)pages;
        }, cancellationToken);
    }

    public Task RasterizePageAsync(
        string pdfPath,
        int pageNumber,
        string outputPath,
        int dpi,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pdfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (pageNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(pageNumber));

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pdfBytes = File.ReadAllBytes(pdfPath);
            Conversion.SavePng(outputPath, pdfBytes, pageNumber - 1, password: null, options: new RenderOptions { Dpi = dpi });
        }, cancellationToken);
    }
}
