using Capture.Core.CaptureProfiles;
using Capture.Pdf;
using SkiaSharp;

namespace Capture.Tests;

public class ScanPassTests
{
    [Fact]
    public async Task A_scan_pass_written_as_one_tiff_imports_every_page_at_its_resolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-scanpass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var gray = WritePng(Path.Combine(root, "1.png"), SKColorType.Gray8, new SKColor(40, 40, 40));
            var color = WritePng(Path.Combine(root, "2.png"), SKColorType.Rgba8888, new SKColor(200, 30, 10));
            var tiff = Path.Combine(root, "Scan.tif");

            TiffPageWriter.Write([(gray, 300), (color, 200)], tiff);
            var pages = await new SkiaImagePageImporter().ImportAsync(tiff, Path.Combine(root, "out"));

            Assert.Equal([(1, 300), (2, 200)], pages.Select(page => (page.PageNumber, page.Dpi)));
            Assert.All(pages, page => Assert.Equal((40, 30), (page.Width, page.Height)));
            using var first = SKBitmap.Decode(pages[0].ImagePath);
            using var second = SKBitmap.Decode(pages[1].ImagePath);
            Assert.Equal(new SKColor(40, 40, 40), first.GetPixel(5, 5));
            Assert.Equal(new SKColor(200, 30, 10), second.GetPixel(39, 29));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_pass_too_big_for_a_classic_tiff_is_written_as_bigtiff_and_still_imports()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-scanpass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var page = WritePng(Path.Combine(root, "1.png"), SKColorType.Rgba8888, new SKColor(10, 120, 200));
            var tiff = Path.Combine(root, "Scan.tif");

            TiffPageWriter.Write([(page, 300), (page, 300)], tiff, classicRawLimit: 0);
            var header = File.ReadAllBytes(tiff)[..4];
            var pages = await new SkiaImagePageImporter().ImportAsync(tiff, Path.Combine(root, "out"));

            Assert.Equal(43, header[2]); // BigTIFF
            Assert.Equal(2, pages.Count);
            using var first = SKBitmap.Decode(pages[0].ImagePath);
            Assert.Equal(new SKColor(10, 120, 200), first.GetPixel(0, 0));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Pages_in_one_input_stay_one_document_but_separate_files_each_end_one()
    {
        // Why a scan pass is combined into one file: by default each input file ends its document.
        var profile = new CaptureProfile { Name = "Scans", DocumentTypes = [new DocumentTypeDefinition { Name = "Default" }] };
        static AnalyzedPage Page(string input, int number) => new(input, number, "", []);

        var oneFile = new CapturePlanner().Plan(profile, [new AnalyzedInput("scan", [Page("scan", 1), Page("scan", 2), Page("scan", 3)])]);
        var threeFiles = new CapturePlanner().Plan(profile,
            [new AnalyzedInput("a", [Page("a", 1)]), new AnalyzedInput("b", [Page("b", 1)]), new AnalyzedInput("c", [Page("c", 1)])]);

        Assert.Single(oneFile.Batches.SelectMany(batch => batch.Documents));
        Assert.Equal(3, threeFiles.Batches.SelectMany(batch => batch.Documents).Count());
    }

    private static string WritePng(string path, SKColorType colorType, SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(40, 30, colorType, SKAlphaType.Opaque));
        bitmap.Erase(color);
        using var output = File.Create(path);
        bitmap.Encode(output, SKEncodedImageFormat.Png, 100);
        return path;
    }
}
