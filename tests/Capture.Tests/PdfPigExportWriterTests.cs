using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Pdf;
using SkiaSharp;
using UglyToad.PdfPig;

namespace Capture.Tests;

public class PdfPigExportWriterTests
{
    [Fact]
    public async Task Writes_one_pdf_page_per_input_page_in_order()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var pages = new[]
            {
                Page(directory, 1, SKColors.Red),
                Page(directory, 2, SKColors.Blue),
                Page(directory, 3, SKColors.Green)
            };
            var outputPath = Path.Combine(directory, "export.pdf");

            await NewWriter().WriteAsync(pages, outputPath, compress: false);

            using var document = PdfDocument.Open(outputPath);
            Assert.Equal(3, document.NumberOfPages);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Page_size_is_derived_from_pixel_dimensions_and_dpi_not_raw_pixels()
    {
        // AddPage's MediaBox is in PDF points (1/72"), not pixels — passing bitmap.Width/Height straight
        // through (the shipped bug) would produce a 200x260-point page instead of the correct
        // 96x124.8-point page for a 200x260px image scanned at 150 DPI.
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var outputPath = Path.Combine(directory, "export.pdf");
            await NewWriter().WriteAsync([Page(directory, 1, SKColors.Red)], outputPath, compress: false);

            using var document = PdfDocument.Open(outputPath);
            var page = document.GetPage(1);
            Assert.Equal(96.0, page.Width, precision: 1);
            Assert.Equal(124.8, page.Height, precision: 1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Compressing_produces_a_smaller_file_than_full_quality()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            // A photo-like page (noise, not a flat color) so JPEG's lossy compression actually
            // shrinks it relative to PNG — a flat color already compresses losslessly either way.
            var pages = new[] { NoisyPage(directory, 1) };
            var fullQualityPath = Path.Combine(directory, "full.pdf");
            var compressedPath = Path.Combine(directory, "compressed.pdf");

            var writer = NewWriter();
            await writer.WriteAsync(pages, fullQualityPath, compress: false);
            await writer.WriteAsync(pages, compressedPath, compress: true);

            var fullQualitySize = new FileInfo(fullQualityPath).Length;
            var compressedSize = new FileInfo(compressedPath).Length;
            Assert.True(compressedSize < fullQualitySize,
                $"Expected compressed ({compressedSize} bytes) to be smaller than full quality ({fullQualitySize} bytes).");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Pdfa_option_produces_a_document_with_xmp_metadata()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var outputPath = Path.Combine(directory, "export.pdf");
            await NewWriter().WriteAsync([Page(directory, 1, SKColors.Red)], outputPath, compress: false, pdfa: true);

            using var stream = File.OpenRead(outputPath);
            using var document = PdfDocument.Open(stream);
            Assert.True(document.TryGetXmpMetadata(out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SearchablePdf_option_makes_ocr_words_extractable()
    {
        var directory = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var page = Page(directory, 1, SKColors.Red);
            var lattice = new FakeLatticeStore();
            lattice.Words[(page.DocumentId, 1)] = new PageLattice
            {
                PageNumber = 1,
                PixelWidth = 200,
                PixelHeight = 260,
                Dpi = 150,
                Source = LatticeSource.Ocr,
                Words = [new LatticeWord { Text = "Invoice", X = 10, Y = 10, Width = 80, Height = 20 }]
            };
            var outputPath = Path.Combine(directory, "export.pdf");

            await new PdfPigExportWriter(lattice).WriteAsync([page], outputPath, compress: false, searchablePdf: true);

            using var document = PdfDocument.Open(outputPath);
            Assert.Contains("Invoice", document.GetPage(1).Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static PdfPigExportWriter NewWriter() => new(new FakeLatticeStore());

    private sealed class FakeLatticeStore : ILatticeStore
    {
        public Dictionary<(Guid, int), PageLattice> Words { get; } = new();

        public Task SaveAsync(Guid documentId, PageLattice lattice, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<PageLattice?> GetAsync(Guid documentId, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(Words.GetValueOrDefault((documentId, pageNumber)));
    }

    private static DocumentPage Page(string directory, int number, SKColor color)
    {
        var path = Path.Combine(directory, $"{number:D4}.png");
        using (var bitmap = new SKBitmap(200, 260))
        {
            bitmap.Erase(color);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(path);
            data.SaveTo(stream);
        }
        return new DocumentPage { DocumentId = Guid.NewGuid(), PageNumber = number, SourcePageNumber = number, ImagePath = path, Width = 200, Height = 260, Dpi = 150 };
    }

    private static DocumentPage NoisyPage(string directory, int number)
    {
        var path = Path.Combine(directory, $"{number:D4}.png");
        var random = new Random(42);
        using (var bitmap = new SKBitmap(600, 800))
        {
            for (var y = 0; y < bitmap.Height; y++)
                for (var x = 0; x < bitmap.Width; x++)
                    bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(path);
            data.SaveTo(stream);
        }
        return new DocumentPage { DocumentId = Guid.NewGuid(), PageNumber = number, SourcePageNumber = number, ImagePath = path, Width = 600, Height = 800, Dpi = 150 };
    }
}
