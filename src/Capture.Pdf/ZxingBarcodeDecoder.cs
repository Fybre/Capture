using Capture.Core.Indexing;
using Capture.Core.Profiles;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Capture.Pdf;

public sealed class ZxingBarcodeDecoder : IBarcodeDecoder
{
    public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return null;

        using var bitmap = SKBitmap.Decode(imagePath);
        if (bitmap is null)
            return null;

        using var cropped = Crop(bitmap, zone);
        var result = Read(cropped);
        if (result is null && (cropped.Width > 1600 || cropped.Height > 1600))
        {
            using var smaller = cropped.Resize(new SKImageInfo(cropped.Width / 2, cropped.Height / 2), SKSamplingOptions.Default);
            if (smaller is not null)
            {
                var scaled = Read(smaller);
                if (scaled is not null)
                    return ToResult(scaled, smaller.Width, smaller.Height, zone);
            }
        }

        return result is null ? null : ToResult(result, cropped.Width, cropped.Height, zone);
    }

    private static Result? Read(SKBitmap bitmap)
    {
        var source = ToLuminance(bitmap);
        var reader = CreateReader();
        var result = reader.Decode(source);
        if (result is not null && !string.IsNullOrWhiteSpace(result.Text))
            return result;

        var many = reader.DecodeMultiple(source);
        return many?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item.Text));
    }

    private static BarcodeReaderGeneric CreateReader() => new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            TryHarder = true,
            TryInverted = true,
            PossibleFormats =
            [
                BarcodeFormat.QR_CODE,
                BarcodeFormat.CODE_128,
                BarcodeFormat.CODE_39,
                BarcodeFormat.CODE_93,
                BarcodeFormat.EAN_13,
                BarcodeFormat.EAN_8,
                BarcodeFormat.UPC_A,
                BarcodeFormat.UPC_E,
                BarcodeFormat.ITF,
                BarcodeFormat.CODABAR,
                BarcodeFormat.DATA_MATRIX,
                BarcodeFormat.PDF_417,
                BarcodeFormat.AZTEC
            ]
        }
    };

    private static BarcodeReadResult ToResult(Result result, int width, int height, ZoneRect? zone) =>
        new(result.Text, result.BarcodeFormat.ToString(), 95, ToZone(result, width, height, zone));

    private static ZoneRect? ToZone(Result result, int width, int height, ZoneRect? crop)
    {
        var points = result.ResultPoints?.Where(point => point is not null).ToList();
        if (points is null || points.Count == 0 || width <= 0 || height <= 0)
            return crop;

        var minX = points.Min(point => point.X);
        var maxX = points.Max(point => point.X);
        var minY = points.Min(point => point.Y);
        var maxY = points.Max(point => point.Y);
        var padX = Math.Max(6, (maxX - minX) * 0.12f);
        var padY = Math.Max(10, Math.Max(maxY - minY, (maxX - minX) * 0.25f) * 0.4f);
        minX = Math.Clamp(minX - padX, 0, width);
        minY = Math.Clamp(minY - padY, 0, height);
        maxX = Math.Clamp(maxX + padX, 0, width);
        maxY = Math.Clamp(maxY + padY, 0, height);

        var nx = minX / width;
        var ny = minY / height;
        var nw = Math.Max(0.004f, (maxX - minX) / width);
        var nh = Math.Max(0.004f, (maxY - minY) / height);
        if (crop is null)
        {
            return new ZoneRect
            {
                X = nx,
                Y = ny,
                Width = nw,
                Height = nh
            };
        }

        return new ZoneRect
        {
            PageNumber = crop.PageNumber,
            X = crop.X + nx * crop.Width,
            Y = crop.Y + ny * crop.Height,
            Width = nw * crop.Width,
            Height = nh * crop.Height
        };
    }

    private static SKBitmap Crop(SKBitmap source, ZoneRect? zone)
    {
        if (zone is null || zone.Width <= 0 || zone.Height <= 0)
        {
            var copy = new SKBitmap(source.Width, source.Height);
            source.CopyTo(copy);
            return copy;
        }

        var x = (int)Math.Clamp(Math.Round(zone.X * source.Width), 0, Math.Max(0, source.Width - 1));
        var y = (int)Math.Clamp(Math.Round(zone.Y * source.Height), 0, Math.Max(0, source.Height - 1));
        var width = (int)Math.Clamp(Math.Round(zone.Width * source.Width), 1, source.Width - x);
        var height = (int)Math.Clamp(Math.Round(zone.Height * source.Height), 1, source.Height - y);
        var dest = new SKBitmap(width, height);
        using var canvas = new SKCanvas(dest);
        canvas.DrawBitmap(source, SKRect.Create(x, y, width, height), SKRect.Create(0, 0, width, height));
        return dest;
    }

    private static RGBLuminanceSource ToLuminance(SKBitmap bitmap)
    {
        var pixels = bitmap.Pixels;
        var rgb = new byte[pixels.Length * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            var color = pixels[i];
            var offset = i * 4;
            rgb[offset] = color.Red;
            rgb[offset + 1] = color.Green;
            rgb[offset + 2] = color.Blue;
            rgb[offset + 3] = color.Alpha;
        }

        return new RGBLuminanceSource(rgb, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
    }
}
