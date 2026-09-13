using Capture.Pdf;
using SkiaSharp;

namespace Capture.Tests;

public class SkiaImageRotatorTests
{
    [Theory]
    [InlineData(90, 30, 60)]
    [InlineData(270, 30, 60)]
    [InlineData(180, 60, 30)]
    public void Rotate_swaps_dimensions_only_for_a_90_or_270_degree_turn(int degrees, int expectedWidth, int expectedHeight)
    {
        var rotator = new SkiaImageRotator();
        var path = Write(60, 30, bitmap => bitmap.Erase(SKColors.White));

        var (width, height) = rotator.Rotate(path, degrees);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
        using var reloaded = SKBitmap.Decode(path);
        Assert.Equal(expectedWidth, reloaded.Width);
        Assert.Equal(expectedHeight, reloaded.Height);
    }

    [Fact]
    public void Rotate_moves_a_marked_corner_to_the_expected_side()
    {
        var rotator = new SkiaImageRotator();
        // A wide rectangle with a black square only in its top-left corner.
        var path = Write(60, 30, bitmap =>
        {
            bitmap.Erase(SKColors.White);
            using var canvas = new SKCanvas(bitmap);
            canvas.DrawRect(0, 0, 10, 10, new SKPaint { Color = SKColors.Black });
        });

        rotator.Rotate(path, 90);

        // A 90-degree clockwise turn moves the top-left corner to the top-right.
        using var rotated = SKBitmap.Decode(path);
        Assert.Equal(30, rotated.Width);
        Assert.Equal(60, rotated.Height);
        Assert.Equal(SKColors.Black, rotated.GetPixel(25, 5));
        Assert.Equal(SKColors.White, rotated.GetPixel(5, 5));
        Assert.Equal(SKColors.White, rotated.GetPixel(5, 55));
    }

    [Fact]
    public void Rotate_rejects_an_angle_that_is_not_a_multiple_of_90()
    {
        var rotator = new SkiaImageRotator();
        var path = Write(20, 20, bitmap => bitmap.Erase(SKColors.White));

        Assert.Throws<ArgumentOutOfRangeException>(() => rotator.Rotate(path, 45));
    }

    private static string Write(int width, int height, Action<SKBitmap> paint)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        using var bitmap = new SKBitmap(width, height);
        paint(bitmap);
        using var output = File.Create(path);
        bitmap.Encode(output, SKEncodedImageFormat.Png, 90);
        return path;
    }
}
