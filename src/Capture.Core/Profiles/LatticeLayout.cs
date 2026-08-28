using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

public static class LatticeLayout
{
    public static bool CenterInside(LatticeWord word, ZoneRect zone)
    {
        var cx = word.X + word.Width / 2f;
        var cy = word.Y + word.Height / 2f;
        return cx >= zone.X
            && cy >= zone.Y
            && cx <= zone.X + zone.Width
            && cy <= zone.Y + zone.Height;
    }

    public static int CompareReadingOrder(LatticeWord left, LatticeWord right)
    {
        var line = CompareLine(left, right);
        return line != 0 ? line : left.X.CompareTo(right.X);
    }

    public static List<LatticeWord> InReadingOrder(IEnumerable<LatticeWord> words)
    {
        var list = words.ToList();
        list.Sort(CompareReadingOrder);
        return list;
    }

    public static ZoneRect? Union(IReadOnlyList<LatticeWord> words)
    {
        if (words.Count == 0)
            return null;

        var left = words.Min(word => word.X);
        var top = words.Min(word => word.Y);
        var right = words.Max(word => word.X + word.Width);
        var bottom = words.Max(word => word.Y + word.Height);
        return new ZoneRect
        {
            X = left,
            Y = top,
            Width = Math.Max(0, right - left),
            Height = Math.Max(0, bottom - top)
        };
    }

    private static int CompareLine(LatticeWord left, LatticeWord right)
    {
        var leftY = left.Y + left.Height / 2f;
        var rightY = right.Y + right.Height / 2f;
        var threshold = Math.Max(left.Height, right.Height) * 0.6f;
        if (Math.Abs(leftY - rightY) <= threshold)
            return 0;
        return leftY.CompareTo(rightY);
    }
}
