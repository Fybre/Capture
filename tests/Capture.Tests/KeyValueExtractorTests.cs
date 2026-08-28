using Capture.Core.Lattice;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class KeyValueExtractorTests
{
    [Fact]
    public void Extracts_value_after_key()
    {
        var lattice = InvoiceLattice();
        var field = new IndexField
        {
            Kind = FieldKind.KeyValue,
            KeyPattern = @"Invoice\s*No",
            ValuePattern = @"\d+",
            PageScope = PageScope.First,
            Occurrence = MatchOccurrence.First
        };

        var result = KeyValueExtractor.Extract([lattice], field);

        Assert.Equal("00001521", result.Text);
        Assert.NotNull(result.Bounds);
        Assert.True(result.Bounds!.X > 0.3f);
    }

    [Fact]
    public void Last_occurrence_wins()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words =
            [
                Word("Ref", 0.05f, 0.10f),
                Word("A1", 0.20f, 0.10f),
                Word("Ref", 0.05f, 0.20f),
                Word("B2", 0.20f, 0.20f)
            ]
        };

        var field = new IndexField
        {
            Kind = FieldKind.KeyValue,
            KeyPattern = "Ref",
            ValuePattern = @"[A-Z]\d",
            Occurrence = MatchOccurrence.Last,
            PageScope = PageScope.First
        };

        var result = KeyValueExtractor.Extract([lattice], field);
        Assert.Equal("B2", result.Text);
    }

    [Fact]
    public void Search_zone_limits_key_value()
    {
        var lattice = new PageLattice
        {
            PageNumber = 1,
            Words =
            [
                Word("Ref", 0.05f, 0.10f),
                Word("A1", 0.20f, 0.10f),
                Word("Ref", 0.05f, 0.80f),
                Word("B2", 0.20f, 0.80f)
            ]
        };

        var field = new IndexField
        {
            Kind = FieldKind.KeyValue,
            KeyPattern = "Ref",
            ValuePattern = @"[A-Z]\d",
            PageScope = PageScope.Number,
            PageNumber = 1,
            SearchZone = new ZoneRect { PageNumber = 1, X = 0, Y = 0.70f, Width = 0.5f, Height = 0.2f }
        };

        var result = KeyValueExtractor.Extract([lattice], field);
        Assert.Equal("B2", result.Text);
    }

    [Fact]
    public void Invalid_regex_returns_empty()
    {
        var result = KeyValueExtractor.Extract([InvoiceLattice()], new IndexField
        {
            Kind = FieldKind.KeyValue,
            KeyPattern = "[",
            ValuePattern = @"\d+",
            PageScope = PageScope.First
        });

        Assert.Equal(string.Empty, result.Text);
    }

    private static PageLattice InvoiceLattice() => new()
    {
        PageNumber = 1,
        Words =
        [
            Word("Invoice", 0.10f, 0.10f),
            Word("No", 0.22f, 0.10f),
            Word("00001521", 0.40f, 0.10f),
            Word("Total", 0.10f, 0.50f)
        ]
    };

    private static LatticeWord Word(string text, float x, float y) => new()
    {
        Text = text,
        Confidence = 90,
        X = x,
        Y = y,
        Width = 0.10f,
        Height = 0.03f
    };
}
