namespace Capture.Core.Redaction;

/// <summary>Presidio reports match offsets as Python Unicode code-point indices; .NET strings index by
/// UTF-16 code unit. These only differ for supplementary-plane characters (most emoji, some rare CJK)
/// appearing before a match — this conversion must happen before an offset is used as a .NET string
/// index, or matches after such a character land in the wrong place.</summary>
public static class PresidioOffsets
{
    /// <summary>Converts a Python Unicode code-point offset into <paramref name="text"/> to the
    /// equivalent .NET UTF-16 char index.</summary>
    public static int CodePointToUtf16Index(string text, int codePointOffset)
    {
        var charIndex = 0;
        var codePointIndex = 0;
        while (codePointIndex < codePointOffset && charIndex < text.Length)
        {
            charIndex += char.IsHighSurrogate(text[charIndex])
                && charIndex + 1 < text.Length
                && char.IsLowSurrogate(text[charIndex + 1])
                    ? 2
                    : 1;
            codePointIndex++;
        }

        return charIndex;
    }
}
