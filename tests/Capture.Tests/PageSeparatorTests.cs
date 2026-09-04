using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class PageSeparatorTests
{
    [Fact]
    public async Task Splits_on_barcode_keeping_separator_page()
    {
        var profile = Profile(Barcode());
        var pages = Pages("a.png", "b.png", "c.png");
        var decoder = new MapDecoder
        {
            ["a.png"] = "DOC-1",
            ["c.png"] = "DOC-2"
        };

        var splits = await PageSeparator.SplitAsync(pages, profile, decoder, blanks: null, latticeProvider: null);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1, 2], splits[0].SourcePages);
        Assert.Equal([3], splits[1].SourcePages);
    }

    [Fact]
    public async Task Discards_separator_page_when_configured()
    {
        var strategy = Barcode();
        strategy.DiscardSeparatorPage = true;
        var profile = Profile(strategy);
        var pages = Pages("sep.png", "body.png");
        var decoder = new MapDecoder { ["sep.png"] = "DOC-1" };

        var splits = await PageSeparator.SplitAsync(pages, profile, decoder, blanks: null, latticeProvider: null);

        var split = Assert.Single(splits);
        Assert.Equal([2], split.SourcePages);
    }

    [Fact]
    public async Task Barcode_trigger_with_no_matching_hit_leaves_the_file_unsplit()
    {
        var profile = Profile(Barcode());
        var pages = Pages("a.png", "b.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, new MapDecoder(), blanks: null, latticeProvider: null);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    [Fact]
    public async Task Barcode_format_filter_excludes_non_matching_decodes()
    {
        var strategy = Barcode();
        strategy.BarcodeFormat = "QR_CODE";
        var profile = Profile(strategy);
        var pages = Pages("a.png", "b.png");
        var decoder = new MapDecoder { ["a.png"] = "DOC-1" }; // MapDecoder always reports CODE_128

        var splits = await PageSeparator.SplitAsync(pages, profile, decoder, blanks: null, latticeProvider: null);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    [Fact]
    public async Task Splits_on_blank_pages_and_drops_them_by_default()
    {
        var strategy = BlankPage();
        strategy.DiscardSeparatorPage = true;
        var profile = Profile(strategy);
        var pages = Pages("a.png", "blank.png", "b.png");
        var blanks = new SetBlanks { "blank.png" };

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks, latticeProvider: null);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([3], splits[1].SourcePages);
    }

    [Fact]
    public async Task Splits_on_blank_pages_and_keeps_them_when_configured()
    {
        var strategy = BlankPage();
        strategy.DiscardSeparatorPage = false;
        var profile = Profile(strategy);
        var pages = Pages("a.png", "blank.png", "b.png");
        var blanks = new SetBlanks { "blank.png" };

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks, latticeProvider: null);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([2, 3], splits[1].SourcePages);
    }

    [Fact]
    public async Task Splits_every_n_pages()
    {
        var profile = Profile(EveryNPages(2));
        var pages = Pages("a.png", "b.png", "c.png", "d.png", "e.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, latticeProvider: null);

        Assert.Equal(3, splits.Count);
        Assert.Equal([1, 2], splits[0].SourcePages);
        Assert.Equal([3, 4], splits[1].SourcePages);
        Assert.Equal([5], splits[2].SourcePages);
    }

    [Fact]
    public async Task Two_independent_EveryNPages_strategies_count_separately()
    {
        // ANY of a PageCount=2 strategy and a PageCount=3 strategy — each keeps counting off its own
        // hits, not off the other strategy's or the combined boundary's.
        var profile = Profile(EveryNPages(2), EveryNPages(3));
        var pages = Pages("a.png", "b.png", "c.png", "d.png", "e.png", "f.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, latticeProvider: null);

        // PageCount=2 strategy hits at page3 (resets to count 1) then again at page5 (count 2 -> hit).
        // PageCount=3 strategy hits at page4 (count 3 -> hit, resets to 1). Each counts only its own
        // hits, so page3/4/5 are each boundaries; page6 isn't (neither strategy has reached its
        // threshold again by then).
        Assert.Equal(4, splits.Count);
        Assert.Equal([1, 2], splits[0].SourcePages);
        Assert.Equal([3], splits[1].SourcePages);
        Assert.Equal([4], splits[2].SourcePages);
        Assert.Equal([5, 6], splits[3].SourcePages);
    }

    [Fact]
    public async Task No_strategies_leaves_the_file_unsplit()
    {
        var profile = new ImportProfile();
        var pages = Pages("a.png", "b.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, latticeProvider: null);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    [Fact]
    public async Task Regex_strategy_splits_on_whole_page_text_match()
    {
        var profile = Profile(Regex("^INVOICE"));
        var pages = Pages("a.png", "b.png", "c.png");
        var lattice = new MapLattice
        {
            ["b.png"] = Lattice("INVOICE 123")
        };

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, lattice.Provide);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([2, 3], splits[1].SourcePages);
    }

    [Fact]
    public async Task Regex_strategy_with_empty_pattern_never_hits()
    {
        var profile = Profile(Regex(null));
        var pages = Pages("a.png", "b.png");
        var lattice = new MapLattice { ["a.png"] = Lattice("anything") };

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, lattice.Provide);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    [Fact]
    public async Task OcrZone_strategy_splits_on_zone_text_match()
    {
        var zone = new ZoneRect { X = 0f, Y = 0f, Width = 0.3f, Height = 0.3f };
        var strategy = OcrZone(zone, "^COVER$");
        var profile = Profile(strategy);
        var pages = Pages("a.png", "b.png");
        var lattice = new MapLattice
        {
            // Inside the zone.
            ["b.png"] = Lattice(("COVER", 0.1f, 0.1f, 0.1f, 0.1f), ("OTHERTEXT", 0.6f, 0.6f, 0.1f, 0.1f))
        };

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, lattice.Provide);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([2], splits[1].SourcePages);
    }

    [Fact]
    public async Task OcrZone_strategy_with_empty_pattern_hits_on_any_zone_text()
    {
        var zone = new ZoneRect { X = 0f, Y = 0f, Width = 0.3f, Height = 0.3f };
        var strategy = OcrZone(zone, textPattern: null);
        var profile = Profile(strategy);
        var pages = Pages("a.png", "b.png");
        var lattice = new MapLattice
        {
            ["b.png"] = Lattice(("ANYTHING", 0.1f, 0.1f, 0.1f, 0.1f))
        };

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, lattice.Provide);

        Assert.Equal(2, splits.Count);
    }

    [Fact]
    public async Task OcrZone_strategy_does_not_hit_when_zone_is_empty()
    {
        var zone = new ZoneRect { X = 0f, Y = 0f, Width = 0.1f, Height = 0.1f };
        var strategy = OcrZone(zone, textPattern: null);
        var profile = Profile(strategy);
        var pages = Pages("a.png", "b.png");
        var lattice = new MapLattice
        {
            // Word is outside the tiny zone, so nothing is extracted.
            ["b.png"] = Lattice(("OUTSIDE", 0.6f, 0.6f, 0.1f, 0.1f))
        };

        var splits = await PageSeparator.SplitAsync(pages, profile, barcodes: null, blanks: null, lattice.Provide);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2], split.SourcePages);
    }

    [Fact]
    public async Task MatchMode_All_requires_every_strategy_to_hit_the_same_page()
    {
        var decoder = new MapDecoder { ["b.png"] = "DOC-1" };
        var lattice = new MapLattice { ["b.png"] = Lattice("INVOICE") };

        // Page b hits both Barcode and Regex — should split there. Page c hits neither.
        var profile = Profile(Barcode(), Regex("INVOICE"));
        profile.MatchMode = SeparationMatchMode.All;
        var pages = Pages("a.png", "b.png", "c.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, decoder, blanks: null, lattice.Provide);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([2, 3], splits[1].SourcePages);
    }

    [Fact]
    public async Task MatchMode_All_does_not_split_when_only_one_strategy_hits()
    {
        var decoder = new MapDecoder { ["b.png"] = "DOC-1" }; // barcode hits, regex never will
        var lattice = new MapLattice(); // no page has matching text

        var profile = Profile(Barcode(), Regex("INVOICE"));
        profile.MatchMode = SeparationMatchMode.All;
        var pages = Pages("a.png", "b.png", "c.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, decoder, blanks: null, lattice.Provide);

        var split = Assert.Single(splits);
        Assert.Equal([1, 2, 3], split.SourcePages);
    }

    [Fact]
    public async Task MatchMode_AtLeast_splits_once_the_minimum_number_of_strategies_hit()
    {
        var decoder = new MapDecoder { ["b.png"] = "DOC-1" };
        var blanks = new SetBlanks(); // never blank on its own
        var lattice = new MapLattice { ["b.png"] = Lattice("INVOICE") };

        // Barcode + Regex both hit page b; BlankPage never hits. AtLeast(2) should still split there.
        var profile = Profile(Barcode(), Regex("INVOICE"), BlankPage());
        profile.MatchMode = SeparationMatchMode.AtLeast;
        profile.MatchMinimum = 2;
        var pages = Pages("a.png", "b.png", "c.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, decoder, blanks, lattice.Provide);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([2, 3], splits[1].SourcePages);
    }

    [Fact]
    public async Task Discard_fires_when_any_contributing_strategy_asks_for_it()
    {
        // Barcode (no discard) and Regex (discard) both hit page b under Any — the page should still
        // be dropped because at least one contributing strategy asked for it.
        var barcode = Barcode();
        var regex = Regex("INVOICE");
        regex.DiscardSeparatorPage = true;
        var decoder = new MapDecoder { ["b.png"] = "DOC-1" };
        var lattice = new MapLattice { ["b.png"] = Lattice("INVOICE") };

        var profile = Profile(barcode, regex);
        profile.MatchMode = SeparationMatchMode.Any;
        var pages = Pages("a.png", "b.png", "c.png");

        var splits = await PageSeparator.SplitAsync(pages, profile, decoder, blanks: null, lattice.Provide);

        Assert.Equal(2, splits.Count);
        Assert.Equal([1], splits[0].SourcePages);
        Assert.Equal([3], splits[1].SourcePages);
    }

    private static ImportProfile Profile(params SeparationStrategy[] strategies) =>
        new() { Strategies = strategies.ToList() };

    private static SeparationStrategy Barcode() => new() { Type = SeparationStrategyType.Barcode };

    private static SeparationStrategy BlankPage() => new() { Type = SeparationStrategyType.BlankPage };

    private static SeparationStrategy EveryNPages(int pageCount) => new() { Type = SeparationStrategyType.EveryNPages, PageCount = pageCount };

    private static SeparationStrategy Regex(string? textPattern) => new() { Type = SeparationStrategyType.Regex, TextPattern = textPattern };

    private static SeparationStrategy OcrZone(ZoneRect zone, string? textPattern) =>
        new() { Type = SeparationStrategyType.OcrZone, Zone = zone, TextPattern = textPattern };

    private static PageLattice Lattice(string wholePageText) =>
        new() { Words = [new LatticeWord { Text = wholePageText, X = 0, Y = 0, Width = 1, Height = 1, Confidence = 99 }] };

    private static PageLattice Lattice(params (string Text, float X, float Y, float Width, float Height)[] words) =>
        new()
        {
            Words = words
                .Select(word => new LatticeWord { Text = word.Text, X = word.X, Y = word.Y, Width = word.Width, Height = word.Height, Confidence = 99 })
                .ToList()
        };

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

    // Maps a RasterPage's ImagePath to a canned PageLattice — pages with no entry get an empty lattice
    // (no words), matching how a real blank/unreadable page would behave.
    private sealed class MapLattice : Dictionary<string, PageLattice>
    {
        public Task<PageLattice> Provide(RasterPage page, CancellationToken cancellationToken) =>
            Task.FromResult(TryGetValue(page.ImagePath, out var lattice) ? lattice : new PageLattice());
    }
}
