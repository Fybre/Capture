using Capture.Pdf;
using SkiaSharp;

namespace Capture.Tests;

public class BlankPageDetectorTests
{
    [Fact]
    public void White_page_is_blank_and_marked_page_is_not()
    {
        var detector = new InkCoverageBlankPageDetector();
        var white = Write(bitmap => bitmap.Erase(SKColors.White));
        var marked = Write(bitmap =>
        {
            bitmap.Erase(SKColors.White);
            using var canvas = new SKCanvas(bitmap);
            canvas.DrawRect(2, 2, 20, 20, new SKPaint { Color = SKColors.Black });
        });

        Assert.True(detector.IsBlank(white, 1));
        Assert.False(detector.IsBlank(marked, 1));
    }

    private static string Write(Action<SKBitmap> paint)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        using var bitmap = new SKBitmap(32, 32);
        paint(bitmap);
        using var output = File.Create(path);
        bitmap.Encode(output, SKEncodedImageFormat.Png, 90);
        return path;
    }
}
