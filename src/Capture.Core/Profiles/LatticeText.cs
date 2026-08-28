using System.Text;
using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

public static class LatticeText
{
    public readonly record struct Span(int Start, int End, LatticeWord Word);

    public sealed record Built(string Text, IReadOnlyList<Span> Map);

    public static Built Build(IEnumerable<LatticeWord> words)
    {
        var ordered = LatticeLayout.InReadingOrder(words);
        var text = new StringBuilder();
        var map = new List<Span>(ordered.Count);
        foreach (var word in ordered)
        {
            if (text.Length > 0)
                text.Append(' ');
            var start = text.Length;
            text.Append(word.Text);
            map.Add(new Span(start, start + word.Text.Length, word));
        }

        return new Built(text.ToString(), map);
    }

    public static List<LatticeWord> WordsCovering(Built built, int start, int end)
    {
        return built.Map
            .Where(span => span.Start < end && span.End > start)
            .Select(span => span.Word)
            .ToList();
    }
}
