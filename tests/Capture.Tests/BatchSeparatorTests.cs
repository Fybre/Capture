using Capture.Core.Batches;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class BatchSeparatorTests
{
    [Fact]
    public async Task Barcode_strategy_reports_a_hit_for_every_page_that_decodes()
    {
        var profile = Profile(Barcode());
        var pages = Pages("a.png", "b.png", "c.png");
        var decoder = new MapDecoder { ["a.png"] = ("BATCH-1", "CODE_128"), ["c.png"] = ("BATCH-2", "CODE_128") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, latticeProvider: null);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].PageNumber);
        Assert.Equal("BATCH-1", hits[0].CapturedValue);
        Assert.Equal(3, hits[1].PageNumber);
        Assert.Equal("BATCH-2", hits[1].CapturedValue);
    }

    [Fact]
    public async Task Barcode_strategy_filters_by_format_when_configured()
    {
        var strategy = Barcode();
        strategy.BarcodeFormat = "QR_CODE";
        var profile = Profile(strategy);
        var pages = Pages("a.png", "b.png");
        var decoder = new MapDecoder { ["a.png"] = ("BATCH-1", "CODE_128"), ["b.png"] = ("BATCH-2", "QR_CODE") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, latticeProvider: null);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.PageNumber);
        Assert.Equal("BATCH-2", hit.CapturedValue);
    }

    [Fact]
    public async Task Barcode_strategy_filters_by_value_pattern_when_configured()
    {
        var strategy = Barcode();
        strategy.BarcodeValuePattern = "^BATCH-";
        var profile = Profile(strategy);
        var pages = Pages("a.png", "b.png");
        var decoder = new MapDecoder { ["a.png"] = ("NOPE-1", "CODE_128"), ["b.png"] = ("BATCH-2", "CODE_128") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, latticeProvider: null);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.PageNumber);
    }

    [Fact]
    public async Task Barcode_strategy_carries_the_discard_flag_through_to_each_hit()
    {
        var strategy = Barcode();
        strategy.DiscardSeparatorPage = true;
        var profile = Profile(strategy);
        var pages = Pages("a.png");
        var decoder = new MapDecoder { ["a.png"] = ("BATCH-1", "CODE_128") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, latticeProvider: null);

        Assert.True(Assert.Single(hits).DiscardPage);
    }

    [Fact]
    public async Task Regex_strategy_reports_a_hit_only_for_pages_whose_text_matches()
    {
        var profile = Profile(Regex(@"Invoice #(\d+)"));
        var pages = Pages("a.png", "b.png");
        var lattice = new MapLattice
        {
            ["a.png"] = Lattice("just some other text"),
            ["b.png"] = Lattice("Header Invoice #4471 Footer")
        };

        var hits = await BatchSeparator.DetectAsync(pages, profile, barcodes: null, lattice.Provide);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.PageNumber);
        Assert.Equal("4471", hit.CapturedValue);
    }

    [Fact]
    public async Task EveryNPages_strategy_hits_once_the_threshold_is_reached()
    {
        // Threshold=2: the counter starts at 0 and is checked before incrementing, so the hit lands on
        // the 3rd page (count reaches 2 there), not the 2nd — same behavior PageSeparatorTests'
        // Splits_every_n_pages already documents for the identical strategy/evaluator.
        var profile = Profile(EveryNPages(2));
        var pages = Pages("a.png", "b.png", "c.png", "d.png", "e.png");

        var hits = await BatchSeparator.DetectAsync(pages, profile, barcodes: null, latticeProvider: null);

        Assert.Equal([3, 5], hits.Select(hit => hit.PageNumber));
    }

    [Fact]
    public async Task MatchMode_All_only_hits_when_every_strategy_hits_the_same_page()
    {
        var decoder = new MapDecoder { ["b.png"] = ("BATCH-1", "CODE_128") };
        var lattice = new MapLattice { ["b.png"] = Lattice("INVOICE") };

        var profile = Profile(Barcode(), Regex("INVOICE"));
        profile.MatchMode = SeparationMatchMode.All;
        var pages = Pages("a.png", "b.png", "c.png");

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, lattice.Provide);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.PageNumber);
    }

    [Fact]
    public void NewBatchPerFile_and_Manual_never_scan_pages()
    {
        Assert.False(BatchSeparator.NeedsPageScan(new BatchProfile { Mode = BatchMode.NewBatchPerFile }));
        Assert.False(BatchSeparator.NeedsPageScan(new BatchProfile { Mode = BatchMode.Manual }));
        Assert.False(BatchSeparator.NeedsPageScan(new BatchProfile { Mode = BatchMode.UseStrategies }));
        Assert.False(BatchSeparator.NeedsPageScan(null));
    }

    [Fact]
    public void UseStrategies_with_at_least_one_strategy_needs_a_page_scan()
    {
        Assert.True(BatchSeparator.NeedsPageScan(Profile(Barcode())));
    }

    [Fact]
    public void ExpandSplitsAtBoundaries_breaks_a_single_unsplit_document_at_every_batch_trigger_page()
    {
        // Reproduces the reported bug: no indexing profile means ClassicSeparatorStep hands back one
        // split covering the whole file. Two barcode hits fall inside it (pages 1 and 4) — both must
        // still produce their own document/batch instead of only the first one taking effect.
        var wholeFile = new ClassifiedSplit { Profile = null, SourcePages = [1, 2, 3, 4, 5, 6] };
        var hits = new Dictionary<int, BatchTriggerHit>
        {
            [1] = new BatchTriggerHit(1, "BATCH-0001", DiscardPage: true),
            [4] = new BatchTriggerHit(4, "BATCH-0002", DiscardPage: true)
        };

        var expanded = BatchSeparator.ExpandSplitsAtBoundaries([wholeFile], hits);

        Assert.Equal(2, expanded.Count);
        Assert.Equal([1, 2, 3], expanded[0].SourcePages);
        Assert.Equal([4, 5, 6], expanded[1].SourcePages);
    }

    [Fact]
    public void ExpandSplitsAtBoundaries_does_not_split_when_the_hit_is_already_the_first_page()
    {
        var splits = new List<ClassifiedSplit>
        {
            new() { Profile = null, SourcePages = [1, 2, 3] },
            new() { Profile = null, SourcePages = [4, 5, 6] }
        };
        var hits = new Dictionary<int, BatchTriggerHit>
        {
            [1] = new BatchTriggerHit(1, "BATCH-0001", DiscardPage: false),
            [4] = new BatchTriggerHit(4, "BATCH-0002", DiscardPage: false)
        };

        var expanded = BatchSeparator.ExpandSplitsAtBoundaries(splits, hits);

        Assert.Equal(2, expanded.Count);
        Assert.Equal([1, 2, 3], expanded[0].SourcePages);
        Assert.Equal([4, 5, 6], expanded[1].SourcePages);
    }

    [Fact]
    public void ExpandSplitsAtBoundaries_is_a_noop_with_no_hits()
    {
        var splits = new List<ClassifiedSplit> { new() { Profile = null, SourcePages = [1, 2, 3] } };

        var expanded = BatchSeparator.ExpandSplitsAtBoundaries(splits, new Dictionary<int, BatchTriggerHit>());

        Assert.Same(splits, expanded);
    }

    private static BatchProfile Profile(params SeparationStrategy[] strategies) =>
        new() { Mode = BatchMode.UseStrategies, Strategies = strategies.ToList() };

    private static SeparationStrategy Barcode() => new() { Type = SeparationStrategyType.Barcode };

    private static SeparationStrategy EveryNPages(int pageCount) => new() { Type = SeparationStrategyType.EveryNPages, PageCount = pageCount };

    private static SeparationStrategy Regex(string textPattern) => new() { Type = SeparationStrategyType.Regex, TextPattern = textPattern };

    private static PageLattice Lattice(string wholePageText) =>
        new() { Words = [new LatticeWord { Text = wholePageText, X = 0, Y = 0, Width = 1, Height = 1, Confidence = 99 }] };

    private static IReadOnlyList<RasterPage> Pages(params string[] names) =>
        names.Select((name, index) => new RasterPage(index + 1, name, 10, 10, 96)).ToList();

    private sealed class MapDecoder : Dictionary<string, (string Text, string Format)>, IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            TryGetValue(imagePath, out var result) ? new BarcodeReadResult(result.Text, result.Format, 99) : null;
    }

    // Maps a RasterPage's ImagePath to a canned PageLattice — pages with no entry get an empty lattice
    // (no words), matching how a real blank/unreadable page would behave.
    private sealed class MapLattice : Dictionary<string, PageLattice>
    {
        public Task<PageLattice> Provide(RasterPage page, CancellationToken cancellationToken) =>
            Task.FromResult(TryGetValue(page.ImagePath, out var lattice) ? lattice : new PageLattice());
    }
}
