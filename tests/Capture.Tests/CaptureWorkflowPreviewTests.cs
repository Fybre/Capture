using Capture.Core.CaptureProfiles;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;

namespace Capture.Tests;

public sealed class CaptureWorkflowPreviewTests
{
    [Fact]
    public async Task Production_capture_rejects_a_disabled_profile()
    {
        var workflow = new CaptureWorkflowService(
            new AppPaths(Path.Combine(Path.GetTempPath(), "capture-disabled-test-" + Guid.NewGuid().ToString("N"))),
            pdfs: null!,
            images: null!,
            latticeBuilder: null!,
            barcodes: null!,
            blanks: null!,
            new CapturePlanner(),
            materializer: null!);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.ExecuteAsync(
            new CaptureProfile { Enabled = false }, ["unused"], DocumentSource.Import, "manual"));

        Assert.Equal("This capture profile is disabled.", exception.Message);
    }

    [Fact]
    public async Task Preview_plans_inputs_without_calling_the_materializer_and_cleans_scratch()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-preview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "invoice.png");
            await File.WriteAllTextAsync(source, "test input");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var type = new DocumentTypeDefinition
            {
                Name = "Invoice",
                RecognitionRules = new RuleSet
                {
                    Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "Invoice" }]
                }
            };
            var profile = new CaptureProfile
            {
                DefaultDocumentTypeId = type.Id,
                DocumentTypes = [type]
            };
            var workflow = new CaptureWorkflowService(
                paths,
                pdfs: null!,
                new OnePageImageImporter(),
                new InvoiceLatticeBuilder(),
                new NoBarcodeDecoder(),
                new NeverBlankDetector(),
                new CapturePlanner(),
                materializer: null!);

            var plan = await workflow.PreviewAsync(profile, [source], DocumentSource.Import);

            var batch = Assert.Single(plan.Batches);
            var document = Assert.Single(batch.Documents);
            Assert.Equal(type.Id, document.Type?.Id);
            Assert.Single(document.SourcePages);
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.WorkDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Detailed_preview_extracts_document_indexes_without_materializing_anything()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-index-preview-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "invoice.png");
            await File.WriteAllTextAsync(source, "test input");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var type = new DocumentTypeDefinition
            {
                Name = "Invoice",
                RecognitionRules = new RuleSet
                {
                    Rules = [new SeparationStrategy { Type = SeparationStrategyType.Regex, TextPattern = "Invoice" }]
                },
                Fields =
                [
                    new IndexField
                    {
                        Name = "Document kind",
                        Kind = FieldKind.Regex,
                        ValuePattern = "(Invoice)",
                        Mandatory = true
                    }
                ]
            };
            var profile = new CaptureProfile
            {
                DefaultDocumentTypeId = type.Id,
                DocumentTypes = [type],
                Batch = new BatchDefinition
                {
                    Fields =
                    [
                        new IndexField
                        {
                            Name = "Student number",
                            Kind = FieldKind.Barcode,
                            ValuePattern = @"^Student\|.*$"
                        }
                    ]
                }
            };
            var decoder = new FixedBarcodeDecoder("Student|X00007");
            var workflow = new CaptureWorkflowService(
                paths,
                pdfs: null!,
                new OnePageImageImporter(),
                new InvoiceLatticeBuilder(),
                decoder,
                new NeverBlankDetector(),
                new CapturePlanner(),
                materializer: null!,
                new ProfileApplicator(decoder));

            var preview = await workflow.PreviewWithIndexesAsync(profile, [source], DocumentSource.Import);

            var batch = Assert.Single(preview.Batches);
            Assert.Equal("Student|X00007", Assert.Single(batch.IndexValues).Value);
            var document = Assert.Single(batch.Documents);
            Assert.Equal("Invoice", document.DocumentType);
            Assert.Equal(DocumentStatus.Ready, document.Status);
            var index = Assert.Single(document.IndexValues);
            Assert.Equal("Document kind", index.FieldName);
            Assert.Equal("Invoice", index.Value);
            Assert.Empty(Directory.EnumerateFileSystemEntries(paths.WorkDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Blank_page_removal_discards_blank_pages_and_renumbers_survivors_before_planning()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-blank-removal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "scan.png");
            await File.WriteAllTextAsync(source, "test input");
            var paths = new AppPaths(Path.Combine(root, "app"));
            var type = new DocumentTypeDefinition { Name = "Scan" };
            var profile = new CaptureProfile
            {
                DefaultDocumentTypeId = type.Id,
                DocumentTypes = [type],
                RemoveBlankPagesOnIngestion = true,
                // Every 2nd surviving (non-blank) page starts a new document — proves counting happens
                // against the compacted sequence, not the original 4-page numbering (page 2 is blank).
                FileIsDocumentBoundary = true
            };
            type.StartRules = new RuleSet
            {
                MatchMode = SeparationMatchMode.Any,
                Rules = [new SeparationStrategy { Type = SeparationStrategyType.EveryNPages, PageCount = 2 }]
            };
            var workflow = new CaptureWorkflowService(
                paths,
                pdfs: null!,
                new FourPageImageImporter(blankPageNumber: 2),
                new InvoiceLatticeBuilder(),
                new NoBarcodeDecoder(),
                new BlankOnSuffixDetector("-blank"),
                new CapturePlanner(),
                materializer: null!);

            var plan = await workflow.PreviewAsync(profile, [source], DocumentSource.Import);

            var batch = Assert.Single(plan.Batches);
            // 3 surviving pages (originally 1, 3, 4 — page 2 was blank): the first document is whatever
            // fallback-classifies survivor #1 before any start rule has a chance to match, then the
            // EveryNPages(2) rule starts a new document exactly at survivor #2 (originally page 3).
            Assert.Equal(2, batch.Documents.Count);
            Assert.Single(batch.Documents[0].SourcePages);
            Assert.Equal(2, batch.Documents[1].SourcePages.Count);
            Assert.Equal([1], batch.Documents[0].SourcePages.Select(page => page.PageNumber));
            Assert.Equal([2, 3], batch.Documents[1].SourcePages.Select(page => page.PageNumber));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class FourPageImageImporter(int blankPageNumber) : IImagePageImporter
    {
        public Task<IReadOnlyList<RasterPage>> ImportAsync(
            string imagePath,
            string outputDirectory,
            CancellationToken cancellationToken = default,
            int? dpiOverride = null) =>
            Task.FromResult<IReadOnlyList<RasterPage>>(Enumerable.Range(1, 4)
                .Select(number => new RasterPage(
                    number,
                    number == blankPageNumber ? imagePath + "-blank" : imagePath,
                    100, 100, 200))
                .ToList());
    }

    [Fact]
    public async Task Ai_separation_splits_and_types_a_stack_and_flags_uncertain_documents()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-ai-separation-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "stack.png");
            await File.WriteAllTextAsync(source, "test input");
            var invoice = new DocumentTypeDefinition { Name = "Invoice", AiDescription = "A bill." };
            var statement = new DocumentTypeDefinition { Name = "Statement", AiDescription = "An account statement." };
            var profile = new CaptureProfile
            {
                AiSeparation = true,
                DefaultDocumentTypeId = invoice.Id,
                DocumentTypes = [invoice, statement]
            };
            var separator = new ScriptedSeparator(
                new AiPageVerdict(true, "invoice", 95, "Invoice header."),
                new AiPageVerdict(false, null, 50, "Unclear."),
                new AiPageVerdict(true, "statement", 92, "Statement header."));
            var workflow = new CaptureWorkflowService(
                new AppPaths(Path.Combine(root, "app")),
                pdfs: null!,
                new ThreePageImageImporter(),
                new InvoiceLatticeBuilder(),
                new NoBarcodeDecoder(),
                new NeverBlankDetector(),
                new CapturePlanner(),
                materializer: null!,
                aiSeparator: separator);

            var plan = await workflow.PreviewAsync(profile, [source], DocumentSource.Import);

            var documents = plan.Batches.SelectMany(batch => batch.Documents).ToList();
            Assert.Equal(["Invoice", "Statement"], documents.Select(document => document.Type?.Name));
            Assert.Equal([2, 1], documents.Select(document => document.SourcePages.Count));
            Assert.Contains("50%", Assert.Single(documents[0].ReviewReasons!));
            Assert.Null(documents[1].ReviewReasons);
            Assert.Equal(3, separator.Pages.Count);
            Assert.True(separator.Pages[0].ForceNew);
            Assert.Equal("Invoice", separator.Pages[0].Text);
            Assert.Equal(["invoice", "statement"], separator.Types.Select(type => type.Id));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ScriptedSeparator(params AiPageVerdict[] verdicts) : IAiPageSeparator
    {
        public IReadOnlyList<AiSeparationPage> Pages { get; private set; } = [];
        public IReadOnlyList<AiSeparationType> Types { get; private set; } = [];
        public string? Guidance { get; private set; }
        public bool IsConfigured => true;

        public Task<IReadOnlyList<AiPageVerdict>> AnalyzeAsync(
            IReadOnlyList<AiSeparationPage> pages, IReadOnlyList<AiSeparationType>? types, string? guidance = null, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
        {
            Pages = pages;
            Guidance = guidance;
            Types = types;
            return Task.FromResult<IReadOnlyList<AiPageVerdict>>(verdicts);
        }
    }

    private sealed class ThreePageImageImporter : IImagePageImporter
    {
        public Task<IReadOnlyList<RasterPage>> ImportAsync(
            string imagePath,
            string outputDirectory,
            CancellationToken cancellationToken = default,
            int? dpiOverride = null) =>
            Task.FromResult<IReadOnlyList<RasterPage>>(
                [new RasterPage(1, imagePath, 100, 100, 200), new RasterPage(2, imagePath, 100, 100, 200), new RasterPage(3, imagePath, 100, 100, 200)]);
    }

    private sealed class BlankOnSuffixDetector(string suffix) : IBlankPageDetector
    {
        public bool IsBlank(string imagePath, float maxInkPercent) => imagePath.EndsWith(suffix, StringComparison.Ordinal);
    }

    private sealed class OnePageImageImporter : IImagePageImporter
    {
        public Task<IReadOnlyList<RasterPage>> ImportAsync(
            string imagePath,
            string outputDirectory,
            CancellationToken cancellationToken = default,
            int? dpiOverride = null) =>
            Task.FromResult<IReadOnlyList<RasterPage>>([new RasterPage(1, imagePath, 100, 100, 200)]);
    }

    private sealed class InvoiceLatticeBuilder : ILatticeBuilder
    {
        public Task<PageLattice> BuildPageAsync(
            CaptureDocument document,
            DocumentPage page,
            CancellationToken cancellationToken = default,
            Capture.Core.CaptureProfiles.OcrOptions? ocr = null) =>
            Task.FromResult(new PageLattice
            {
                PageNumber = page.PageNumber,
                PixelWidth = page.Width,
                PixelHeight = page.Height,
                Dpi = page.Dpi,
                Words = [new LatticeWord { Text = "Invoice", Confidence = 100 }]
            });

        public Task BuildDocumentAsync(
            CaptureDocument document,
            IReadOnlyList<DocumentPage> pages,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoBarcodeDecoder : IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) => null;
    }

    private sealed class FixedBarcodeDecoder(string value) : IBarcodeDecoder
    {
        public BarcodeReadResult? Decode(string imagePath, ZoneRect? zone) =>
            new(value, "QR_CODE", 95);
    }

    private sealed class NeverBlankDetector : IBlankPageDetector
    {
        public bool IsBlank(string imagePath, float maxInkPercent) => false;
    }
}
