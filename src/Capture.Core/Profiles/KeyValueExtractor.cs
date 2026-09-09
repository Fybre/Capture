using System.Text.RegularExpressions;
using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

public static class KeyValueExtractor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static PatternExtractResult Extract(IReadOnlyList<PageLattice> pages, IndexField field)
    {
        var empty = new PatternExtractResult(string.Empty, 0, null, field.PageNumber);
        if (string.IsNullOrWhiteSpace(field.KeyPattern) || string.IsNullOrWhiteSpace(field.ValuePattern))
            return empty;

        Regex keyRegex;
        Regex valueRegex;
        try
        {
            keyRegex = new Regex(field.KeyPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            valueRegex = new Regex(field.ValuePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
        }
        catch (ArgumentException)
        {
            return empty;
        }

        var targets = pages
            .Where(page => PatternPages.Matches(page.PageNumber, field))
            .OrderBy(page => page.PageNumber)
            .ToList();

        PatternExtractResult? last = null;
        foreach (var page in targets)
        {
            var hit = ExtractPage(page, field, keyRegex, valueRegex);
            if (hit is null)
                continue;
            if (field.Occurrence == MatchOccurrence.First)
                return hit;
            last = hit;
        }

        return last ?? empty;
    }

    private static PatternExtractResult? ExtractPage(
        PageLattice page,
        IndexField field,
        Regex keyRegex,
        Regex valueRegex)
    {
        var source = PatternPages.WordsOnPage(page, field);
        if (source is null)
            return null;

        var built = LatticeText.Build(source);
        if (built.Text.Length == 0)
            return null;

        Match? chosenValue = null;
        try
        {
            foreach (Match key in keyRegex.Matches(built.Text))
            {
                if (!key.Success)
                    continue;

                var value = valueRegex.Match(built.Text, key.Index + key.Length);
                if (!value.Success)
                    continue;

                chosenValue = value;
                if (field.Occurrence == MatchOccurrence.First)
                    break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (chosenValue is null)
            return null;

        var valueWords = LatticeText.WordsCovering(built, chosenValue.Index, chosenValue.Index + chosenValue.Length);
        if (valueWords.Count == 0)
            return null;

        var bounds = LatticeLayout.Union(valueWords);
        if (bounds is not null)
            bounds.PageNumber = page.PageNumber;

        var confidence = (float)valueWords.Average(word => word.Confidence);
        return new PatternExtractResult(chosenValue.Value.Trim(), confidence, bounds, page.PageNumber);
    }
}
