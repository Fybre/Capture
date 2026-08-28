using Capture.Pdf;

namespace Capture.Tests;

public class PdfTextExtractorTests
{
    [Fact]
    public async Task Vector_invoice_page_extracts_real_text()
    {
        var sample = "/Users/craig/Downloads/18.09.2026.SCM.PO2089 SCA Payment August 1st shipment.pdf";
        if (!File.Exists(sample))
            return;

        var extractor = new PdfPigTextExtractor();
        var words = await extractor.TryExtractPageAsync(sample, 2);

        Assert.NotNull(words);
        Assert.NotEmpty(words);
        var text = string.Join(' ', words!.Select(word => word.Text));
        Assert.Contains("INVOICE", text, StringComparison.OrdinalIgnoreCase);
        Assert.All(words, word =>
        {
            Assert.InRange(word.X, 0, 1);
            Assert.InRange(word.Y, 0, 1);
            Assert.True(word.Width > 0);
            Assert.True(word.Height > 0);
        });
    }

    [Fact]
    public async Task Raster_payment_slip_has_no_usable_pdf_text()
    {
        var sample = "/Users/craig/Downloads/Sample SCDQ PO.pdf";
        if (!File.Exists(sample))
            return;

        var extractor = new PdfPigTextExtractor();
        var words = await extractor.TryExtractPageAsync(sample, 1);
        Assert.Null(words);
    }
}
