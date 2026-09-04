using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Pipeline;

namespace Capture.Core.Batches;

/// <summary>A page that triggered a batch boundary, with whatever value the trigger captured and
/// (when <see cref="BatchProfile.Fields"/> is non-empty) the full set of batch-level index values
/// captured from that same raw page at detection time — see <see cref="BatchSeparator.DetectAsync"/>.</summary>
public sealed record BatchTriggerHit(int PageNumber, string CapturedValue, bool DiscardPage, IReadOnlyList<IndexValue> CapturedFields);

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
        IProfileApplicator? applicator = null,
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

            // Captured now, against the raw page, before this page might be discarded from every
            // resulting document — see BatchProfile.Fields' own doc comment for why this can't wait
            // until a document exists.
            var capturedFields = await CaptureFieldsAsync(page, profile, capturedValue, latticeProvider, applicator, cancellationToken)
                .ConfigureAwait(false);

            hits.Add(new BatchTriggerHit(page.PageNumber, capturedValue, discardPage, capturedFields));
        }

        return hits;
    }

    // Extracts BatchProfile.Fields against the single raw triggering page — before any CaptureDocument
    // exists, mirroring the throwaway-document trick PageLatticeProviderFactory already uses elsewhere
    // in this codebase for the same reason (need OCR/lattice-backed extraction ahead of materialization).
    // Field zones/page numbers are authored against this profile's own multi-page sample document in
    // the Designer, but at capture time there's only ever this one real page — build one synthetic
    // lattice/page entry per distinct page number any field references, all wrapping the same real
    // page's content, so ProfileApplicator's existing page-number-based candidate selection (Zonal's
    // page lookup, Barcode's PageScope) works unmodified rather than needing special-casing here.
    private static async Task<IReadOnlyList<IndexValue>> CaptureFieldsAsync(
        RasterPage page,
        BatchProfile profile,
        string? capturedValue,
        Func<RasterPage, CancellationToken, Task<PageLattice>>? latticeProvider,
        IProfileApplicator? applicator,
        CancellationToken cancellationToken)
    {
        if (profile.Fields.Count == 0 || applicator is null || latticeProvider is null)
            return [];

        var lattice = await latticeProvider(page, cancellationToken).ConfigureAwait(false);

        var pageNumbers = profile.Fields
            .Select(field => field.Zone?.PageNumber ?? field.PageNumber)
            .Distinct()
            .DefaultIfEmpty(1)
            .ToList();

        var syntheticLattices = pageNumbers
            .Select(number => new PageLattice
            {
                PageNumber = number,
                PixelWidth = lattice.PixelWidth,
                PixelHeight = lattice.PixelHeight,
                Dpi = lattice.Dpi,
                Source = lattice.Source,
                Words = lattice.Words
            })
            .ToList();

        var throwawayId = Guid.NewGuid();
        var throwawayDocument = new CaptureDocument { OriginalFileName = string.Empty, StoredPath = page.ImagePath };
        var syntheticPages = pageNumbers
            .Select(number => new DocumentPage
            {
                DocumentId = throwawayId,
                PageNumber = number,
                SourcePageNumber = number,
                ImagePath = page.ImagePath,
                Width = page.Width,
                Height = page.Height,
                Dpi = page.Dpi
            })
            .ToList();

        return await applicator.ApplyAsync(
                profile.Fields,
                profile.Scripts,
                profile.SharedScriptSource,
                syntheticLattices,
                profileName: profile.Name,
                pages: syntheticPages,
                batchSeparatorValue: capturedValue,
                document: throwawayDocument,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
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
