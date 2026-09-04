using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Pipeline;

namespace Capture.Core.Batches;

/// <summary>A page that triggered a batch boundary, with whatever value the trigger captured.</summary>
public sealed record BatchTriggerHit(int PageNumber, string CapturedValue, bool DiscardPage);

/// <summary>
/// Detects batch boundaries independently of document splitting (<c>PageSeparator</c>) — a separate
/// scan over the same pages, evaluating <c>BatchProfile.Strategies</c> the same way
/// <c>PageSeparator</c> evaluates <c>ImportProfile.Strategies</c> (via the shared
/// <see cref="SeparationStrategyEvaluator"/>), combined via <c>BatchProfile.MatchMode</c>.
/// </summary>
public static class BatchSeparator
{
    /// <summary>Whether <paramref name="profile"/> requires scanning pages at all —
    /// <see cref="BatchMode.NewBatchPerFile"/> and <see cref="BatchMode.Manual"/> are handled entirely
    /// by <c>BatchAllocator</c>, with no page-level condition to evaluate.</summary>
    public static bool NeedsPageScan(BatchProfile? profile) =>
        profile is not null && profile.Mode == BatchMode.UseStrategies && profile.Strategies.Count > 0;

    public static async Task<IReadOnlyList<BatchTriggerHit>> DetectAsync(
        IReadOnlyList<RasterPage> pages,
        BatchProfile profile,
        IBarcodeDecoder? barcodes,
        Func<RasterPage, CancellationToken, Task<PageLattice>>? latticeProvider,
        CancellationToken cancellationToken = default)
    {
        var hits = new List<BatchTriggerHit>();
        if (!NeedsPageScan(profile))
            return hits;

        // EveryNPages strategies each track their own "pages since this strategy's own last hit"
        // counter, independent of every other strategy in the list — same as PageSeparator's own use
        // of the shared evaluator.
        var everyNPagesCounters = new Dictionary<Guid, int>();

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageHits = new List<(SeparationStrategy Strategy, string Value)>();
            foreach (var strategy in profile.Strategies)
            {
                var value = await SeparationStrategyEvaluator.EvaluateAsync(strategy, page, barcodes, blanks: null, latticeProvider, everyNPagesCounters, cancellationToken)
                    .ConfigureAwait(false);
                if (value is not null)
                    pageHits.Add((strategy, value));
            }

            var isHit = SeparationStrategyEvaluator.Combine(profile.MatchMode, profile.MatchMinimum, profile.Strategies.Count, pageHits.Count);
            if (!isHit)
                continue;

            // No natural way to combine multiple captured strings into one BatchSeparatorValue —
            // first contributing strategy (in list order) with a non-empty value wins, documented
            // simplification rather than inventing a concatenation scheme nobody asked for.
            var capturedValue = pageHits.Select(hit => hit.Value).FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? string.Empty;
            var discardPage = pageHits.Any(hit => hit.Strategy.DiscardSeparatorPage);
            hits.Add(new BatchTriggerHit(page.PageNumber, capturedValue, discardPage));
        }

        return hits;
    }

    /// <summary>
    /// Breaks each split at every batch-trigger page that isn't already its first page, so a hit never
    /// ends up stranded mid-document. A <c>CaptureDocument</c> can only ever belong to one batch, so a
    /// batch-trigger page that falls mid-document (e.g. no indexing profile is splitting documents at
    /// all) has to force a document break there too — otherwise every batch after the first has nowhere
    /// to attach to. Mirrors <c>PageSeparator</c>'s flush-on-hit logic as a second pass over whatever
    /// splits the (document-level) pre-index step already produced.
    /// </summary>
    public static IReadOnlyList<ClassifiedSplit> ExpandSplitsAtBoundaries(
        IReadOnlyList<ClassifiedSplit> splits,
        IReadOnlyDictionary<int, BatchTriggerHit> batchHitsByPage)
    {
        if (batchHitsByPage.Count == 0)
            return splits;

        var expanded = new List<ClassifiedSplit>();
        foreach (var split in splits)
        {
            var current = new List<int>();
            foreach (var page in split.SourcePages)
            {
                if (batchHitsByPage.ContainsKey(page) && current.Count > 0)
                {
                    expanded.Add(new ClassifiedSplit
                    {
                        Profile = split.Profile,
                        SourcePages = current,
                        SeparatorValues = split.SeparatorValues
                    });
                    current = [];
                }

                current.Add(page);
            }

            if (current.Count > 0)
            {
                expanded.Add(new ClassifiedSplit
                {
                    Profile = split.Profile,
                    SourcePages = current,
                    SeparatorValues = split.SeparatorValues
                });
            }
        }

        return expanded;
    }
}
