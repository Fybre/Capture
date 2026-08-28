using System.Text.RegularExpressions;
using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;

namespace Capture.Core.Batches;

/// <summary>A page that triggered a batch boundary, with whatever value the trigger captured.</summary>
public sealed record BatchTriggerHit(int PageNumber, string CapturedValue, bool DiscardPage);

/// <summary>
/// Detects batch boundaries independently of document splitting (<c>PageSeparator</c>). Whole-page only —
/// no zone, matching <c>BatchProfile</c>'s barcode/regex triggers.
/// </summary>
public static class BatchSeparator
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether <paramref name="profile"/>'s trigger requires scanning pages at all — <see cref="BatchTrigger.NewBatchPerFile"/>,
    /// <see cref="BatchTrigger.EveryNPages"/>, and <see cref="BatchTrigger.Manual"/> are handled entirely by <c>BatchAllocator</c>.</summary>
    public static bool NeedsPageScan(BatchProfile? profile) =>
        profile is not null && profile.Trigger is BatchTrigger.Barcode or BatchTrigger.RegexMatch;

    public static async Task<IReadOnlyList<BatchTriggerHit>> DetectAsync(
        IReadOnlyList<RasterPage> pages,
        BatchProfile profile,
        IBarcodeDecoder? barcodes,
        Func<RasterPage, CancellationToken, Task<string>>? pageTextProvider,
        CancellationToken cancellationToken = default)
    {
        var hits = new List<BatchTriggerHit>();
        if (!NeedsPageScan(profile))
            return hits;

        foreach (var page in pages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hit = profile.Trigger == BatchTrigger.Barcode
                ? DetectBarcode(page, profile, barcodes)
                : await DetectRegexAsync(page, profile, pageTextProvider, cancellationToken).ConfigureAwait(false);

            if (hit is not null)
                hits.Add(hit);
        }

        return hits;
    }

    private static BatchTriggerHit? DetectBarcode(RasterPage page, BatchProfile profile, IBarcodeDecoder? barcodes)
    {
        var decoded = barcodes?.Decode(page.ImagePath, zone: null);
        if (decoded is null || string.IsNullOrWhiteSpace(decoded.Text))
            return null;

        if (!string.IsNullOrWhiteSpace(profile.BarcodeFormat)
            && !string.Equals(profile.BarcodeFormat, decoded.Format, StringComparison.OrdinalIgnoreCase))
            return null;

        if (!BarcodePatterns.Matches(profile.BarcodeValuePattern, decoded.Text))
            return null;

        return new BatchTriggerHit(page.PageNumber, decoded.Text, profile.DiscardSeparatorPage);
    }

    private static async Task<BatchTriggerHit?> DetectRegexAsync(
        RasterPage page,
        BatchProfile profile,
        Func<RasterPage, CancellationToken, Task<string>>? pageTextProvider,
        CancellationToken cancellationToken)
    {
        if (pageTextProvider is null || string.IsNullOrWhiteSpace(profile.TextPattern))
            return null;

        var text = await pageTextProvider(page, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        Match match;
        try
        {
            var regex = new Regex(profile.TextPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
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
        var captured = group.Value.Trim();
        return captured.Length == 0 ? null : new BatchTriggerHit(page.PageNumber, captured, profile.DiscardSeparatorPage);
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
