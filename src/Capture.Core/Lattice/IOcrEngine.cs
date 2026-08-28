namespace Capture.Core.Lattice;

public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(string imagePath, CancellationToken cancellationToken = default);
}
