using Capture.Core.Import;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Pdf;
using Capture.Storage;
using SkiaSharp;

namespace Capture.Tests;

public class ImageImportTests
{
    [Fact]
    public async Task Import_png_creates_single_page()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pngPath = Path.Combine(root, "sample.png");

        using (var bitmap = new SKBitmap(32, 24))
        using (var output = File.Create(pngPath))
        {
            bitmap.Erase(SKColors.White);
            bitmap.Encode(output, SKEncodedImageFormat.Png, 90);
        }

        var paths = new AppPaths(Path.Combine(root, "data"));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();

        var importer = new DocumentImporter(paths, store, new PdfiumRasterizer(), new SkiaImagePageImporter(), new NoOpLatticeBuilder(), new PdfPigSubsetWriter());
        var document = await importer.ImportFileAsync(pngPath, DocumentSource.Import);

        Assert.True(document.Status == DocumentStatus.NeedsReview, document.ErrorMessage);
        Assert.Equal(1, document.PageCount);
        var pages = await store.GetPagesAsync(document.Id);
        var page = Assert.Single(pages);
        Assert.True(File.Exists(page.ImagePath));
        Assert.Equal(32, page.Width);
        Assert.Equal(24, page.Height);
    }
}
