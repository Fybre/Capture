namespace Capture.Core.Import;

/// <summary>Finds and removes a small tilt in a scanned page image — the few degrees a sheet picks up
/// going through a feeder or lying crooked on the glass. Turning a page that's upside down or sideways is
/// a separate step (it needs OCR to tell which way up reads), done with <see cref="IImageRotator"/>.</summary>
public interface IPageStraightener
{
    /// <summary>How far the page's lines of text are turned clockwise, in degrees — 0 when there's no
    /// clear tilt (or too little on the page to tell). Looks within ±<see cref="MaxSkewDegrees"/>.</summary>
    double DetectSkew(string imagePath);

    /// <summary>Rotates the image at <paramref name="imagePath"/> in place by any angle, clockwise, about
    /// its centre, keeping its size (so the page stays its paper size) and filling the uncovered corners
    /// white. Returns its size.</summary>
    (int Width, int Height) Rotate(string imagePath, double degreesClockwise);

    const double MaxSkewDegrees = 10;
}
