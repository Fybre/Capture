using Capture.Core.Redaction;

namespace Capture.Tests;

public class PresidioEntityNamesTests
{
    [Theory]
    [InlineData("PERSON", "Name")]
    [InlineData("US_SSN", "Social Security number")]
    [InlineData("CREDIT_CARD", "Credit card number")]
    [InlineData("EMAIL_ADDRESS", "Email address")]
    [InlineData("AU_TFN", "Tax File Number (AU)")]
    [InlineData("AU_MEDICARE", "Medicare number (AU)")]
    [InlineData("AU_ABN", "Business Number (ABN)")]
    [InlineData("AU_ACN", "Company Number (ACN)")]
    public void Describe_returns_the_known_friendly_name(string code, string expected)
    {
        Assert.Equal(expected, PresidioEntityNames.Describe(code));
    }

    [Fact]
    public void Describe_falls_back_to_a_title_cased_version_of_an_unknown_code()
    {
        Assert.Equal("Some New Entity", PresidioEntityNames.Describe("SOME_NEW_ENTITY"));
    }

    [Fact]
    public void Describe_handles_empty_input_without_throwing()
    {
        Assert.Equal(string.Empty, PresidioEntityNames.Describe(string.Empty));
    }
}
