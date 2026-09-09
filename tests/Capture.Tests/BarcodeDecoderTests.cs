using Capture.Core.Profiles;
using Capture.Pdf;
using SkiaSharp;
using ZXing;
using ZXing.QrCode;

namespace Capture.Tests;

public class BarcodeDecoderTests
{
    [Fact]
    public void Decodes_qr_code_from_page_and_zone()
    {
        var path = WriteQr("PO-4421");
        var decoder = new ZxingBarcodeDecoder();

        var full = decoder.Decode(path, zone: null);
        Assert.Equal("PO-4421", full?.Text);

        var zoned = decoder.Decode(path, new ZoneRect { X = 0.1f, Y = 0.1f, Width = 0.8f, Height = 0.8f });
        Assert.Equal("PO-4421", zoned?.Text);
    }

    private static string WriteQr(string text)
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new QrCodeEncodingOptions
            {
                Width = 240,
                Height = 240,
                Margin = 2
            }
        };
        var data = writer.Write(text);
        using var bitmap = new SKBitmap(new SKImageInfo(data.Width, data.Height, SKColorType.Bgra8888));
        var dest = bitmap.GetPixelSpan();
        data.Pixels.AsSpan().CopyTo(dest);

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
        using var output = File.Create(path);
        bitmap.Encode(output, SKEncodedImageFormat.Png, 100);
        return path;
    }
}
