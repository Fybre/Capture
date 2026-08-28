namespace Capture.Core.Import;

public interface IBlankPageDetector
{
    bool IsBlank(string imagePath, float maxInkPercent);
}
