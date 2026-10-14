using Capture.Pdf;
using SkiaSharp;

namespace Capture.Tests;

public class PageStraightenerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "capture-straighten-" + Guid.NewGuid().ToString("N"));

    public PageStraightenerTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    // An A4-ish page at 150 dpi with lines of "text" (dashes of varying length), turned clockwise.
    private string Page(double tiltClockwise, string name = "page.png")
    {
        var path = Path.Combine(_folder, name);
        using var bitmap = new SKBitmap(1240, 1754);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(620, 877);
            canvas.RotateDegrees((float)tiltClockwise);
            canvas.Translate(-620, -877);
            using var ink = new SKPaint { Color = SKColors.Black };
            var random = new Random(7);
            for (var line = 0; line < 40; line++)
            {
                var y = 150 + line * 36;
                var x = 120f;
                while (x < 1080)
                {
                    var word = random.Next(20, 90);
                    canvas.DrawRect(x, y, word, 14, ink);
                    x += word + random.Next(10, 20);
                }
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
        return path;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2.5)]
    [InlineData(-4)]
    [InlineData(7)]
    public void Finds_the_tilt_of_lines_of_text(double tilt)
    {
        var detected = new SkiaPageStraightener().DetectSkew(Page(tilt));

        Assert.InRange(detected, tilt - 0.3, tilt + 0.3);
    }

    [Fact]
    public void Turning_back_by_the_detected_tilt_leaves_the_page_level()
    {
        var straightener = new SkiaPageStraightener();
        var path = Page(3);

        var (width, height) = straightener.Rotate(path, -straightener.DetectSkew(path));

        Assert.Equal((1240, 1754), (width, height));
        Assert.InRange(straightener.DetectSkew(path), -0.3, 0.3);
    }

    [Fact]
    public void A_blank_page_has_no_tilt()
    {
        var path = Path.Combine(_folder, "blank.png");
        using (var bitmap = new SKBitmap(1240, 1754))
        {
            bitmap.Erase(SKColors.White);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
        }

        Assert.Equal(0, new SkiaPageStraightener().DetectSkew(path));
    }
}
