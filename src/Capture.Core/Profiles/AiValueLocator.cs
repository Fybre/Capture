using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

/// <summary>Best-effort "where did this come from" for an AI-extracted value. Unlike a zonal, key/value,
/// or regex field, an AI field has no configured page/zone/pattern — the model reads the whole document
/// and returns a bare answer, so there's nothing to point at unless we go looking for it ourselves.
/// Searches every page's OCR/PDF-text words (in reading order, first match wins) for the value's exact
/// text, so a review-panel click can jump to and highlight it the same way a regex field's match does.
/// Returns null whenever the model's answer isn't a verbatim substring of the page text (paraphrased,
/// reformatted, or computed values, e.g. a normalized date or a summed total) — that's an expected,
/// silent miss, not a bug: the field still works, it just won't have a highlight to show.</summary>
public static class AiValueLocator
{
    public static PatternExtractResult? Locate(IReadOnlyList<PageLattice> pages, string? value)
    {
        var needle = value?.Trim();
        if (string.IsNullOrEmpty(needle))
            return null;

        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            var built = LatticeText.Build(page.Words);
            if (built.Text.Length == 0)
                continue;

            var index = built.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            var matchWords = LatticeText.WordsCovering(built, index, index + needle.Length);
            if (matchWords.Count == 0)
                continue;

            var bounds = LatticeLayout.Union(matchWords);
            if (bounds is null)
                continue;
            bounds.PageNumber = page.PageNumber;

            var confidence = (float)matchWords.Average(word => word.Confidence);
            return new PatternExtractResult(needle, confidence, bounds, page.PageNumber);
        }

        return null;
    }
}
