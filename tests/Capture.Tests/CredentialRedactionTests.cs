using Capture.Core.Watch;

namespace Capture.Tests;

/// <summary>Covers SettingsViewModel's default-off "Include credentials" export gate — a normal export
/// must not carry a real secret, and importing a redacted export must not wipe out the local secret it
/// stood in for.</summary>
public class CredentialRedactionTests
{
    [Fact]
    public void A_configured_secret_is_replaced_with_the_placeholder()
    {
        Assert.Equal(CredentialRedaction.Placeholder, CredentialRedaction.Redact("sk-real-secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void A_never_configured_secret_stays_null(string? secret)
    {
        Assert.Null(CredentialRedaction.Redact(secret));
    }

    [Fact]
    public void Importing_the_placeholder_preserves_the_current_local_secret()
    {
        Assert.Equal("current-secret", CredentialRedaction.PreserveIfRedacted(CredentialRedaction.Placeholder, "current-secret"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Importing_a_blank_value_also_preserves_the_current_local_secret(string? imported)
    {
        Assert.Equal("current-secret", CredentialRedaction.PreserveIfRedacted(imported, "current-secret"));
    }

    [Fact]
    public void Importing_a_real_value_replaces_the_current_local_secret()
    {
        // The "Include credentials" opt-in path: a real secret in the file should actually be adopted.
        Assert.Equal("imported-secret", CredentialRedaction.PreserveIfRedacted("imported-secret", "current-secret"));
    }
}
