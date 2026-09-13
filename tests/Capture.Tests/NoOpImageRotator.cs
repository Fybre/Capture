using Capture.Core.Import;

namespace Capture.Tests;

/// <summary>Reports the dimension swap a real rotation would produce, without touching the file — most
/// PageManagementService tests care about orchestration (which pages got flagged, what got invalidated),
/// not the actual pixel transform, which SkiaImageRotator (Capture.Pdf) covers on its own.</summary>
internal sealed class NoOpImageRotator : IImageRotator
{
    public (int Width, int Height) Rotate(string imagePath, int degreesClockwise)
    {
        var normalized = ((degreesClockwise % 360) + 360) % 360;
        return normalized is 90 or 270 ? (200, 100) : (100, 200);
    }
}
