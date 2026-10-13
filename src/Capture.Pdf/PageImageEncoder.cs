using SkiaSharp;

namespace Capture.Pdf;

/// <summary>Shrinks a page image for sending to an AI model: big enough to read headers and totals,
/// small enough to keep each request cheap.</summary>
public static class PageImageEncoder
{
    public static string ToJpegDataUrl(string imagePath, int maxSide = 1200, int quality = 80)
    {
        using var original = SKBitmap.Decode(imagePath) ?? throw new InvalidOperationException($"Could not read the page image {imagePath}.");
        var scale = Math.Min(1f, maxSide / (float)Math.Max(original.Width, original.Height));
        using var resized = scale < 1f
            ? original.Resize(new SKImageInfo(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale))), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : null;
        using var image = SKImage.FromBitmap(resized ?? original);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return "data:image/jpeg;base64," + Convert.ToBase64String(data.ToArray());
    }

    /// <summary>The page as a JPEG at its full size, for OCR services: word positions come back in the
    /// same pixels, and a JPEG keeps a 300 DPI scan well inside upload limits.</summary>
    public static byte[] ToJpegBytes(string imagePath, int quality = 90)
    {
        using var original = SKBitmap.Decode(imagePath) ?? throw new InvalidOperationException($"Could not read the page image {imagePath}.");
        using var image = SKImage.FromBitmap(original);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }
}
