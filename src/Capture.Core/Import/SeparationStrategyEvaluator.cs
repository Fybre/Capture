using System.Text.RegularExpressions;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Import;

/// <summary>
/// Per-strategy-per-page evaluation shared by <see cref="PageSeparator"/> (document splitting, driven
/// by <c>ImportProfile</c>) and <c>BatchSeparator</c> (batch-boundary detection, driven by
/// <c>BatchProfile</c>) — both need the identical "does this <see cref="SeparationStrategy"/> hit on
/// this page" logic and the identical All/Any/AtLeast combining logic, just against two different
/// profile types and two different owning-entity boundaries (document vs. batch). Extracted here so
/// that logic exists in exactly one place rather than being copied a second time.
/// </summary>
internal static class SeparationStrategyEvaluator
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static bool Combine(SeparationMatchMode matchMode, int matchMinimum, int strategyCount, int hitCount) => matchMode switch
    {
        SeparationMatchMode.All => strategyCount > 0 && hitCount == strategyCount,
        SeparationMatchMode.Any => hitCount > 0,
        SeparationMatchMode.AtLeast => hitCount >= Math.Max(1, matchMinimum),
        _ => false
    };

    // Returns null for "no hit", or the captured value (possibly empty string, e.g. BlankPage/
    // EveryNPages have nothing to capture) for "hit".
    public static async Task<string?> EvaluateAsync(
        SeparationStrategy strategy,
        RasterPage page,
        IBarcodeDecoder? barcodes,
        IBlankPageDetector? blanks,
        Func<RasterPage, CancellationToken, Task<PageLattice>>? latticeProvider,
        Dictionary<Guid, int> everyNPagesCounters,
        CancellationToken cancellationToken)
    {
        switch (strategy.Type)
        {
            case SeparationStrategyType.Barcode:
                return EvaluateBarcode(strategy, page, barcodes);

            case SeparationStrategyType.BlankPage:
                return EvaluateBlankPage(strategy, page, blanks);

            case SeparationStrategyType.EveryNPages:
                return EvaluateEveryNPages(strategy, everyNPagesCounters);

            case SeparationStrategyType.Regex:
                return await EvaluateRegexAsync(strategy, page, latticeProvider, cancellationToken).ConfigureAwait(false);

            case SeparationStrategyType.OcrZone:
                return await EvaluateOcrZoneAsync(strategy, page, latticeProvider, cancellationToken).ConfigureAwait(false);

            case SeparationStrategyType.Similarity:
                // TODO(Phase C): compute the page's embedding, cosine-compare against
                // strategy.ReferenceEmbedding, hit when similarity >= strategy.SimilarityThreshold.
                // Never hits until the embedding backend ships.
                return null;

            default:
                return null;
        }
    }

    private static string? EvaluateBarcode(SeparationStrategy strategy, RasterPage page, IBarcodeDecoder? barcodes)
    {
        var decoded = barcodes?.Decode(page.ImagePath, strategy.Zone);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
            return null;

        if (!string.IsNullOrWhiteSpace(strategy.BarcodeFormat)
            && !string.Equals(strategy.BarcodeFormat, decoded.Format, StringComparison.OrdinalIgnoreCase))
            return null;

        return BarcodePatterns.Matches(strategy.BarcodeValuePattern, decoded.Text) ? decoded.Text : null;
    }

    private static string? EvaluateBlankPage(SeparationStrategy strategy, RasterPage page, IBlankPageDetector? blanks) =>
        blanks is not null && blanks.IsBlank(page.ImagePath, strategy.BlankInkPercent) ? string.Empty : null;

    private static string? EvaluateEveryNPages(SeparationStrategy strategy, Dictionary<Guid, int> counters)
    {
        var threshold = Math.Max(1, strategy.PageCount);
        var count = counters.GetValueOrDefault(strategy.Id);
        var hit = count >= threshold;
        counters[strategy.Id] = hit ? 1 : count + 1;
        return hit ? string.Empty : null;
    }

    private static async Task<string?> EvaluateRegexAsync(
        SeparationStrategy strategy,
        RasterPage page,
        Func<RasterPage, CancellationToken, Task<PageLattice>>? latticeProvider,
        CancellationToken cancellationToken)
    {
        if (latticeProvider is null || string.IsNullOrWhiteSpace(strategy.TextPattern))
            return null;

        var lattice = await latticeProvider(page, cancellationToken).ConfigureAwait(false);
        var text = LatticeText.Build(lattice.Words).Text;
        return MatchRegex(strategy.TextPattern, text);
    }

    private static async Task<string?> EvaluateOcrZoneAsync(
        SeparationStrategy strategy,
        RasterPage page,
        Func<RasterPage, CancellationToken, Task<PageLattice>>? latticeProvider,
        CancellationToken cancellationToken)
    {
        if (latticeProvider is null || strategy.Zone is null)
            return null;

        var lattice = await latticeProvider(page, cancellationToken).ConfigureAwait(false);
        var extracted = ZonalExtractor.Extract(lattice, strategy.Zone);
        if (string.IsNullOrWhiteSpace(extracted.Text))
            return null;

        // Empty pattern means "any non-empty zone text counts as a hit" — mirrors how an empty
        // BarcodeValuePattern already means "matches any value".
        if (string.IsNullOrWhiteSpace(strategy.TextPattern))
            return extracted.Text;

        return MatchRegex(strategy.TextPattern, extracted.Text);
    }

    // Whole-page/zone regex matching, shared by Regex and OcrZone. Returns the first capture group if
    // present, else the whole match, trimmed — same convention this codebase's other regex triggers use.
    private static string? MatchRegex(string pattern, string text)
    {
        Match match;
        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            match = regex.Match(text);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!match.Success)
            return null;

        var group = match.Groups.Count > 1 && match.Groups[1].Success ? match.Groups[1] : match;
        return group.Value.Trim();
    }
}
