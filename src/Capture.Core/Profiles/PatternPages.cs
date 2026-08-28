using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

internal static class PatternPages
{
    public static bool Matches(int pageNumber, IndexField field) => field.PageScope switch
    {
        PageScope.First => pageNumber == 1,
        PageScope.Any => field.SearchZone is null || field.SearchZone.PageNumber == pageNumber,
        _ => pageNumber == Math.Max(1, field.PageNumber)
    };

    public static IEnumerable<LatticeWord>? WordsOnPage(PageLattice page, IndexField field)
    {
        if (field.SearchZone is not null && field.SearchZone.PageNumber != page.PageNumber)
            return null;

        var words = page.Words.AsEnumerable();
        if (field.SearchZone is not null)
            words = words.Where(word => LatticeLayout.CenterInside(word, field.SearchZone));

        return words;
    }
}
