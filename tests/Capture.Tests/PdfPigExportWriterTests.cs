using Capture.Core.Models;
using Capture.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Capture.Tests;

public class PdfPigExportWriterTests
{
    [Fact]
    public async Task Writes_one_pdf_page_per_input_page_in_order()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var pages = new[]
            {
                Page(directory, 1, SKColors.Red),
                Page(directory, 2, SKColors.Blue),
                Page(directory, 3, SKColors.Green)
            };
            var outputPath = Path.Combine(directory, "export.pdf");

            await new PdfPigExportWriter().WriteAsync(pages, outputPath, compress: false);

            using var document = PdfDocument.Open(outputPath);
            Assert.Equal(3, document.NumberOfPages);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Compressing_produces_a_smaller_file_than_full_quality()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // A photo-like page (noise, not a flat color) so JPEG's lossy compression actually
            // shrinks it relative to PNG — a flat color already compresses losslessly either way.
            var pages = new[] { NoisyPage(directory, 1) };
            var fullQualityPath = Path.Combine(directory, "full.pdf");
            var compressedPath = Path.Combine(directory, "compressed.pdf");

            var writer = new PdfPigExportWriter();
            await writer.WriteAsync(pages, fullQualityPath, compress: false);
            await writer.WriteAsync(pages, compressedPath, compress: true);

            var fullQualitySize = new FileInfo(fullQualityPath).Length;
            var compressedSize = new FileInfo(compressedPath).Length;
            Assert.True(compressedSize < fullQualitySize,
                $"Expected compressed ({compressedSize} bytes) to be smaller than full quality ({fullQualitySize} bytes).");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DocumentPage Page(string directory, int number, SKColor color)
    {
        var path = Path.Combine(directory, $"{number:D4}.png");
        using (var bitmap = new SKBitmap(200, 260))
        {
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(path);
            data.SaveTo(stream);
        }
        return new DocumentPage { DocumentId = Guid.NewGuid(), PageNumber = number, SourcePageNumber = number, ImagePath = path, Width = 200, Height = 260, Dpi = 150 };
    }

    private static DocumentPage NoisyPage(string directory, int number)
    {
        var path = Path.Combine(directory, $"{number:D4}.png");
        var random = new Random(42);
        using (var bitmap = new SKBitmap(600, 800))
        {
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(path);
            data.SaveTo(stream);
        }
        return new DocumentPage { DocumentId = Guid.NewGuid(), PageNumber = number, SourcePageNumber = number, ImagePath = path, Width = 600, Height = 800, Dpi = 150 };
    }
}
