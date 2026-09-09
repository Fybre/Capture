using Capture.Core.Lattice;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public static class DocumentText
{
    public static string FromLattices(IEnumerable<PageLattice> lattices)
    {
        var pages = lattices.OrderBy(page => page.PageNumber).ToList();
        return string.Join("\n\n", pages.Select(page =>
        {
            var text = LatticeText.Build(page.Words).Text;
            return string.IsNullOrWhiteSpace(text) ? $"--- Page {page.PageNumber} ---" : $"--- Page {page.PageNumber} ---\n{text}";
        }));
    }
}
