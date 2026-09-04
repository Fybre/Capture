using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;

namespace Capture.Core.Import;

public static class PageSeparator
{
    public static bool Enabled(ImportProfile? profile) =>
        profile is not null && profile.Strategies.Count > 0;

    public static async Task<IReadOnlyList<PageSplit>> SplitAsync(
        IReadOnlyList<RasterPage> pages,
        ImportProfile profile,
        IBarcodeDecoder? barcodes,
        IBlankPageDetector? blanks,
        Func<RasterPage, CancellationToken, Task<PageLattice>>? latticeProvider,
        CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0)
            return [];

        if (!Enabled(profile))
            return [All(pages)];

        // EveryNPages strategies each track their own "pages since this strategy's own last hit"
        // counter, independent of every other strategy in the list (see SeparationStrategy.PageCount).
        var everyNPagesCounters = new Dictionary<Guid, int>();

        var current = new PageSplit();
        var results = new List<PageSplit>();

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hits = new List<(SeparationStrategy Strategy, string? Value)>();
            foreach (var strategy in profile.Strategies)
            {
                var value = await SeparationStrategyEvaluator.EvaluateAsync(strategy, page, barcodes, blanks, latticeProvider, everyNPagesCounters, cancellationToken)
                    .ConfigureAwait(false);
                if (value is not null)
                    hits.Add((strategy, value));
            }

            var isBoundary = SeparationStrategyEvaluator.Combine(profile.MatchMode, profile.MatchMinimum, profile.Strategies.Count, hits.Count);

            if (isBoundary)
                Flush(results, ref current);

            var discard = isBoundary && hits.Any(hit => hit.Strategy.DiscardSeparatorPage);
            if (!discard)
            {
                current.SourcePages.Add(page.PageNumber);
                if (isBoundary)
                {
                    foreach (var (strategy, value) in hits)
                    {
                        if (!string.IsNullOrEmpty(value))
                            current.SeparatorValues[strategy.Id] = value;
                    }
                }
            }
        }

        Flush(results, ref current);
        return results.Count == 0 ? [All(pages)] : results;
    }

    private static PageSplit All(IReadOnlyList<RasterPage> pages)
    {
        var split = new PageSplit();
        foreach (var page in pages)
            split.SourcePages.Add(page.PageNumber);
        return split;
    }

    private static void Flush(List<PageSplit> results, ref PageSplit current)
    {
        if (current.SourcePages.Count > 0)
            results.Add(current);
        current = new PageSplit();
    }
}
