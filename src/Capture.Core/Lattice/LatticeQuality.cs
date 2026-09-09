namespace Capture.Core.Lattice;

public static class LatticeQuality
{
    public static bool LooksLikeRealText(IReadOnlyList<LatticeWord> words)
    {
        if (words.Count == 0)
            return false;

        var text = string.Concat(words.Select(word => word.Text));
        if (text.Length < 20)
            return false;

        var letters = text.Count(char.IsLetter);
        if (letters < 12)
            return false;

        var controls = text.Count(char.IsControl);
        if (controls > text.Length * 0.05)
            return false;

        var high = text.Count(ch => ch > 255);
        if (high > text.Length * 0.25)
            return false;

        var latin = text.Count(ch => ch is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z'));
        if (latin < 10)
            return false;

        return true;
    }
}
