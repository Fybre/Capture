using System.Runtime.InteropServices;
using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace Capture.Pdf;

/// <summary>Reads TIFF pages, which SkiaSharp can't decode: every page of a multi-page TIFF, in any of
/// the compressions scanners write (CCITT Group 3/4 fax, LZW, JPEG, PackBits, uncompressed), with the
/// resolution the scanner recorded.</summary>
internal static class TiffPageReader
{
    static TiffPageReader()
    {
        // LibTiff writes warnings and errors straight to the console (an unreadable thumbnail directory
        // is an "error"); failures that matter still surface as a failed open or read below.
        Tiff.SetErrorHandler(new QuietErrors());
    }

    /// <summary>True when the file starts with a TIFF signature ("II*\0" or "MM\0*", or BigTIFF's
    /// "II+\0" or "MM\0+"), whatever its extension.</summary>
    public static bool IsTiff(string path)
    {
        Span<byte> header = stackalloc byte[4];
        using var stream = File.OpenRead(path);
        if (stream.Read(header) < 4)
            return false;
        return (header[0] == 'I' && header[1] == 'I' && header[2] is 42 or 43 && header[3] == 0)
               || (header[0] == 'M' && header[1] == 'M' && header[2] == 0 && header[3] is 42 or 43);
    }

    /// <summary>Calls <paramref name="page"/> with each page's bitmap and resolution (dpi, or null when
    /// the file doesn't say) in order. The bitmap is disposed after the call.</summary>
    public static void ReadPages(string path, Action<SKBitmap, int?> page, CancellationToken cancellationToken)
    {
        using var tiff = Tiff.Open(path, "r")
            ?? throw new InvalidOperationException($"Unable to open '{Path.GetFileName(path)}' as a TIFF.");
        var count = Math.Max(1, (int)tiff.NumberOfDirectories());
        var pages = 0;
        for (short index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Not every directory is a page: cameras and some scanners add a thumbnail after the image
            // — marked as reduced-resolution, or an old-style embedded JPEG with no size of its own
            // that LibTiff can't even open as a directory.
            if (!tiff.SetDirectory(index) || !IsPage(tiff, out var width, out var height))
            {
                if (index == 0)
                    throw new InvalidOperationException($"Unable to read '{Path.GetFileName(path)}' — its first image isn't readable.");
                continue;
            }

            var raster = new int[width * height];
            if (!tiff.ReadRGBAImageOriented(width, height, raster, Orientation.TOPLEFT))
                throw new InvalidOperationException($"Unable to decode page {pages + 1} of '{Path.GetFileName(path)}'.");
            pages++;

            // LibTiff packs each pixel as A<<24 | B<<16 | G<<8 | R, which on a little-endian machine is
            // R, G, B, A in memory — Skia's RGBA8888.
            using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
            Marshal.Copy(raster, 0, bitmap.GetPixels(), raster.Length);
            page(bitmap, Resolution(tiff));
        }
    }

    private static bool IsPage(Tiff tiff, out int width, out int height)
    {
        width = tiff.GetField(TiffTag.IMAGEWIDTH)?[0].ToInt() ?? 0;
        height = tiff.GetField(TiffTag.IMAGELENGTH)?[0].ToInt() ?? 0;
        var kind = tiff.GetField(TiffTag.SUBFILETYPE) is { } type ? (FileType)type[0].ToInt() : 0;
        return width > 0 && height > 0 && !kind.HasFlag(FileType.REDUCEDIMAGE);
    }

    private static int? Resolution(Tiff tiff)
    {
        var resolution = tiff.GetField(TiffTag.XRESOLUTION);
        if (resolution is null)
            return null;
        var value = resolution[0].ToDouble();
        var unit = tiff.GetField(TiffTag.RESOLUTIONUNIT) is { } units ? (ResUnit)units[0].ToInt() : ResUnit.INCH;
        var dpi = unit == ResUnit.CENTIMETER ? value * 2.54 : value;
        // "No absolute unit" files and junk values say nothing useful about the scan.
        return unit == ResUnit.NONE || dpi is < 50 or > 2400 ? null : (int)Math.Round(dpi);
    }

    private sealed class QuietErrors : TiffErrorHandler
    {
        public override void ErrorHandler(Tiff tif, string method, string format, params object[] args)
        {
        }

        public override void ErrorHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args)
        {
        }

        public override void WarningHandler(Tiff tif, string method, string format, params object[] args)
        {
        }

        public override void WarningHandlerExt(Tiff tif, object clientData, string method, string format, params object[] args)
        {
        }
    }
}
