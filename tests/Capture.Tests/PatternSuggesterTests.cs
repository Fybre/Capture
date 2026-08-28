using Capture.Core.Profiles;

namespace Capture.Tests;

public class PatternSuggesterTests
{
    [Fact]
    public void Key_keeps_words_and_flexes_spacing_and_punctuation()
    {
        var pattern = PatternSuggester.ForKey("Invoice No:");
        Assert.Equal(@"Invoice\s*No\s*:?", pattern);
        Assert.Matches(pattern, "Invoice No:");
        Assert.Matches(pattern, "Invoice  No");
        Assert.Matches(pattern, "Invoice No");
    }

    [Fact]
    public void Value_uses_format_when_not_string()
    {
        Assert.Equal(ValuePatterns.For(FieldFormat.Integer), PatternSuggester.ForValue("12/08/2026", FieldFormat.Integer));
        Assert.Equal(ValuePatterns.For(FieldFormat.Date), PatternSuggester.ForValue("12/08/2026", FieldFormat.Date));
    }

    [Fact]
    public void String_value_becomes_generic_from_sample()
    {
        Assert.Equal(@"-?\d+", PatternSuggester.ForValue("00001521", FieldFormat.String));
        Assert.Equal(@"[A-Za-z0-9]+", PatternSuggester.ForValue("AB12", FieldFormat.String));
        Assert.Equal(@"\S+", PatternSuggester.ForValue("A.B.N", FieldFormat.String));
    }
}
