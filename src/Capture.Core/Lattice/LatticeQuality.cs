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

    /// <summary>Whether built-in OCR read a page badly enough to try an external service — typically
    /// handwriting, which comes back as low-confidence fragments. A page with no words at all is taken
    /// to be blank and isn't sent.</summary>
    public static bool IsPoorOcr(IReadOnlyList<LatticeWord> words, int minConfidence)
    {
        if (words.Count == 0)
            return false;
        var characters = words.Sum(word => Math.Max(1, word.Text.Length));
        var confidence = words.Sum(word => word.Confidence * Math.Max(1, word.Text.Length)) / characters;
        return confidence < minConfidence || !LooksLikeRealText(words);
    }
}
