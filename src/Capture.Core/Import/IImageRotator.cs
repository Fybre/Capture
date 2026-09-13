namespace Capture.Core.Import;

/// <summary>Rotates a page image file in place, clockwise, by a multiple of 90 degrees.</summary>
public interface IImageRotator
{
    /// <summary>Rotates the image at <paramref name="imagePath"/> in place and returns its new
    /// (Width, Height) in pixels — swapped for a 90 or 270 degree rotation, unchanged for 180.
    /// <paramref name="degreesClockwise"/> must be 90, 180, or 270.</summary>
    (int Width, int Height) Rotate(string imagePath, int degreesClockwise);
}
