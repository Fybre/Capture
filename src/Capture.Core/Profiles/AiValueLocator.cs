using System.Text;
using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

/// <summary>Best-effort "where did this come from" for an AI-extracted value. Unlike a zonal, key/value,
/// or regex field, an AI field has no configured page/zone/pattern — the model reads the whole document
/// and returns a bare answer, so there's nothing to point at unless we go looking for it ourselves.
/// Searches every page's OCR/PDF-text words (in reading order, first match wins) for the value's text,
/// so a review-panel click can jump to and highlight it the same way a regex field's match does.
/// Money/number answers are normalized to bare digits/punctuation before matching — an extracted total
/// like "1249.60" otherwise never matches its own source text "$1,249.60", because the thousands comma
/// sits inside the token and breaks a literal substring search. Only "$" and "," are stripped, and only
/// from a search copy of the text mapped back to the original word spans, so the fix can never bridge
/// two separate OCR tokens together — it only ever collapses punctuation within one token.
/// Returns null whenever the model's answer isn't found even after that normalization (paraphrased,
/// reformatted, or computed values, e.g. a summed total not itself printed anywhere) — that's an
/// expected, silent miss, not a bug: the field still works, it just won't have a highlight to show.</summary>
public static class AiValueLocator
{
    private const string StrippedChars = "$,";

    public static PatternExtractResult? Locate(IReadOnlyList<PageLattice> pages, string? value)
    {
        var needle = Normalize(value?.Trim());
        if (needle.Text.Length == 0)
            return null;

        foreach (var page in pages.OrderBy(page => page.PageNumber))
        {
            var built = LatticeText.Build(page.Words);
            if (built.Text.Length == 0)
                continue;

            var haystack = Normalize(built.Text);
            var index = haystack.Text.IndexOf(needle.Text, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                continue;

            // Map the match back from the stripped search text to the original, unstripped offsets
            // before asking which words it covers — WordsCovering's spans were computed on built.Text.
            var originalStart = haystack.OriginalIndex[index];
            var originalEnd = haystack.OriginalIndex[index + needle.Text.Length - 1] + 1;

            var matchWords = LatticeText.WordsCovering(built, originalStart, originalEnd);
            if (matchWords.Count == 0)
                continue;

            var bounds = LatticeLayout.Union(matchWords);
            if (bounds is null)
                continue;
            bounds.PageNumber = page.PageNumber;

            var confidence = (float)matchWords.Average(word => word.Confidence);
            return new PatternExtractResult(value!.Trim(), confidence, bounds, page.PageNumber);
        }

        return null;
    }

    private readonly record struct NormalizedText(string Text, int[] OriginalIndex);

    private static NormalizedText Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return new NormalizedText(string.Empty, []);

        var sb = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (StrippedChars.IndexOf(text[i]) >= 0)
                continue;
            sb.Append(text[i]);
            map.Add(i);
        }

        return new NormalizedText(sb.ToString(), map.ToArray());
    }
}
