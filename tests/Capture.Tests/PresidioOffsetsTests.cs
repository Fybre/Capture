using Capture.Core.Redaction;

namespace Capture.Tests;

public class PresidioOffsetsTests
{
    [Fact]
    public void Offsets_before_any_supplementary_plane_character_are_unchanged()
    {
        const string text = "Contact Jane Doe for details.";
        Assert.Equal(8, PresidioOffsets.CodePointToUtf16Index(text, 8));
        Assert.Equal(16, PresidioOffsets.CodePointToUtf16Index(text, 16));
    }

    [Fact]
    public void Offsets_after_a_supplementary_plane_character_shift_by_the_surrogate_pair_width()
    {
        // U+1F600 GRINNING FACE — encoded as a UTF-16 surrogate pair (2 chars), 1 Python code point.
        var text = "\U0001F600AB";

        // Presidio (code points): 0 = the emoji, 1 = 'A', 2 = 'B'.
        // .NET (UTF-16 chars):     0-1 = the emoji (surrogate pair), 2 = 'A', 3 = 'B'.
        Assert.Equal(0, PresidioOffsets.CodePointToUtf16Index(text, 0));
        Assert.Equal(2, PresidioOffsets.CodePointToUtf16Index(text, 1));
        Assert.Equal(3, PresidioOffsets.CodePointToUtf16Index(text, 2));
    }

    [Fact]
    public void Offset_at_or_past_the_end_of_the_text_clamps_to_the_text_length()
    {
        const string text = "abc";
        Assert.Equal(3, PresidioOffsets.CodePointToUtf16Index(text, 3));
        Assert.Equal(3, PresidioOffsets.CodePointToUtf16Index(text, 10));
    }
}
