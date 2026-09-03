using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Import;

public static class PageSeparator
{
    public static bool Enabled(ImportProfile? profile) =>
        profile is not null && profile.Trigger != ImportSeparationTrigger.None;

    public static IReadOnlyList<PageSplit> Split(
        IReadOnlyList<RasterPage> pages,
        ImportProfile profile,
        IBarcodeDecoder? barcodes,
        IBlankPageDetector? blanks)
    {
        if (pages.Count == 0)
            return [];

        if (!Enabled(profile))
            return [All(pages)];

        return profile.Trigger switch
        {
            ImportSeparationTrigger.Barcode => SplitOnBarcode(pages, profile, barcodes),
            ImportSeparationTrigger.BlankPage => SplitOnBlank(pages, profile, blanks),
            ImportSeparationTrigger.EveryNPages => SplitEveryNPages(pages, profile),
            _ => [All(pages)]
        };
    }

    private static IReadOnlyList<PageSplit> SplitOnBarcode(
        IReadOnlyList<RasterPage> pages,
        ImportProfile profile,
        IBarcodeDecoder? barcodes)
    {
        if (barcodes is null)
            return [All(pages)];

        var current = new PageSplit();
        var results = new List<PageSplit>();

        foreach (var page in pages)
        {
            var decoded = barcodes.Decode(page.ImagePath, profile.BarcodeZone);
            var hit = decoded is not null
                && !string.IsNullOrWhiteSpace(decoded.Text)
                && (string.IsNullOrWhiteSpace(profile.BarcodeFormat)
                    || string.Equals(profile.BarcodeFormat, decoded.Format, StringComparison.OrdinalIgnoreCase))
                && BarcodePatterns.Matches(profile.BarcodeValuePattern, decoded.Text)
                ? decoded.Text
                : null;

            if (hit is not null && current.SourcePages.Count > 0)
                Flush(results, ref current);

            if (hit is null || !profile.DiscardSeparatorPage)
                current.SourcePages.Add(page.PageNumber);
        }

        Flush(results, ref current);
        return results.Count == 0 ? [All(pages)] : results;
    }

    private static IReadOnlyList<PageSplit> SplitOnBlank(
        IReadOnlyList<RasterPage> pages,
        ImportProfile profile,
        IBlankPageDetector? blanks)
    {
        if (blanks is null)
            return [All(pages)];

        var current = new PageSplit();
        var results = new List<PageSplit>();

        foreach (var page in pages)
        {
            var blank = blanks.IsBlank(page.ImagePath, profile.BlankInkPercent);
            if (blank)
            {
                Flush(results, ref current);
                if (!profile.DiscardSeparatorPage)
                    current.SourcePages.Add(page.PageNumber);
                continue;
            }

            current.SourcePages.Add(page.PageNumber);
        }

        Flush(results, ref current);
        return results.Count == 0 ? [All(pages)] : results;
    }

    private static IReadOnlyList<PageSplit> SplitEveryNPages(IReadOnlyList<RasterPage> pages, ImportProfile profile)
    {
        var threshold = Math.Max(1, profile.PageCount);
        var current = new PageSplit();
        var results = new List<PageSplit>();

        foreach (var page in pages)
        {
            if (current.SourcePages.Count >= threshold)
                Flush(results, ref current);

            current.SourcePages.Add(page.PageNumber);
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
