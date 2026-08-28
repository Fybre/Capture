using System.Text.RegularExpressions;
using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

public static class RegexExtractor
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    public static PatternExtractResult Extract(IReadOnlyList<PageLattice> pages, IndexField field)
    {
        var empty = new PatternExtractResult(string.Empty, 0, null, field.PageNumber);
        if (string.IsNullOrWhiteSpace(field.ValuePattern))
            return empty;

        Regex regex;
        try
        {
            regex = new Regex(field.ValuePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
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
            var hit = ExtractPage(page, field, regex);
            if (hit is null)
                continue;
            if (field.Occurrence == MatchOccurrence.First)
                return hit;
            last = hit;
        }

        return last ?? empty;
    }

    private static PatternExtractResult? ExtractPage(PageLattice page, IndexField field, Regex regex)
    {
        var words = PatternPages.WordsOnPage(page, field);
        if (words is null)
            return null;

        var built = LatticeText.Build(words);
        if (built.Text.Length == 0)
            return null;

        Match? chosen = null;
        try
        {
            foreach (Match match in regex.Matches(built.Text))
            {
                if (!match.Success)
                    continue;
                chosen = match;
                if (field.Occurrence == MatchOccurrence.First)
                    break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (chosen is null)
            return null;

        var group = chosen.Groups.Count > 1 && chosen.Groups[1].Success
            ? chosen.Groups[1]
            : chosen;

        var captured = group.Value.Trim();
        if (captured.Length == 0)
            return null;

        var valueWords = LatticeText.WordsCovering(built, group.Index, group.Index + group.Length);
        if (valueWords.Count == 0)
            return null;

        var bounds = LatticeLayout.Union(valueWords);
        if (bounds is not null)
            bounds.PageNumber = page.PageNumber;

        var confidence = (float)valueWords.Average(word => word.Confidence);
        return new PatternExtractResult(captured, confidence, bounds, page.PageNumber);
    }
}
