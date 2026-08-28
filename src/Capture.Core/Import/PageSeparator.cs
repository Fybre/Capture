using Capture.Core.Indexing;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Import;

public static class PageSeparator
{
    public static bool Enabled(IndexingProfile? profile) =>
        profile is not null && profile.Separation.Trigger != DocumentSeparationTrigger.None;

    public static IReadOnlyList<PageSplit> Split(
        IReadOnlyList<RasterPage> pages,
        IndexingProfile profile,
        IBarcodeDecoder? barcodes,
        IBlankPageDetector? blanks)
    {
        if (pages.Count == 0)
            return [];

        if (!Enabled(profile))
            return [All(pages)];

        return profile.Separation.Trigger switch
        {
            DocumentSeparationTrigger.Barcode => SplitOnBarcode(pages, profile, barcodes),
            DocumentSeparationTrigger.BlankPage => SplitOnBlank(pages, profile, blanks),
            DocumentSeparationTrigger.EveryNPages => SplitEveryNPages(pages, profile),
            _ => [All(pages)]
        };
    }

    private static IReadOnlyList<PageSplit> SplitOnBarcode(
        IReadOnlyList<RasterPage> pages,
        IndexingProfile profile,
        IBarcodeDecoder? barcodes)
    {
        var field = profile.Fields.FirstOrDefault(
            item => item.Id == profile.Separation.BarcodeFieldId && item.Kind == FieldKind.Barcode);
        if (field is null || barcodes is null)
            return [All(pages)];

        var current = new PageSplit();
        var results = new List<PageSplit>();

        foreach (var page in pages)
        {
            var decoded = barcodes.Decode(page.ImagePath, field.Zone);
            var hit = decoded is not null
                && !string.IsNullOrWhiteSpace(decoded.Text)
                && BarcodePatterns.Matches(field, decoded.Text)
                ? decoded.Text
                : null;

            if (hit is not null && current.SourcePages.Count > 0)
                Flush(results, ref current);

            if (hit is not null)
                current.SeparatorValues[field.Id] = hit;

            if (hit is null || !profile.Separation.DiscardSeparatorPage)
                current.SourcePages.Add(page.PageNumber);
        }

        Flush(results, ref current);
        return results.Count == 0 ? [All(pages)] : results;
    }

    private static IReadOnlyList<PageSplit> SplitOnBlank(
        IReadOnlyList<RasterPage> pages,
        IndexingProfile profile,
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
                if (!profile.Separation.DiscardSeparatorPage)
                    current.SourcePages.Add(page.PageNumber);
                continue;
            }

            current.SourcePages.Add(page.PageNumber);
        }

        Flush(results, ref current);
        return results.Count == 0 ? [All(pages)] : results;
    }

    private static IReadOnlyList<PageSplit> SplitEveryNPages(IReadOnlyList<RasterPage> pages, IndexingProfile profile)
    {
        var threshold = Math.Max(1, profile.Separation.PageCount);
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
