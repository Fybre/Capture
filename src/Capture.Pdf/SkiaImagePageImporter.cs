using Capture.Core.Import;
using Capture.Core.Models;
using SkiaSharp;

namespace Capture.Pdf;

public sealed class SkiaImagePageImporter : IImagePageImporter
{
    public Task<IReadOnlyList<RasterPage>> ImportAsync(
        string imagePath,
        string outputDirectory,
        CancellationToken cancellationToken = default,
        int? dpiOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        return Task.Run(() =>
        {
            using var stream = File.OpenRead(imagePath);
            using var codec = SKCodec.Create(stream)
                ?? throw new InvalidOperationException($"Unable to decode '{Path.GetFileName(imagePath)}'.");

            var info = codec.Info;
            var frameCount = Math.Max(1, codec.FrameCount);
            var pages = new List<RasterPage>(frameCount);

            for (var i = 0; i < frameCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var bitmap = new SKBitmap(info);
                var result = frameCount == 1
                    ? codec.GetPixels(bitmap.Info, bitmap.GetPixels())
                    : codec.GetPixels(bitmap.Info, bitmap.GetPixels(), new SKCodecOptions(i));

                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                    throw new InvalidOperationException(
                        $"Unable to decode page {i + 1} of '{Path.GetFileName(imagePath)}' ({result}).");

                var outPath = Path.Combine(outputDirectory, $"{i + 1:D4}.png");
                using (var output = File.Create(outPath))
                {
                    if (!bitmap.Encode(output, SKEncodedImageFormat.Png, 90))
                        throw new InvalidOperationException($"Unable to write page image '{outPath}'.");
                }

                pages.Add(new RasterPage(i + 1, outPath, bitmap.Width, bitmap.Height, dpiOverride is > 0 ? dpiOverride.Value : 96));
            }

            return (IReadOnlyList<RasterPage>)pages;
        }, cancellationToken);
    }
}
