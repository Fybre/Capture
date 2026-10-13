using BitMiracle.LibTiff.Classic;
using Capture.Pdf;

namespace Capture.Tests;

public class TiffImportTests
{
    [Fact]
    public async Task Every_page_of_a_group4_tiff_is_imported_at_its_resolution()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-tiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "scan.tif");
            WriteBilevelTiff(path, pages: 2, width: 64, height: 32, dpi: 200);

            var pages = await new SkiaImagePageImporter().ImportAsync(path, Path.Combine(root, "out"));

            Assert.Equal([1, 2], pages.Select(page => page.PageNumber));
            Assert.All(pages, page =>
            {
                Assert.Equal((64, 32, 200), (page.Width, page.Height, page.Dpi));
                Assert.True(File.Exists(page.ImagePath));
            });
            using var first = SkiaSharp.SKBitmap.Decode(pages[0].ImagePath);
            Assert.Equal(SkiaSharp.SKColors.Black, first.GetPixel(0, 0));
            Assert.Equal(SkiaSharp.SKColors.White, first.GetPixel(63, 31));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_thumbnail_after_the_image_is_not_a_page()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-tiff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "photo.tif");
            WriteBilevelTiff(path, pages: 2, width: 64, height: 32, dpi: 200, secondIsThumbnail: true);

            var page = Assert.Single(await new SkiaImagePageImporter().ImportAsync(path, Path.Combine(root, "out")));
            Assert.Equal(64, page.Width);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // Black left half, white right half; MINISWHITE so 1 bits are black, as fax scans store them.
    private static void WriteBilevelTiff(string path, int pages, int width, int height, int dpi, bool secondIsThumbnail = false)
    {
        using var tiff = Tiff.Open(path, "w");
        for (var page = 0; page < pages; page++)
        {
            tiff.SetField(TiffTag.IMAGEWIDTH, width);
            tiff.SetField(TiffTag.IMAGELENGTH, height);
            tiff.SetField(TiffTag.BITSPERSAMPLE, 1);
            tiff.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tiff.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISWHITE);
            tiff.SetField(TiffTag.COMPRESSION, Compression.CCITTFAX4);
            tiff.SetField(TiffTag.ROWSPERSTRIP, height);
            tiff.SetField(TiffTag.XRESOLUTION, (double)dpi);
            tiff.SetField(TiffTag.YRESOLUTION, (double)dpi);
            tiff.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
            tiff.SetField(TiffTag.SUBFILETYPE, secondIsThumbnail && page == 1 ? FileType.REDUCEDIMAGE : FileType.PAGE);
            tiff.SetField(TiffTag.PAGENUMBER, page, pages);
            var row = new byte[width / 8];
            for (var x = 0; x < width / 2; x++)
                row[x / 8] |= (byte)(0x80 >> (x % 8));
            for (var y = 0; y < height; y++)
                tiff.WriteScanline(row, y);
            tiff.WriteDirectory();
        }
    }
}
