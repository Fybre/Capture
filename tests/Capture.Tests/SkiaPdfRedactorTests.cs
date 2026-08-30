using Capture.Core.Models;
using Capture.Core.Redaction;
using Capture.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Capture.Tests;

public class SkiaPdfRedactorTests
{
    [Fact]
    public async Task WriteAsync_produces_one_image_only_page_per_input_page_with_the_redaction_burned_in()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-redactor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        const int width = 200;
        const int height = 100;
        var page1Path = Path.Combine(root, "0001.png");
        var page2Path = Path.Combine(root, "0002.png");
        WriteWhitePng(page1Path, width, height);
        WriteWhitePng(page2Path, width, height);

        var document = new CaptureDocument
        {
            Id = Guid.NewGuid(),
            OriginalFileName = "sample.pdf",
            StoredPath = Path.Combine(root, "original.pdf")
        };

        var pages = new List<DocumentPage>
        {
            new() { DocumentId = document.Id, PageNumber = 1, SourcePageNumber = 1, ImagePath = page1Path },
            new() { DocumentId = document.Id, PageNumber = 2, SourcePageNumber = 2, ImagePath = page2Path }
        };

        // Covers the left half of page 1 only: X in [0, 0.5), full height.
        var candidates = new List<RedactionCandidate>
        {
            new() { PageNumber = 1, X = 0f, Y = 0f, Width = 0.5f, Height = 1f, Score = 1f }
        };

        var outputPath = Path.Combine(root, "redacted.pdf");
        var writer = new SkiaPdfRedactor();
        await writer.WriteAsync(document, pages, candidates, outputPath);

        using var pdf = PdfDocument.Open(outputPath);
        Assert.Equal(2, pdf.NumberOfPages);

        var page1Image = Assert.Single(pdf.GetPage(1).GetImages());
        Assert.True(page1Image.TryGetPng(out var page1Png));
        using var page1Bitmap = SKBitmap.Decode(page1Png);

        // Redacted region (left half) is solid black; untouched region (right half) is still white.
        Assert.Equal(new SKColor(0, 0, 0), page1Bitmap.GetPixel(width / 4, height / 2));
        Assert.Equal(SKColors.White, page1Bitmap.GetPixel(width * 3 / 4, height / 2));

        // Page 2 had no candidates — rebuilt from its own image unchanged, still fully white.
        var page2Image = Assert.Single(pdf.GetPage(2).GetImages());
        Assert.True(page2Image.TryGetPng(out var page2Png));
        using var page2Bitmap = SKBitmap.Decode(page2Png);
        Assert.Equal(SKColors.White, page2Bitmap.GetPixel(width / 2, height / 2));
    }

    private static void WriteWhitePng(string path, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        using var stream = File.Create(path);
        bitmap.Encode(stream, SKEncodedImageFormat.Png, 90);
    }
}
