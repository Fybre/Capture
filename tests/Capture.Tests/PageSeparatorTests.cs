using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class PageSeparatorTests
{
    [Fact]
    public void Splits_on_barcode_keeping_separator_page()
    {
        var profile = new ImportProfile { Trigger = ImportSeparationTrigger.Barcode };
        var pages = Pages("a.png", "b.png", "c.png");
        var decoder = new MapDecoder
        {
            ["a.png"] = "DOC-1",
            ["c.png"] = "DOC-2"
        };

        var splits = PageSeparator.Split(pages, profile, decoder, blanks: null);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1, 2], splits[0].SourcePages);
        Assert.Equal([3], splits[1].SourcePages);
    }

    [Fact]
    public void Discards_separator_page_when_configured()
    {
        var profile = new ImportProfile
        {
            Trigger = ImportSeparationTrigger.Barcode,
            DiscardSeparatorPage = true
        };
        var pages = Pages("sep.png", "body.png");
        var decoder = new MapDecoder { ["sep.png"] = "DOC-1" };

        var splits = PageSeparator.Split(pages, profile, decoder, blanks: null);

        var split = Assert.Single(splits);
        Assert.Equal([2], split.SourcePages);
    }

    [Fact]
    public void Barcode_trigger_with_no_matching_hit_leaves_the_file_unsplit()
    {
        var profile = new ImportProfile { Trigger = ImportSeparationTrigger.Barcode };
        var pages = Pages("a.png", "b.png");

        var splits = PageSeparator.Split(pages, profile, new MapDecoder(), blanks: null);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    [Fact]
    public void Barcode_format_filter_excludes_non_matching_decodes()
    {
        var profile = new ImportProfile { Trigger = ImportSeparationTrigger.Barcode, BarcodeFormat = "QR_CODE" };
        var pages = Pages("a.png", "b.png");
        var decoder = new MapDecoder { ["a.png"] = "DOC-1" }; // MapDecoder always reports CODE_128

        var splits = PageSeparator.Split(pages, profile, decoder, blanks: null);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    [Fact]
    public void Splits_on_blank_pages_and_drops_them_by_default()
    {
        var profile = new ImportProfile { Trigger = ImportSeparationTrigger.BlankPage, DiscardSeparatorPage = true };
        var pages = Pages("a.png", "blank.png", "b.png");
        var blanks = new SetBlanks { "blank.png" };

        var splits = PageSeparator.Split(pages, profile, barcodes: null, blanks);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([3], splits[1].SourcePages);
    }

    [Fact]
    public void Splits_on_blank_pages_and_keeps_them_when_configured()
    {
        var profile = new ImportProfile { Trigger = ImportSeparationTrigger.BlankPage, DiscardSeparatorPage = false };
        var pages = Pages("a.png", "blank.png", "b.png");
        var blanks = new SetBlanks { "blank.png" };

        var splits = PageSeparator.Split(pages, profile, barcodes: null, blanks);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([2, 3], splits[1].SourcePages);
    }

    [Fact]
    public void Splits_every_n_pages()
    {
        var profile = new ImportProfile { Trigger = ImportSeparationTrigger.EveryNPages, PageCount = 2 };
        var pages = Pages("a.png", "b.png", "c.png", "d.png", "e.png");

        var splits = PageSeparator.Split(pages, profile, barcodes: null, blanks: null);

        Assert.Equal(3, splits.Count);
        Assert.Equal([1, 2], splits[0].SourcePages);
        Assert.Equal([3, 4], splits[1].SourcePages);
        Assert.Equal([5], splits[2].SourcePages);
    }

    [Fact]
    public void No_trigger_leaves_the_file_unsplit()
    {
        var profile = new ImportProfile();
        var pages = Pages("a.png", "b.png");

        var splits = PageSeparator.Split(pages, profile, barcodes: null, blanks: null);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    private static IReadOnlyList<RasterPage> Pages(params string[] names) =>
        names.Select((name, index) => new RasterPage(index + 1, name, 10, 10, 96)).ToList();

    private sealed class MapDecoder : Dictionary<string, string>, IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            TryGetValue(imagePath, out var text) ? new BarcodeReadResult(text, "CODE_128", 99) : null;
    }

    private sealed class SetBlanks : HashSet<string>, IBlankPageDetector
    {
        public bool IsBlank(string imagePath, float maxInkPercent) => Contains(imagePath);
    }
}
