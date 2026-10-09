using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Platform;

namespace Capture.App.Services;

public sealed record VersionHistoryEntry(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("changes")] IReadOnlyList<string> Changes)
{
    /// <summary>"7 Oct 2026", or empty for an entry not yet released.</summary>
    public string DateDisplay => DateTime.TryParseExact(Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        ? date.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
        : string.Empty;

    /// <summary>Set by <see cref="VersionHistory.Load"/> for the entry matching the running build.</summary>
    public bool IsCurrent { get; init; }
}

/// <summary>The user-facing summary of important changes per release, shown from About ▸ Version
/// history. Lives in Assets/VersionHistory.json, newest first — add an entry for each release tag
/// (and give it a date when it ships).</summary>
public static class VersionHistory
{
    private static readonly Uri AssetUri = new("avares://Capture.App/Assets/VersionHistory.json");

    public static IReadOnlyList<VersionHistoryEntry> Load(string? currentVersion)
    {
        using var stream = AssetLoader.Open(AssetUri);
        return Parse(stream, currentVersion);
    }

    internal static IReadOnlyList<VersionHistoryEntry> Parse(Stream json, string? currentVersion)
    {
        var entries = JsonSerializer.Deserialize<List<VersionHistoryEntry>>(json) ?? [];
        var current = Normalize(currentVersion);
        return entries
            .Select(entry => entry with { IsCurrent = current is not null && Normalize(entry.Version) == current })
            .ToList();
    }

    /// <summary>Release tags look like "v0.8.12" and dev builds like "0.8.12-ci.4"; compare on the
    /// numeric part only.</summary>
    internal static System.Version? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        var trimmed = raw.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return System.Version.TryParse(trimmed, out var version) ? version : null;
    }
}
