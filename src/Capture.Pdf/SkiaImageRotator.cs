using Capture.Core.Import;
using SkiaSharp;

namespace Capture.Pdf;

public sealed class SkiaImageRotator : IImageRotator
{
    public (int Width, int Height) Rotate(string imagePath, int degreesClockwise)
    {
        var normalized = ((degreesClockwise % 360) + 360) % 360;
        if (normalized is not (90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(degreesClockwise), degreesClockwise, "Rotation must be 90, 180, or 270 degrees.");

        using var original = SKBitmap.Decode(imagePath) ?? throw new InvalidOperationException($"Could not decode image at {imagePath}.");

        var swapDimensions = normalized != 180;
        var newWidth = swapDimensions ? original.Height : original.Width;
        var newHeight = swapDimensions ? original.Width : original.Height;

        using var rotated = new SKBitmap(newWidth, newHeight);
        using (var canvas = new SKCanvas(rotated))
        {
            // Standard "rotate into a possibly differently-sized canvas" recipe: move the origin to the
            // new canvas's center, rotate the coordinate system, then shift back by the ORIGINAL bitmap's
            // half-extent before drawing it at (0,0) — so the original's own center lands exactly on the
            // new canvas's center once the rotation is applied.
            canvas.Clear(SKColors.White);
            canvas.Translate(newWidth / 2f, newHeight / 2f);
            canvas.RotateDegrees(normalized);
            canvas.Translate(-original.Width / 2f, -original.Height / 2f);
            canvas.DrawBitmap(original, 0, 0);
        }

        var format = string.Equals(Path.GetExtension(imagePath), ".png", StringComparison.OrdinalIgnoreCase)
            ? SKEncodedImageFormat.Png
            : SKEncodedImageFormat.Jpeg;
        using var data = rotated.Encode(format, 92);
        using var stream = File.Create(imagePath);
        data.SaveTo(stream);

        return (newWidth, newHeight);
    }
}
