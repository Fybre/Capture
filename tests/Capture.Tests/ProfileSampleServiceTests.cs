using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Pdf;
using Capture.Storage;
using SkiaSharp;

namespace Capture.Tests;

public class ProfileSampleServiceTests
{
    // Regression coverage for "load a different file for the sample" — PrepareAsync used to leave
    // the previous sample's page images/lattices/original file on disk when a replacement sample had
    // a different page count or extension, silently reappearing as extra pages via GetPageImagePaths.
    [Fact]
    public async Task PrepareAsync_replacing_a_sample_removes_the_previous_ones_stale_files()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-sample-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(Path.Combine(root, "data"));
        var service = new ProfileSampleService(paths, new PdfiumRasterizer(), new SkiaImagePageImporter(), new NoOpLatticeBuilder());

        var profile = new IndexingProfile { Name = "Test" };

        var firstPath = WritePng(root, "first.png", 32, 24);
        await service.PrepareAsync(profile, firstPath);
        Assert.Single(service.GetPageImagePaths(profile.Id));
        Assert.Equal("first.png", profile.SampleFileName);
        var originalSamplePath = paths.ProfileSamplePath(profile.Id, "first.png");
        Assert.True(File.Exists(originalSamplePath));

        var secondPath = WritePng(root, "second.jpg", 16, 16, SKEncodedImageFormat.Jpeg);
        await service.PrepareAsync(profile, secondPath);

        var pages = service.GetPageImagePaths(profile.Id);
        var page = Assert.Single(pages);
        Assert.DoesNotContain("first", Path.GetFileName(page), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("second.jpg", profile.SampleFileName);

        // The old "sample.png" file must be gone, not just superseded by a new "sample.jpg".
        Assert.False(File.Exists(originalSamplePath));
        Assert.True(File.Exists(paths.ProfileSamplePath(profile.Id, "second.jpg")));
    }

    private static string WritePng(string root, string fileName, int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, fileName);
        using var bitmap = new SKBitmap(width, height);
        using var output = File.Create(path);
        bitmap.Erase(SKColors.White);
        bitmap.Encode(output, format, 90);
        return path;
    }
}
