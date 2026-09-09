namespace Capture.Core.Watch;

/// <summary>Shared logic for redacting secrets (AI API key, Therefore password/bearer token) out of a
/// settings export, and correctly re-importing a file that carries that redaction — see
/// SettingsViewModel.ExportSettingsAsync/ImportSettingsAsync.</summary>
public static class CredentialRedaction
{
    /// <summary>Written in place of a real secret when it's deliberately not exported — distinguishable
    /// from a genuinely blank/never-configured value, both to a human reading the file and to
    /// <see cref="PreserveIfRedacted"/> on the next import.</summary>
    public const string Placeholder = "(not exported)";

    /// <summary>What to write into an export: the placeholder if a secret is actually configured, or
    /// null if it was never set (no point claiming something was "not exported" that was never there).</summary>
    public static string? Redact(string? secret) => string.IsNullOrEmpty(secret) ? null : Placeholder;

    /// <summary>What to keep after importing: an imported value that's blank or the placeholder means
    /// "this credential wasn't part of the export," so the currently-configured local value is kept
    /// rather than being wiped out.</summary>
    public static string PreserveIfRedacted(string? imported, string current) =>
        string.IsNullOrEmpty(imported) || imported == Placeholder ? current : imported;
}
