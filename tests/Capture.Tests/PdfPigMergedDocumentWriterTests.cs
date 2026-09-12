using Capture.Core.Models;
using Capture.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Capture.Tests;

public class PdfPigMergedDocumentWriterTests
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
                Page(directory, 2, SKColors.Blue)
            };
            var outputPath = Path.Combine(directory, "merged.pdf");

            await new PdfPigMergedDocumentWriter().WriteAsync(pages, outputPath);

            using var document = PdfDocument.Open(outputPath);
            Assert.Equal(2, document.NumberOfPages);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Page_size_is_derived_from_pixel_dimensions_and_dpi_not_raw_pixels()
    {
        // AddPage's MediaBox is in PDF points (1/72"), not pixels — passing bitmap.Width/Height straight
        // through (the shipped bug) produced a page ~4x too large per dimension for a 300 DPI scan.
        // 200x260px at 150 DPI should come out as a 96x124.8-point page.
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var outputPath = Path.Combine(directory, "merged.pdf");
            await new PdfPigMergedDocumentWriter().WriteAsync([Page(directory, 1, SKColors.Red)], outputPath);

            using var document = PdfDocument.Open(outputPath);
            var page = document.GetPage(1);
            Assert.Equal(96.0, page.Width, precision: 1);
            Assert.Equal(124.8, page.Height, precision: 1);
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
}
