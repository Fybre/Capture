using Capture.Core.Import;
using SkiaSharp;

namespace Capture.Pdf;

public sealed class InkCoverageBlankPageDetector : IBlankPageDetector
{
    public bool IsBlank(string imagePath, float maxInkPercent)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return false;

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap is null || bitmap.Width == 0 || bitmap.Height == 0)
            return false;

        var dark = 0;
        foreach (var pixel in bitmap.Pixels)
        {
            if ((pixel.Red + pixel.Green + pixel.Blue) / 3 < 245)
                dark++;
        }

        var percent = dark * 100f / bitmap.Pixels.Length;
        return percent <= maxInkPercent;
    }
}
