namespace Capture.Core.Import;

/// <summary>
/// A named, reusable configuration describing how one imported file gets carved into documents —
/// "how do I carve up what's coming in." Independent of any <c>IndexingProfile</c> (which now only
/// governs field extraction for an already-separated document) and of <c>BatchProfile</c> (which
/// groups already-separated documents into batches) — chosen separately, alongside those, per watch
/// folder or per manual import.
/// </summary>
public sealed class ImportProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New import profile";

    /// <summary>The sample document every zone-based strategy in <see cref="Strategies"/> (Barcode,
    /// OcrZone, and eventually Similarity's reference page) draws against — one shared sample per
    /// profile, not one per strategy, so a real separator sheet's barcode and surrounding text can
    /// both be marked on the same page.</summary>
    public string? SampleFileName { get; set; }

    /// <summary>The buildable list of separation conditions — see <see cref="SeparationStrategy"/>.
    /// Empty means no splitting (equivalent to the old <c>Trigger.None</c>): everything appends into
    /// one document.</summary>
    public List<SeparationStrategy> Strategies { get; set; } = [];

    /// <summary>How <see cref="Strategies"/> combine into a single "does this page start a new
    /// document" decision.</summary>
    public SeparationMatchMode MatchMode { get; set; } = SeparationMatchMode.Any;

    /// <summary>Only meaningful when <see cref="MatchMode"/> is <see cref="SeparationMatchMode.AtLeast"/>.</summary>
    public int MatchMinimum { get; set; } = 1;

    /// <summary>The seam auto-classification will use later: which IndexingProfiles are valid for
    /// documents this Import Profile produces. Empty = no constraint (today's implicit behavior — the
    /// reviewer picks any Indexing Profile by hand). Not consumed by any classification logic yet — its
    /// one immediate use is constraining the Indexing Profile picker to this list.</summary>
    public List<Guid> IndexingProfileIds { get; set; } = [];

    /// <summary>Applied automatically when nothing else has already resolved an Indexing Profile for
    /// an incoming document — no Indexing Profile was explicitly chosen for the import (manual toolbar
    /// selection or watch folder configuration), and auto-classification either isn't enabled or found
    /// no match. Null means no automatic fallback — the document is left unindexed until someone
    /// applies a profile by hand, same as today's behavior.</summary>
    public Guid? DefaultIndexingProfileId { get; set; }

    /// <summary>Which <c>BatchProfile</c> documents this import profile produces should join —
    /// batching is controlled from here rather than selected independently, so a user configuring one
    /// import profile controls both separation and batching (and, via <see cref="IndexingProfileIds"/>,
    /// indexing) from a single place. Null means no batch profile — falls back to
    /// <c>WatchSettings.NoBatchProfileBehavior</c>, same as before this concept existed.</summary>
    public Guid? BatchProfileId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Short display summary for list views (e.g. the Import Profiles window's grid) — there's
    /// no longer a single "separation method" to show now that <see cref="Strategies"/> is a list.</summary>
    public string StrategySummary => Strategies.Count switch
    {
        0 => "No strategies",
        1 => Strategies[0].Type.ToString(),
        _ => $"{Strategies.Count} strategies ({MatchMode})"
    };
}
