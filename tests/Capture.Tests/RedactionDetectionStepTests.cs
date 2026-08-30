using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Store;
using Capture.Storage;

namespace Capture.Tests;

public class RedactionDetectionStepTests
{
    [Fact]
    public async Task Sensitive_field_produces_a_candidate_without_the_detector_being_configured()
    {
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector { IsConfigured = false };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        var context = env.CreateContext(document, redactionEnabled: true, indexValues:
        [
            new IndexValue
            {
                FieldId = Guid.NewGuid(),
                FieldName = "SSN",
                Sensitive = true,
                Value = "123-45-6789",
                Bounds = new ZoneRect { PageNumber = 1, X = 0.1f, Y = 0.1f, Width = 0.2f, Height = 0.05f }
            }
        ]);

        await step.RunAsync(context);

        var saved = await env.Candidates.GetAsync(document.Id);
        var candidate = Assert.Single(saved);
        Assert.Equal(RedactionSource.SensitiveField, candidate.Source);
        Assert.Equal("SSN", candidate.Label);

        // A lone Sensitive-field candidate (score 100) clears the default 100 bypass threshold, so it
        // auto-applies rather than sitting in PendingReview.
        var reloaded = await env.Store.GetAllAsync();
        Assert.Equal(RedactionStatus.Applied, Assert.Single(reloaded).RedactionStatus);
    }

    [Fact]
    public async Task RunAsync_is_a_noop_when_the_document_is_not_Ready_or_redaction_is_disabled()
    {
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector { IsConfigured = true, Matches = [new PiiMatch("PERSON", 8, 16, 0.95f)] };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        document.Status = DocumentStatus.NeedsReview;
        await env.SaveLatticeAsync(document.Id, 1, "Contact Jane Doe for details.");

        // Neither gate passes: document isn't Ready, and redaction isn't enabled.
        var context = env.CreateContext(document, redactionEnabled: false, indexValues: []);
        await step.RunAsync(context);

        Assert.Empty(await env.Candidates.GetAsync(document.Id));
        Assert.Equal(RedactionStatus.None, document.RedactionStatus);
    }

    [Fact]
    public async Task DetectAsync_runs_directly_regardless_of_document_status_or_Enabled_for_a_manual_trigger()
    {
        // Mirrors MainViewModel.RedactSelectedAsync — a manual "redact this document now" action calls
        // DetectAsync directly, bypassing RunAsync's Ready/Enabled gate entirely.
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector { IsConfigured = true, Matches = [new PiiMatch("PERSON", 8, 16, 0.95f)] };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        document.Status = DocumentStatus.NeedsReview;
        await env.SaveLatticeAsync(document.Id, 1, "Contact Jane Doe for details.");

        // A profile that never touched Redaction settings at all — Enabled defaults to false — using
        // the same "unconfigured profile" default a manual trigger falls back to.
        var settings = new RedactionSettings();
        await step.DetectAsync(document, env.PagesFor(document), [], settings);

        var saved = await env.Candidates.GetAsync(document.Id);
        Assert.Single(saved);
    }

    [Fact]
    public async Task Failing_detector_sets_RedactionStatus_Failed_instead_of_throwing()
    {
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector { IsConfigured = true, Throw = true };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        await env.SaveLatticeAsync(document.Id, 1, "Contact Jane Doe for details.");
        var context = env.CreateContext(document, redactionEnabled: true, indexValues: []);

        await step.RunAsync(context);

        var reloaded = Assert.Single(await env.Store.GetAllAsync());
        Assert.Equal(RedactionStatus.Failed, reloaded.RedactionStatus);
        Assert.NotNull(reloaded.RedactionError);
    }

    [Fact]
    public async Task All_candidates_clearing_the_bypass_threshold_applies_immediately_without_review()
    {
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector
        {
            IsConfigured = true,
            Matches = [new PiiMatch("PERSON", 8, 16, 0.95f)] // "Jane Doe" inside "Contact Jane Doe for details."
        };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        await env.SaveLatticeAsync(document.Id, 1, "Contact Jane Doe for details.");
        var context = env.CreateContext(document, redactionEnabled: true, indexValues: [], bypassThreshold: 90);

        await step.RunAsync(context);

        var reloaded = Assert.Single(await env.Store.GetAllAsync());
        Assert.Equal(RedactionStatus.Applied, reloaded.RedactionStatus);
        Assert.NotNull(reloaded.RedactedPath);
    }

    [Fact]
    public async Task Overlapping_matches_keep_only_the_highest_scoring_candidate()
    {
        // Presidio's regex-based and NER-based recognizers commonly both fire on the same text —
        // here a low-confidence, wrongly-labeled match for just "Jane" (a sub-span, lower score) should
        // be dropped in favor of the higher-confidence PERSON match covering all of "Jane Doe".
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector
        {
            IsConfigured = true,
            Matches =
            [
                new PiiMatch("PERSON", 8, 16, 0.95f),       // "Jane Doe"
                new PiiMatch("ORGANIZATION", 8, 12, 0.6f)   // "Jane" — fully inside the match above
            ]
        };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        await env.SaveLatticeAsync(document.Id, 1, "Contact Jane Doe for details.");
        var context = env.CreateContext(document, redactionEnabled: true, indexValues: [], bypassThreshold: 90);

        await step.RunAsync(context);

        var saved = await env.Candidates.GetAsync(document.Id);
        var candidate = Assert.Single(saved);
        Assert.Equal("PERSON", candidate.Label);
    }

    [Fact]
    public async Task Non_overlapping_matches_are_all_kept()
    {
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector
        {
            IsConfigured = true,
            Matches =
            [
                new PiiMatch("PERSON", 8, 16, 0.95f),  // "Jane Doe"
                new PiiMatch("PERSON", 21, 28, 0.9f)   // "details" — separate, non-overlapping span
            ]
        };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        await env.SaveLatticeAsync(document.Id, 1, "Contact Jane Doe for details.");
        var context = env.CreateContext(document, redactionEnabled: true, indexValues: [], bypassThreshold: 90);

        await step.RunAsync(context);

        var saved = await env.Candidates.GetAsync(document.Id);
        Assert.Equal(2, saved.Count);
    }

    [Fact]
    public async Task Mixed_confidence_candidates_land_in_PendingReview_with_all_of_them_saved()
    {
        var env = await CreateEnvironmentAsync();
        var detector = new FakePiiDetector
        {
            IsConfigured = true,
            Matches =
            [
                new PiiMatch("PERSON", 8, 16, 0.95f), // high confidence — clears bypass on its own
                new PiiMatch("PERSON", 21, 28, 0.55f)  // "details" — low confidence, forces review
            ]
        };
        var step = env.CreateStep(detector);

        var document = await env.CreateDocumentAsync(pageCount: 1);
        await env.SaveLatticeAsync(document.Id, 1, "Contact Jane Doe for details.");
        var context = env.CreateContext(document, redactionEnabled: true, indexValues: [], bypassThreshold: 90);

        await step.RunAsync(context);

        var reloaded = Assert.Single(await env.Store.GetAllAsync());
        Assert.Equal(RedactionStatus.PendingReview, reloaded.RedactionStatus);

        var saved = await env.Candidates.GetAsync(document.Id);
        Assert.Equal(2, saved.Count);
    }

    private static async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "capture-redaction-step-" + Guid.NewGuid().ToString("N"));
        var paths = new AppPaths(root);
        var store = new SqliteDocumentStore(paths);
        await store.InitializeAsync();
        var lattices = new JsonLatticeStore(paths);
        var candidates = new JsonRedactionCandidateStore(paths);
        var writer = new FakeRedactedDocumentWriter();
        var applier = new RedactionApplier(writer, store, paths);
        return new TestEnvironment(paths, store, lattices, candidates, applier);
    }

    private sealed record TestEnvironment(
        IAppPaths Paths,
        IDocumentStore Store,
        ILatticeStore Lattices,
        IRedactionCandidateStore Candidates,
        RedactionApplier Applier)
    {
        public RedactionDetectionStep CreateStep(IPiiDetector detector) =>
            new(Lattices, detector, Candidates, Store, Applier);

        public async Task<CaptureDocument> CreateDocumentAsync(int pageCount)
        {
            var id = Guid.NewGuid();
            var pages = Enumerable.Range(1, pageCount).Select(number => new DocumentPage
            {
                DocumentId = id,
                PageNumber = number,
                SourcePageNumber = number,
                ImagePath = Path.Combine(Paths.DocumentPagesDirectory(id), $"{number:D4}.png")
            }).ToList();

            var document = new CaptureDocument
            {
                Id = id,
                OriginalFileName = "sample.pdf",
                StoredPath = Path.Combine(Paths.DocumentDirectory(id), "original.pdf"),
                Status = DocumentStatus.Ready,
                PageCount = pageCount
            };

            await Store.SaveAsync(document, pages);
            return document;
        }

        public Task SaveLatticeAsync(Guid documentId, int pageNumber, string text)
        {
            var words = new List<LatticeWord>();
            var x = 0f;
            foreach (var word in text.Split(' '))
            {
                words.Add(new LatticeWord { Text = word, Confidence = 100, X = x, Y = 0.1f, Width = 0.05f, Height = 0.03f });
                x += 0.06f;
            }

            return Lattices.SaveAsync(documentId, new PageLattice
            {
                PageNumber = pageNumber,
                PixelWidth = 1000,
                PixelHeight = 1400,
                Dpi = 150,
                Source = LatticeSource.Ocr,
                Words = words
            });
        }

        public IReadOnlyList<DocumentPage> PagesFor(CaptureDocument document) =>
            Enumerable.Range(1, document.PageCount).Select(number => new DocumentPage
            {
                DocumentId = document.Id,
                PageNumber = number,
                SourcePageNumber = number,
                ImagePath = Path.Combine(Paths.DocumentPagesDirectory(document.Id), $"{number:D4}.png")
            }).ToList();

        public PostIndexContext CreateContext(
            CaptureDocument document,
            bool redactionEnabled,
            IReadOnlyList<IndexValue> indexValues,
            int bypassThreshold = 100)
        {
            var profile = new IndexingProfile
            {
                Redaction = new RedactionSettings
                {
                    Enabled = redactionEnabled,
                    BypassReviewScoreThresholdPercent = bypassThreshold
                }
            };

            return new PostIndexContext
            {
                Document = document,
                Pages = PagesFor(document),
                IndexValues = indexValues,
                Profile = profile
            };
        }
    }

    private sealed class FakePiiDetector : IPiiDetector
    {
        public bool IsConfigured { get; set; }
        public bool Throw { get; set; }
        public IReadOnlyList<PiiMatch> Matches { get; set; } = [];

        public Task<IReadOnlyList<PiiMatch>> AnalyzeAsync(
            string text, IReadOnlyList<string> entities, string language, CancellationToken cancellationToken = default)
        {
            if (Throw)
                throw new InvalidOperationException("Simulated Presidio failure.");
            return Task.FromResult(Matches);
        }
    }

    private sealed class FakeRedactedDocumentWriter : IRedactedDocumentWriter
    {
        public Task<string> WriteAsync(
            CaptureDocument document,
            IReadOnlyList<DocumentPage> pages,
            IReadOnlyList<RedactionCandidate> confirmedCandidates,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(outputPath);
        }
    }
}
