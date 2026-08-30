using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Store;

namespace Capture.Core.Redaction;

/// <summary>Post-index step: once a document reaches Ready and its profile has redaction enabled,
/// finds PII via the bundled Presidio sidecar and any field marked Sensitive, and either queues the
/// results for review or — if every candidate clears the profile's bypass threshold — redacts
/// immediately via the shared <see cref="RedactionApplier"/>.</summary>
public sealed class RedactionDetectionStep : IPostIndexStep
{
    private readonly ILatticeStore _lattices;
    private readonly IPiiDetector _piiDetector;
    private readonly IRedactionCandidateStore _candidates;
    private readonly IDocumentStore _store;
    private readonly RedactionApplier _applier;

    public RedactionDetectionStep(
        ILatticeStore lattices,
        IPiiDetector piiDetector,
        IRedactionCandidateStore candidates,
        IDocumentStore store,
        RedactionApplier applier)
    {
        _lattices = lattices;
        _piiDetector = piiDetector;
        _candidates = candidates;
        _store = store;
        _applier = applier;
    }

    public Task RunAsync(PostIndexContext context, CancellationToken cancellationToken = default)
    {
        if (context.Document.Status != DocumentStatus.Ready || !context.Profile.Redaction.Enabled)
            return Task.CompletedTask;

        return DetectAsync(context.Document, context.Pages, context.IndexValues, context.Profile.Redaction, cancellationToken);
    }

    /// <summary>The actual detection/apply logic, with no gating on <see cref="DocumentStatus"/> or
    /// <see cref="RedactionSettings.Enabled"/> — used directly by a manual "redact this document now"
    /// action (see MainViewModel.RedactSelectedAsync), which applies regardless of profile
    /// configuration or document status, as well as by <see cref="RunAsync"/> once its own gate passes.</summary>
    public async Task DetectAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> pages,
        IReadOnlyList<IndexValue> indexValues,
        RedactionSettings settings,
        CancellationToken cancellationToken = default)
    {
        var found = new List<RedactionCandidate>();

        try
        {
            if (_piiDetector.IsConfigured)
            {
                foreach (var page in pages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var lattice = await _lattices.GetAsync(document.Id, page.PageNumber, cancellationToken)
                        .ConfigureAwait(false);
                    if (lattice is null || lattice.Words.Count == 0)
                        continue;

                    var built = LatticeText.Build(lattice.Words);
                    var matches = await _piiDetector
                        .AnalyzeAsync(built.Text, settings.Entities, settings.Language, cancellationToken)
                        .ConfigureAwait(false);

                    foreach (var match in matches)
                    {
                        if (match.Score * 100 < settings.ScoreThresholdPercent)
                            continue;

                        var words = LatticeText.WordsCovering(built, match.Start, match.End);
                        if (words.Count == 0)
                            continue;

                        var (x, y, width, height) = UnionBounds(words);
                        found.Add(new RedactionCandidate
                        {
                            Source = RedactionSource.Presidio,
                            Label = match.EntityType,
                            PreviewText = built.Text[Math.Max(0, match.Start)..Math.Min(built.Text.Length, match.End)],
                            PageNumber = page.PageNumber,
                            Score = match.Score,
                            X = x,
                            Y = y,
                            Width = width,
                            Height = height
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            document.RedactionStatus = RedactionStatus.Failed;
            document.RedactionError = ex.Message;
            await _store.UpdateAsync(document, cancellationToken).ConfigureAwait(false);
            return;
        }

        // Presidio runs several independent recognizers over the same text — a regex-based one (e.g.
        // CREDIT_CARD) and the statistical NER model commonly both fire on the same or a sub-span of
        // text, producing near-duplicate candidates with different (often wrong — the NER model is the
        // less reliable of the two) labels for what is really one region, e.g. "4532" tagged DATE_TIME
        // inside a span already tagged CREDIT_CARD as "4532 0151 1283 0366". Keep only the
        // highest-scoring candidate per overlapping region so the review list doesn't show redundant,
        // frequently mislabeled entries for the same text.
        found = DeduplicateOverlapping(found);

        foreach (var value in indexValues)
        {
            if (!value.Sensitive || value.Bounds is null)
                continue;

            found.Add(new RedactionCandidate
            {
                Source = RedactionSource.SensitiveField,
                Label = value.FieldName,
                PreviewText = value.Value,
                PageNumber = value.Bounds.PageNumber,
                X = value.Bounds.X,
                Y = value.Bounds.Y,
                Width = value.Bounds.Width,
                Height = value.Bounds.Height,
                Score = 1f
            });
        }

        if (found.Count == 0)
            return;

        var allBypass = found.All(candidate => candidate.Score * 100 >= settings.BypassReviewScoreThresholdPercent);
        if (allBypass)
        {
            foreach (var candidate in found)
                candidate.Decision = RedactionDecision.Confirmed;

            await _candidates.SaveAsync(document.Id, found, cancellationToken).ConfigureAwait(false);
            await _applier.ApplyAsync(document, pages, found, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _candidates.SaveAsync(document.Id, found, cancellationToken).ConfigureAwait(false);
        document.RedactionStatus = RedactionStatus.PendingReview;
        await _store.UpdateAsync(document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Greedily keeps the highest-scoring candidate on each page and drops any other candidate
    /// whose box overlaps it by more than half of its own area — a lower-scoring candidate fully or
    /// mostly contained inside (or coincident with) an already-kept one is treated as a duplicate
    /// detection of the same region, not a distinct finding.</summary>
    private static List<RedactionCandidate> DeduplicateOverlapping(List<RedactionCandidate> candidates)
    {
        var kept = new List<RedactionCandidate>();
        foreach (var candidate in candidates.OrderByDescending(item => item.Score))
        {
            var overlapsKept = kept.Any(existing =>
                existing.PageNumber == candidate.PageNumber && Overlaps(existing, candidate));
            if (!overlapsKept)
                kept.Add(candidate);
        }

        return kept;
    }

    private static bool Overlaps(RedactionCandidate a, RedactionCandidate b)
    {
        var left = Math.Max(a.X, b.X);
        var top = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        if (right <= left || bottom <= top)
            return false;

        var intersection = (right - left) * (bottom - top);
        var smallerArea = Math.Min(a.Width * a.Height, b.Width * b.Height);
        return smallerArea > 0 && intersection / smallerArea > 0.5f;
    }

    private static (float X, float Y, float Width, float Height) UnionBounds(IReadOnlyList<LatticeWord> words)
    {
        var left = words.Min(word => word.X);
        var top = words.Min(word => word.Y);
        var right = words.Max(word => word.X + word.Width);
        var bottom = words.Max(word => word.Y + word.Height);
        return (left, top, right - left, bottom - top);
    }
}
