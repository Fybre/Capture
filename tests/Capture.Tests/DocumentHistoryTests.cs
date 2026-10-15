using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;
using Capture.Storage;

namespace Capture.Tests;

public sealed class DocumentHistoryTests
{
    [Fact]
    public async Task Events_are_kept_in_order_and_removed_with_the_document()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-history-" + Guid.NewGuid().ToString("N")));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();
        var document = new CaptureDocument { OriginalFileName = "doc.pdf", StoredPath = "/tmp/doc.pdf" };
        var other = new CaptureDocument { OriginalFileName = "other.pdf", StoredPath = "/tmp/other.pdf" };
        await store.SaveAsync(document, []);
        await store.SaveAsync(other, []);

        await store.AddEventsAsync([
            DocumentEvent.ByCapture(document.Id, DocumentEventKind.Import, "Imported doc.pdf (2 pages)", "Batch 3"),
            DocumentEvent.ByUser(document.Id, DocumentEventKind.Edit, "Changed Total", "Was: 10\nNow: 12")
        ]);
        await store.AddEventsAsync([DocumentEvent.ByUser(other.Id, DocumentEventKind.Review, "Marked ready")]);

        var history = await store.GetEventsAsync(document.Id);
        Assert.Equal(["Imported doc.pdf (2 pages)", "Changed Total"], history.Select(item => item.Message));
        Assert.Equal(DocumentEvent.CaptureActor, history[0].Actor);
        Assert.Equal(DocumentEvent.CurrentUser, history[1].Actor);
        Assert.Equal("Batch 3", history[0].Detail);
        Assert.Equal(DocumentEventKind.Edit, history[1].Kind);

        await store.PurgeAsync(document.Id);
        Assert.Empty(await store.GetEventsAsync(document.Id));
        Assert.Single(await store.GetEventsAsync(other.Id));
    }

    [Fact]
    public void The_plan_says_why_each_document_starts_and_what_it_is()
    {
        var invoice = new DocumentTypeDefinition
        {
            Name = "Invoice",
            StartRules = new RuleSet { Rules = [new SeparationStrategy { Name = "Invoice heading", Type = SeparationStrategyType.Regex, TextPattern = @"INVOICE (\d+)" }] }
        };
        var other = new DocumentTypeDefinition { Name = "Other" };
        var profile = new CaptureProfile { DocumentTypes = [invoice, other], DefaultDocumentTypeId = other.Id };

        var plan = new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [
            new AnalyzedPage("one", 1, "a cover letter", []),
            new AnalyzedPage("one", 2, "INVOICE 1042", []),
            new AnalyzedPage("one", 3, "continued", [])])]);

        var documents = plan.Batches.SelectMany(batch => batch.Documents).ToList();
        Assert.Equal(["Page 1: no rule matched, so it's a Other (the fallback type)."], documents[0].Decisions);
        Assert.Equal(["Page 2: a new Invoice starts — Invoice heading found \"1042\"."], documents[1].Decisions);
    }

    [Fact]
    public void The_plan_records_the_ais_reasons()
    {
        var invoice = new DocumentTypeDefinition { Name = "Invoice", AiDescription = "A bill." };
        var profile = new CaptureProfile { AiSeparation = true, DocumentTypes = [invoice], DefaultDocumentTypeId = invoice.Id };

        var plan = new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [
            new AnalyzedPage("one", 1, "", [], Ai: new AiPageVerdict(true, "invoice", 92, "New supplier header.")),
            new AnalyzedPage("one", 2, "", [], Ai: new AiPageVerdict(true, "invoice", 88, "A different invoice number.") { Provider = "the local AI model" })])]);

        var second = plan.Batches.SelectMany(batch => batch.Documents).ElementAt(1);
        Assert.Equal(
            ["Page 2: the AI says a new Invoice starts here (88% sure): A different invoice number.", "Type Invoice: chosen by the AI."],
            second.Decisions);
        Assert.Equal("the local AI model", second.AiProvider);
    }

    [Fact]
    public void A_separator_page_is_noted_on_the_document_after_it()
    {
        var invoice = new DocumentTypeDefinition
        {
            Name = "Invoice",
            TriggerPageDisposition = PageDisposition.Consume,
            StartRules = new RuleSet { Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "SEPARATOR" }] }
        };
        var profile = new CaptureProfile { DocumentTypes = [invoice], DefaultDocumentTypeId = invoice.Id };

        var plan = new CapturePlanner().Plan(profile, [new AnalyzedInput("one", [
            new AnalyzedPage("one", 1, "SEPARATOR", []),
            new AnalyzedPage("one", 2, "the invoice", [])])]);

        var document = plan.Batches.SelectMany(batch => batch.Documents).Single();
        Assert.Contains("Page 1 was a separator page and was removed.", document.Decisions!);
    }

    [Fact]
    public void Import_history_covers_source_decisions_page_fixes_fields_and_review()
    {
        var type = new DocumentTypeDefinition { Name = "Invoice", AutoReadyThreshold = 80 };
        var profile = new CaptureProfile { Name = "Accounts", DocumentTypes = [type] };
        var planned = new PlannedDocument(Guid.NewGuid(), type,
            [new SourcePage("one", 1), new SourcePage("one", 2), new SourcePage("one", 4)], [],
            Decisions: ["Page 1: a new Invoice starts — barcode rule found \"INV-1\"."]);
        var sources = new Dictionary<string, CaptureMaterializationSource>
        {
            ["one"] = new("one", "/scans/in/stack.pdf", DocumentSource.Import, [], new Dictionary<int, PageLattice>())
            {
                CorrectedPages = new HashSet<int> { 2 },
                RemovedBlankPages = [3]
            }
        };
        var document = new CaptureDocument { OriginalFileName = "stack.pdf", StoredPath = "/tmp/stack.pdf", Status = DocumentStatus.NeedsReview, ReviewNote = "AI was 55% sure page 4 starts a new Invoice" };
        IReadOnlyList<IndexValue> values =
        [
            new() { FieldName = "Invoice No", Value = "INV-1", Confidence = 99 },
            new() { FieldName = "Total", Value = "12.50", Confidence = 40 },
            new() { FieldName = "PO", Value = "", Mandatory = true },
            new() { FieldName = "Approve", Kind = FieldKind.Button }
        ];

        var history = CapturePlanMaterializer.ImportHistory(profile, planned, document, sources, 7, values, new HashSet<string>());

        Assert.Equal(
        [
            "Imported stack.pdf (3 pages)",
            "Split off and typed as Invoice",
            "Straightened or turned the right way up: page 2",
            "Removed blank pages from stack.pdf: page 3",
            "Read its fields: 2 of 3 filled",
            "Needs review"
        ], history.Select(item => item.Message));
        Assert.All(history, item => Assert.Equal(DocumentEvent.CaptureActor, item.Actor));
        Assert.Equal("Capture profile: Accounts\nBatch 7\nstack.pdf: pages 1–2, 4", history[0].Detail);
        Assert.Equal("Not sure of: Total\nRequired but empty: PO", history[4].Detail);
        Assert.Equal(document.ReviewNote, history[5].Detail);
        Assert.DoesNotContain(history, item => item.Detail?.Contains("12.50") == true);
    }

    [Fact]
    public void Import_history_names_the_ai_and_how_the_text_was_read()
    {
        var type = new DocumentTypeDefinition { Name = "Invoice" };
        var profile = new CaptureProfile { Name = "Accounts", DocumentTypes = [type] };
        var planned = new PlannedDocument(Guid.NewGuid(), type, [new SourcePage("one", 1), new SourcePage("one", 2)], [],
            Decisions: ["Page 1: the AI says a new Invoice starts here (95% sure)."], AiProvider: "gpt-4o-mini at api.openai.com");
        var sources = new Dictionary<string, CaptureMaterializationSource>
        {
            ["one"] = new("one", "/in/a.pdf", DocumentSource.Import, [], new Dictionary<int, PageLattice>
            {
                [1] = new() { PageNumber = 1, Source = LatticeSource.PdfText },
                [2] = new() { PageNumber = 2, Source = LatticeSource.Ocr }
            })
        };
        var document = new CaptureDocument { OriginalFileName = "a.pdf", StoredPath = "/tmp/a.pdf", Status = DocumentStatus.Ready };
        var report = new ExtractionReport();
        report.Notes.Add("AI (gpt-4o-mini at api.openai.com) filled 1 of 1 AI field(s): Supplier");
        report.Problems.Add("Script \"Tidy\" failed: boom");

        var history = CapturePlanMaterializer.ImportHistory(profile, planned, document, sources, 1,
            [new IndexValue { FieldName = "Supplier", Value = "Acme", Confidence = 90 }], new HashSet<string>(), report);

        var decision = history.Single(item => item.Kind == DocumentEventKind.Decision);
        Assert.Equal("Split off with AI and typed as Invoice", decision.Message);
        Assert.StartsWith("AI: gpt-4o-mini at api.openai.com\n", decision.Detail);
        Assert.Contains(history, item => item.Message == "Read the text: the PDF's own text on page 1; OCR on page 2");
        Assert.Contains("AI (gpt-4o-mini at api.openai.com) filled 1 of 1 AI field(s): Supplier",
            history.Single(item => item.Message.StartsWith("Read its fields")).Detail);
        Assert.Contains(history, item => item.Kind == DocumentEventKind.Error && item.Message == "Script \"Tidy\" failed: boom");
    }

    [Fact]
    public async Task Durations_are_kept_and_instant_steps_have_none()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "capture-history-" + Guid.NewGuid().ToString("N")));
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();
        var document = new CaptureDocument { OriginalFileName = "doc.pdf", StoredPath = "/tmp/doc.pdf" };
        await store.SaveAsync(document, []);

        await store.AddEventsAsync([
            DocumentEvent.ByUser(document.Id, DocumentEventKind.Export, "Exported to Archive", "Therefore document 12", TimeSpan.FromMilliseconds(6340)),
            DocumentEvent.ByUser(document.Id, DocumentEventKind.Review, "Marked ready")
        ]);

        var history = await store.GetEventsAsync(document.Id);
        Assert.Equal(TimeSpan.FromMilliseconds(6340), history[0].Duration);
        Assert.Null(history[1].Duration);
    }

    [Theory]
    [InlineData(320, "320 ms")]
    [InlineData(6340, "6.3 s")]
    [InlineData(125_000, "2 min 5 s")]
    public void Durations_read_naturally(int milliseconds, string expected) =>
        Assert.Equal(expected, DocumentEvent.FormatDuration(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Import_history_shows_where_the_time_went()
    {
        var type = new DocumentTypeDefinition { Name = "Invoice" };
        var profile = new CaptureProfile { Name = "Accounts", DocumentTypes = [type] };
        var planned = new PlannedDocument(Guid.NewGuid(), type, [new SourcePage("one", 1), new SourcePage("one", 2)], [],
            AiProvider: "the local AI model");
        var sources = new Dictionary<string, CaptureMaterializationSource>
        {
            ["one"] = new("one", "/in/a.pdf", DocumentSource.Scan, [], new Dictionary<int, PageLattice>
            {
                [1] = new() { PageNumber = 1, Source = LatticeSource.Ocr },
                [2] = new() { PageNumber = 2, Source = LatticeSource.Ocr }
            })
            {
                ReadTimes = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromSeconds(1), [2] = TimeSpan.FromSeconds(2) },
                StraightenTimes = new Dictionary<int, TimeSpan> { [1] = TimeSpan.FromMilliseconds(200), [2] = TimeSpan.FromMilliseconds(300) },
                AiSeparationTime = TimeSpan.FromSeconds(12.4),
                AiSeparationPages = 20
            }
        };
        var document = new CaptureDocument { OriginalFileName = "a.pdf", StoredPath = "/tmp/a.pdf", Status = DocumentStatus.Ready };

        var history = CapturePlanMaterializer.ImportHistory(profile, planned, document, sources, 1,
            [new IndexValue { FieldName = "Supplier", Value = "Acme", Confidence = 90 }], new HashSet<string>(),
            timings: new ImportTimings(Build: TimeSpan.FromMilliseconds(150), Fields: TimeSpan.FromSeconds(4)));

        Assert.Equal(TimeSpan.FromMilliseconds(150), history.Single(item => item.Kind == DocumentEventKind.Import).Duration);
        Assert.StartsWith("AI: the local AI model — 12.4 s for the 20 page(s) of this import", history.Single(item => item.Kind == DocumentEventKind.Decision).Detail);
        var text = history.Single(item => item.Message.StartsWith("Read the text"));
        Assert.Equal("Read the text: OCR on pages 1–2 (avg 1.5 s/page)", text.Message);
        Assert.Equal(TimeSpan.FromSeconds(3), text.Duration);
        Assert.Equal("Checking for crooked pages: 500 ms", text.Detail);
        Assert.Equal(TimeSpan.FromSeconds(4), history.Single(item => item.Message.StartsWith("Read its fields")).Duration);
        Assert.Null(history.Single(item => item.Kind == DocumentEventKind.Review).Duration);
    }

    [Theory]
    [InlineData(new[] { 3 }, "page 3")]
    [InlineData(new[] { 1, 2, 3, 4 }, "pages 1–4")]
    [InlineData(new[] { 7, 2, 5, 8, 9 }, "pages 2, 5, 7–9")]
    public void Page_lists_are_short(int[] pages, string expected) =>
        Assert.Equal(expected, CapturePlanMaterializer.PageList(pages));
}
