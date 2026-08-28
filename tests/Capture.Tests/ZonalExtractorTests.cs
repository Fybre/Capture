using Capture.Core.Lattice;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class ZonalExtractorTests
{
    [Fact]
    public void Concatenates_words_in_reading_order()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            PixelWidth = 1000,
            PixelHeight = 1000,
            Dpi = 150,
            Source = LatticeSource.PdfText,
            Words =
            [
                new LatticeWord { Text = "No", Confidence = 90, X = 0.25f, Y = 0.10f, Width = 0.08f, Height = 0.03f },
                new LatticeWord { Text = "Invoice", Confidence = 95, X = 0.10f, Y = 0.10f, Width = 0.12f, Height = 0.03f },
                new LatticeWord { Text = "00001521", Confidence = 88, X = 0.10f, Y = 0.16f, Width = 0.20f, Height = 0.03f },
                new LatticeWord { Text = "Outside", Confidence = 99, X = 0.80f, Y = 0.80f, Width = 0.10f, Height = 0.03f }
            ]
        };

        var result = ZonalExtractor.Extract(lattice, new ZoneRect
        {
            PageNumber = 1,
            X = 0.05f,
            Y = 0.08f,
            Width = 0.40f,
            Height = 0.14f
        });

        Assert.Equal("Invoice No 00001521", result.Text);
        Assert.InRange(result.Confidence, 88, 95);
    }

    [Fact]
    public void Empty_zone_returns_empty()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words = [new LatticeWord { Text = "Hello", Confidence = 100, X = 0.5f, Y = 0.5f, Width = 0.1f, Height = 0.05f }]
        };

        var result = ZonalExtractor.Extract(lattice, new ZoneRect { X = 0, Y = 0, Width = 0.1f, Height = 0.1f });
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(0, result.Confidence);
    }
}
