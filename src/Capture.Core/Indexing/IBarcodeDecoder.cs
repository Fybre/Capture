using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public sealed record BarcodeReadResult(string Text, string Format, float Confidence, ZoneRect? Bounds = null);

public interface IBarcodeDecoder
{
    BarcodeReadResult? Decode(string imagePath, ZoneRect? zone);
}
