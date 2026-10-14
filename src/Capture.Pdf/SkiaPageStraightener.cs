using Capture.Core.Import;
using SkiaSharp;

namespace Capture.Pdf;

/// <summary>Measures tilt by projection: the page's dark pixels are projected onto the vertical axis at a
/// range of angles, and at the page's true tilt the lines of text pile up into sharp peaks with clean gaps
/// between them, which the sum of squared differences between neighbouring rows rewards.</summary>
public sealed class SkiaPageStraightener : IPageStraightener
{
    // Worked on a reduced copy: plenty to find lines of text, and fast.
    private const int WorkWidth = 1000;

    // Too little ink to find lines of text in, e.g. a near-blank page or a photo-free cover sheet.
    private const int MinDarkPixels = 400;

    public double DetectSkew(string imagePath)
    {
        using var original = SKBitmap.Decode(imagePath);
        if (original is null || original.Width < 50 || original.Height < 50)
            return 0;

        var scale = Math.Min(1.0, (double)WorkWidth / original.Width);
        var width = Math.Max(1, (int)(original.Width * scale));
        var height = Math.Max(1, (int)(original.Height * scale));
        using var small = original.Resize(new SKImageInfo(width, height, SKColorType.Gray8), SKSamplingOptions.Default);
        if (small is null)
            return 0;

        var xs = new List<int>();
        var ys = new List<int>();
        var pixels = small.GetPixelSpan();
        var rowBytes = small.RowBytes;
        // A margin is skipped: scanner edges and punched holes are dark but aren't text.
        var marginX = width / 30;
        var marginY = height / 30;
        for (var y = marginY; y < height - marginY; y++)
        {
            for (var x = marginX; x < width - marginX; x++)
            {
                if (pixels[y * rowBytes + x] < 128)
                {
                    xs.Add(x);
                    ys.Add(y);
                }
            }
        }

        // A page that's mostly dark (a photo, a solid fill) has no lines of text to measure.
        if (xs.Count < MinDarkPixels || xs.Count > width * height / 4)
            return 0;

        var (best, bestScore) = Search(xs, ys, height, -IPageStraightener.MaxSkewDegrees, IPageStraightener.MaxSkewDegrees, 0.5, double.NaN);
        (best, bestScore) = Search(xs, ys, height, best - 0.5, best + 0.5, 0.05, best);
        var (_, levelScore) = Search(xs, ys, height, 0, 0, 1, 0);

        // Only a clear improvement over leaving it level counts — on a page without lines of text every
        // angle scores about the same, and a guess would tilt it.
        return bestScore > levelScore * 1.1 ? Math.Round(best, 2) : 0;
    }

    private static (double Angle, double Score) Search(List<int> xs, List<int> ys, int height, double from, double to, double step, double fallback)
    {
        var bestAngle = double.IsNaN(fallback) ? 0 : fallback;
        var bestScore = double.MinValue;
        var diagonal = height * 2;
        var bins = new int[diagonal * 2 + 1];
        for (var angle = from; angle <= to + 1e-9; angle += step)
        {
            Array.Clear(bins);
            var radians = angle * Math.PI / 180;
            var sin = Math.Sin(radians);
            var cos = Math.Cos(radians);
            for (var i = 0; i < xs.Count; i++)
            {
                // Where the pixel falls once the page is turned back by `angle`: a line of text tilted
                // clockwise by that much lands on one row.
                var row = (int)Math.Round(ys[i] * cos - xs[i] * sin) + diagonal;
                if (row >= 0 && row < bins.Length)
                    bins[row]++;
            }

            double score = 0;
            for (var row = 1; row < bins.Length; row++)
            {
                double difference = bins[row] - bins[row - 1];
                score += difference * difference;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestAngle = angle;
            }
        }

        return (bestAngle, bestScore);
    }

    public (int Width, int Height) Rotate(string imagePath, double degreesClockwise)
    {
        using var original = SKBitmap.Decode(imagePath) ?? throw new InvalidOperationException($"Could not decode image at {imagePath}.");
        // Same size as before: the page stays the paper size it was scanned at (a grown canvas would make
        // the saved PDF page bigger than A4). For a tilt of a few degrees only margin is lost at the corners.
        var width = original.Width;
        var height = original.Height;

        // Grey pages stay grey: kept as Gray8 when they came in that way.
        var colorType = original.ColorType == SKColorType.Gray8 ? SKColorType.Gray8 : SKColorType.Rgba8888;
        using var rotated = new SKBitmap(new SKImageInfo(width, height, colorType, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(width / 2f, height / 2f);
            canvas.RotateDegrees((float)degreesClockwise);
            canvas.Translate(-original.Width / 2f, -original.Height / 2f);
            using var paint = new SKPaint { IsAntialias = true };
            using var image = SKImage.FromBitmap(original);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }

        var format = string.Equals(Path.GetExtension(imagePath), ".png", StringComparison.OrdinalIgnoreCase)
            ? SKEncodedImageFormat.Png
            : SKEncodedImageFormat.Jpeg;
        using var data = rotated.Encode(format, 92);
        using (var stream = File.Create(imagePath))
            data.SaveTo(stream);
        return (width, height);
    }
}
