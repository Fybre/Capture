using Capture.Pdf;

namespace Capture.Tests;

public class PdfTextExtractorTests
{
    [Fact]
    public async Task Vector_invoice_page_extracts_real_text()
    {
        var sample = Environment.GetEnvironmentVariable("CAPTURE_TEST_VECTOR_PDF");
        if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
            return;

        var extractor = new PdfPigTextExtractor();
        var words = await extractor.TryExtractPageAsync(sample, 2);

        Assert.NotNull(words);
        Assert.NotEmpty(words);
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
        var sample = Environment.GetEnvironmentVariable("CAPTURE_TEST_RASTER_PDF");
        if (string.IsNullOrWhiteSpace(sample) || !File.Exists(sample))
            return;

        var extractor = new PdfPigTextExtractor();
        var words = await extractor.TryExtractPageAsync(sample, 1);
        Assert.Null(words);
    }
}
