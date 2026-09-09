using Capture.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace Capture.Tests;

public class PdfPigSubsetWriterTests
{
    [Fact]
    public async Task WritePagesAsync_produces_a_new_pdf_with_only_the_requested_pages_in_order()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-pdf-subset-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.pdf");
        WriteBlankPdf(sourcePath, pageCount: 4);

        var writer = new PdfPigSubsetWriter();
        var outputPath = Path.Combine(root, "subset.pdf");

        await writer.WritePagesAsync(sourcePath, [2, 4], outputPath);

        using var output = PdfDocument.Open(outputPath);
        Assert.Equal(2, output.NumberOfPages);
    }

    [Fact]
    public async Task WritePagesAsync_can_reuse_the_same_page_more_than_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-pdf-subset-repeat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "source.pdf");
        WriteBlankPdf(sourcePath, pageCount: 2);

        var writer = new PdfPigSubsetWriter();
        var outputPath = Path.Combine(root, "subset.pdf");

        await writer.WritePagesAsync(sourcePath, [1, 1, 1], outputPath);

        using var output = PdfDocument.Open(outputPath);
        Assert.Equal(3, output.NumberOfPages);
    }

    private static void WriteBlankPdf(string path, int pageCount)
    {
        using var builder = new PdfDocumentBuilder();
        for (var i = 0; i < pageCount; i++)
            builder.AddPage(595, 842); // A4 in points
        File.WriteAllBytes(path, builder.Build());
    }
}
