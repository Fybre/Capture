using Capture.Core.Import;

namespace Capture.Tests;

public class ImportFormatsTests
{
    [Theory]
    [InlineData("invoice.pdf", true)]
    [InlineData("scan.PNG", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("photo.jpeg", true)]
    [InlineData("page.bmp", true)]
    [InlineData("multi.tif", true)]
    [InlineData("multi.tiff", true)]
    [InlineData("notes.txt", false)]
    [InlineData("doc.docx", false)]
    public void IsSupported_matches_known_extensions(string fileName, bool expected)
    {
        Assert.Equal(expected, ImportFormats.IsSupported(fileName));
    }

    [Fact]
    public void IsPdf_only_matches_pdf()
    {
        Assert.True(ImportFormats.IsPdf("a.PDF"));
        Assert.False(ImportFormats.IsPdf("a.png"));
    }
}
