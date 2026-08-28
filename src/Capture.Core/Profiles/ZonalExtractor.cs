using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

public static class ZonalExtractor
{
    public static ZoneExtractResult Extract(PageLattice lattice, ZoneRect zone)
    {
        var hits = LatticeLayout.InReadingOrder(
            lattice.Words.Where(word => LatticeLayout.CenterInside(word, zone)));

        if (hits.Count == 0)
            return new ZoneExtractResult(string.Empty, 0);

        var text = string.Join(' ', hits.Select(word => word.Text));
        var confidence = (float)hits.Average(word => word.Confidence);
        return new ZoneExtractResult(text, confidence);
    }
}
