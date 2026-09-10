using Capture.Core.Lattice;
using Capture.Core.Profiles;

namespace Capture.Tests;

// PagePreview's "drag/click to grab text from the document" gesture composes CenterInside +
// InReadingOrder + Union exactly the way these tests exercise — the same predicate/ordering
// ZonalExtractor already uses for profile-defined zones (see ZonalExtractorTests), plus Union to
// snap the picked highlight to the actual matched words rather than the raw drag rectangle.
public class LatticeLayoutSelectionTests
{
    [Fact]
    public void Words_inside_a_dragged_rectangle_join_in_reading_order_with_snapped_bounds()
    {
        var words = new[]
        {
            new LatticeWord { Text = "No", Confidence = 90, X = 0.25f, Y = 0.10f, Width = 0.08f, Height = 0.03f },
            new LatticeWord { Text = "Invoice", Confidence = 95, X = 0.10f, Y = 0.10f, Width = 0.12f, Height = 0.03f },
            new LatticeWord { Text = "00001521", Confidence = 88, X = 0.10f, Y = 0.16f, Width = 0.20f, Height = 0.03f },
            new LatticeWord { Text = "Outside", Confidence = 99, X = 0.80f, Y = 0.80f, Width = 0.10f, Height = 0.03f }
        };
        var drag = new ZoneRect { X = 0.05f, Y = 0.08f, Width = 0.40f, Height = 0.14f };

        var matched = words.Where(word => LatticeLayout.CenterInside(word, drag)).ToList();
        var ordered = LatticeLayout.InReadingOrder(matched);
        var text = string.Join(' ', ordered.Select(word => word.Text));
        var bounds = LatticeLayout.Union(ordered);

        Assert.Equal("Invoice No 00001521", text);
        Assert.NotNull(bounds);
        // Snapped to the matched words' own extent, not the (larger) drag rectangle.
        Assert.Equal(0.10f, bounds!.X, 3);
        Assert.Equal(0.10f, bounds.Y, 3);
        Assert.Equal(0.23f, bounds.Width, 3);
        Assert.Equal(0.09f, bounds.Height, 3);
    }

    [Fact]
    public void A_click_sized_rectangle_over_no_words_yields_no_match()
    {
        var words = new[]
        {
            new LatticeWord { Text = "Hello", Confidence = 100, X = 0.5f, Y = 0.5f, Width = 0.1f, Height = 0.05f }
        };
        var drag = new ZoneRect { X = 0, Y = 0, Width = 0.05f, Height = 0.05f };

        var matched = words.Where(word => LatticeLayout.CenterInside(word, drag)).ToList();

        Assert.Empty(matched);
    }
}
