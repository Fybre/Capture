using BitMiracle.LibTiff.Classic;
using SkiaSharp;

namespace Capture.Pdf;

/// <summary>Writes page images into one multi-page TIFF — lossless (Deflate), greyscale pages kept
/// greyscale, each page's resolution recorded — so a scanner pass imports as one file with the profile
/// deciding where its documents start, the way a multi-page PDF or TIFF import does.</summary>
public static class TiffPageWriter
{
    // A classic TIFF can't pass 4 GB. Compressed pages are never meaningfully bigger than raw, so a
    // pass whose raw pixels stay under this fits; past it (around 130 colour pages at 300 dpi) the file
    // is written as BigTIFF, which LibTiff reads back the same way.
    public const long ClassicTiffRawLimit = 3_500_000_000;

    /// <summary>Writes <paramref name="pages"/> (image file and its dpi) to
    /// <paramref name="outputPath"/>, in order.</summary>
    public static void Write(IReadOnlyList<(string ImagePath, int Dpi)> pages, string outputPath) =>
        Write(pages, outputPath, ClassicTiffRawLimit);

    /// <summary>As <see cref="Write(IReadOnlyList{ValueTuple{string, int}}, string)"/>, writing BigTIFF
    /// once the pages' raw size passes <paramref name="classicRawLimit"/>.</summary>
    public static void Write(IReadOnlyList<(string ImagePath, int Dpi)> pages, string outputPath, long classicRawLimit)
    {
        if (pages.Count == 0)
            throw new ArgumentException("There are no pages to write.", nameof(pages));

        using (var tiff = Tiff.Open(outputPath, RawBytes(pages) > classicRawLimit ? "w8" : "w")
                   ?? throw new InvalidOperationException($"Unable to create '{Path.GetFileName(outputPath)}'."))
        {
            for (var index = 0; index < pages.Count; index++)
            {
                var (imagePath, dpi) = pages[index];
                using var codec = SKCodec.Create(imagePath)
                    ?? throw new InvalidOperationException($"Unable to decode '{Path.GetFileName(imagePath)}'.");
                var gray = codec.Info.ColorType == SKColorType.Gray8;
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
                    gray ? SKColorType.Gray8 : SKColorType.Rgba8888, SKAlphaType.Unpremul);
                using var bitmap = new SKBitmap(info);
                var result = codec.GetPixels(info, bitmap.GetPixels());
                if (result != SKCodecResult.Success && result != SKCodecResult.IncompleteInput)
                    throw new InvalidOperationException($"Unable to decode '{Path.GetFileName(imagePath)}' ({result}).");

                WritePage(tiff, bitmap, gray, dpi, index, pages.Count);
            }
        }
    }

    private static long RawBytes(IReadOnlyList<(string ImagePath, int Dpi)> pages) =>
        pages.Sum(page =>
        {
            using var codec = SKCodec.Create(page.ImagePath);
            return codec is null ? 0L : (long)codec.Info.Width * codec.Info.Height * (codec.Info.ColorType == SKColorType.Gray8 ? 1 : 3);
        });

    private static void WritePage(Tiff tiff, SKBitmap bitmap, bool gray, int dpi, int index, int count)
    {
        var width = bitmap.Width;
        var height = bitmap.Height;
        var samples = gray ? 1 : 3;
        tiff.SetField(TiffTag.SUBFILETYPE, FileType.PAGE);
        tiff.SetField(TiffTag.PAGENUMBER, index, count);
        tiff.SetField(TiffTag.IMAGEWIDTH, width);
        tiff.SetField(TiffTag.IMAGELENGTH, height);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, samples);
        tiff.SetField(TiffTag.PHOTOMETRIC, gray ? Photometric.MINISBLACK : Photometric.RGB);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.COMPRESSION, Compression.ADOBE_DEFLATE);
        tiff.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL);
        tiff.SetField(TiffTag.ROWSPERSTRIP, tiff.DefaultStripSize(0));
        tiff.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
        if (dpi > 0)
        {
            tiff.SetField(TiffTag.XRESOLUTION, (double)dpi);
            tiff.SetField(TiffTag.YRESOLUTION, (double)dpi);
            tiff.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
        }

        var pixels = bitmap.Bytes;
        var row = new byte[width * samples];
        for (var y = 0; y < height; y++)
        {
            if (gray)
            {
                Buffer.BlockCopy(pixels, y * bitmap.RowBytes, row, 0, width);
            }
            else
            {
                // RGBA in, RGB out: scanners don't produce transparency.
                var source = y * bitmap.RowBytes;
                for (var x = 0; x < width; x++)
                {
                    row[x * 3] = pixels[source + x * 4];
                    row[x * 3 + 1] = pixels[source + x * 4 + 1];
                    row[x * 3 + 2] = pixels[source + x * 4 + 2];
                }
            }

            if (!tiff.WriteScanline(row, y))
                throw new InvalidOperationException($"Unable to write page {index + 1} of the scan.");
        }

        if (!tiff.WriteDirectory())
            throw new InvalidOperationException($"Unable to write page {index + 1} of the scan.");
    }
}
