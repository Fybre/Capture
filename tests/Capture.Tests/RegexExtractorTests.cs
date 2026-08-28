using Capture.Core.Lattice;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class RegexExtractorTests
{
    [Fact]
    public void Uses_first_capturing_group()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words =
            [
                Word("PO", 0.10f, 0.10f),
                Word("00001521", 0.22f, 0.10f),
                Word("Date", 0.10f, 0.40f)
            ]
        };

        var result = RegexExtractor.Extract([lattice], new IndexField
        {
            Kind = FieldKind.Regex,
            ValuePattern = @"PO\s+(\d+)",
            PageScope = PageScope.First
        });

        Assert.Equal("00001521", result.Text);
        Assert.NotNull(result.Bounds);
    }

    [Fact]
    public void Whole_match_when_no_group()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words = [Word("ABC123", 0.10f, 0.10f)]
        };

        var result = RegexExtractor.Extract([lattice], new IndexField
        {
            Kind = FieldKind.Regex,
            ValuePattern = @"[A-Z]+\d+",
            PageScope = PageScope.First
        });

        Assert.Equal("ABC123", result.Text);
    }

    [Fact]
    public void Search_zone_limits_match()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words =
            [
                Word("INV-1", 0.10f, 0.10f),
                Word("INV-2", 0.10f, 0.80f)
            ]
        };

        var field = new IndexField
        {
            Kind = FieldKind.Regex,
            ValuePattern = @"INV-\d",
            PageScope = PageScope.Number,
            PageNumber = 1,
            SearchZone = new ZoneRect { PageNumber = 1, X = 0.05f, Y = 0.70f, Width = 0.3f, Height = 0.2f }
        };

        var result = RegexExtractor.Extract([lattice], field);
        Assert.Equal("INV-2", result.Text);
    }

    private static LatticeWord Word(string text, float x, float y) => new()
    {
        Text = text,
        Confidence = 90,
        X = x,
        Y = y,
        Width = 0.12f,
        Height = 0.04f
    };
}
