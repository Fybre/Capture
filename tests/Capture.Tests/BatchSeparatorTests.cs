using Capture.Core.Batches;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;

namespace Capture.Tests;

public class BatchSeparatorTests
{
    [Fact]
    public async Task Barcode_trigger_reports_a_hit_for_every_page_that_decodes()
    {
        var profile = new BatchProfile { Trigger = BatchTrigger.Barcode };
        var pages = Pages("a.png", "b.png", "c.png");
        var decoder = new MapDecoder { ["a.png"] = ("BATCH-1", "CODE_128"), ["c.png"] = ("BATCH-2", "CODE_128") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, pageTextProvider: null);

        Assert.Equal(2, hits.Count);
        Assert.Equal(1, hits[0].PageNumber);
        Assert.Equal("BATCH-1", hits[0].CapturedValue);
        Assert.Equal(3, hits[1].PageNumber);
        Assert.Equal("BATCH-2", hits[1].CapturedValue);
    }

    [Fact]
    public async Task Barcode_trigger_filters_by_format_when_configured()
    {
        var profile = new BatchProfile { Trigger = BatchTrigger.Barcode, BarcodeFormat = "QR_CODE" };
        var pages = Pages("a.png", "b.png");
        var decoder = new MapDecoder { ["a.png"] = ("BATCH-1", "CODE_128"), ["b.png"] = ("BATCH-2", "QR_CODE") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, pageTextProvider: null);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.PageNumber);
        Assert.Equal("BATCH-2", hit.CapturedValue);
    }

    [Fact]
    public async Task Barcode_trigger_filters_by_value_pattern_when_configured()
    {
        var profile = new BatchProfile { Trigger = BatchTrigger.Barcode, BarcodeValuePattern = "^BATCH-" };
        var pages = Pages("a.png", "b.png");
        var decoder = new MapDecoder { ["a.png"] = ("NOPE-1", "CODE_128"), ["b.png"] = ("BATCH-2", "CODE_128") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, pageTextProvider: null);

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.PageNumber);
    }

    [Fact]
    public async Task Barcode_trigger_carries_the_discard_flag_through_to_each_hit()
    {
        var profile = new BatchProfile { Trigger = BatchTrigger.Barcode, DiscardSeparatorPage = true };
        var pages = Pages("a.png");
        var decoder = new MapDecoder { ["a.png"] = ("BATCH-1", "CODE_128") };

        var hits = await BatchSeparator.DetectAsync(pages, profile, decoder, pageTextProvider: null);

        Assert.True(Assert.Single(hits).DiscardPage);
    }

    [Fact]
    public async Task RegexMatch_trigger_reports_a_hit_only_for_pages_whose_text_matches()
    {
        var profile = new BatchProfile { Trigger = BatchTrigger.RegexMatch, TextPattern = @"Invoice #(\d+)" };
        var pages = Pages("a.png", "b.png");
        var textByPage = new Dictionary<string, string>
        {
            ["a.png"] = "just some other text",
            ["b.png"] = "Header\nInvoice #4471\nFooter"
        };

        var hits = await BatchSeparator.DetectAsync(pages, profile, barcodes: null, PageTextProvider(textByPage));

        var hit = Assert.Single(hits);
        Assert.Equal(2, hit.PageNumber);
        Assert.Equal("4471", hit.CapturedValue);
    }

    [Fact]
    public void NewBatchPerFile_and_manual_triggers_never_scan_pages()
    {
        Assert.False(BatchSeparator.NeedsPageScan(new BatchProfile { Trigger = BatchTrigger.NewBatchPerFile }));
        Assert.False(BatchSeparator.NeedsPageScan(new BatchProfile { Trigger = BatchTrigger.EveryNPages }));
        Assert.False(BatchSeparator.NeedsPageScan(new BatchProfile { Trigger = BatchTrigger.Manual }));
        Assert.False(BatchSeparator.NeedsPageScan(null));
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

    private static IReadOnlyList<RasterPage> Pages(params string[] names) =>
        names.Select((name, index) => new RasterPage(index + 1, name, 10, 10, 96)).ToList();

    private static Func<RasterPage, CancellationToken, Task<string>> PageTextProvider(Dictionary<string, string> textByPage) =>
        (page, _) => Task.FromResult(textByPage.GetValueOrDefault(page.ImagePath, string.Empty));

    private sealed class MapDecoder : Dictionary<string, (string Text, string Format)>, IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            TryGetValue(imagePath, out var result) ? new BarcodeReadResult(result.Text, result.Format, 99) : null;
    }
}
