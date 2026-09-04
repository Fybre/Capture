using Capture.Core.Profiles;

namespace Capture.Core.Import;

public enum ImportSeparationTrigger
{
    None = 0,
    Barcode = 1,
    BlankPage = 2,
    EveryNPages = 3
}

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
    public ImportSeparationTrigger Trigger { get; set; } = ImportSeparationTrigger.None;

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.EveryNPages"/>.</summary>
    public int PageCount { get; set; } = 1;

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.BlankPage"/>.</summary>
    public int BlankInkPercent { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.Barcode"/> — the
    /// sample document the zone below was drawn against, for the Designer's "load a sample, show the
    /// page, drag a rectangle" interaction.</summary>
    public string? SampleFileName { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.Barcode"/> — owned
    /// here directly rather than borrowed from an <c>IndexingProfile</c> field's own zone.</summary>
    public ZoneRect? BarcodeZone { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.Barcode"/> — which
    /// page of <see cref="SampleFileName"/> <see cref="BarcodeZone"/> was drawn on.</summary>
    public int BarcodePageNumber { get; set; } = 1;

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.Barcode"/> — an
    /// optional symbology filter (e.g. "CODE_128"); null/empty matches any format the decoder returns.</summary>
    public string? BarcodeFormat { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.Barcode"/> — an
    /// optional regex the decoded barcode text must match; null/empty matches any value.</summary>
    public string? BarcodeValuePattern { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="ImportSeparationTrigger.Barcode"/> or
    /// <see cref="ImportSeparationTrigger.BlankPage"/> — there's no separator page to discard for
    /// EveryNPages.</summary>
    public bool DiscardSeparatorPage { get; set; }

    /// <summary>The seam auto-classification will use later: which IndexingProfiles are valid for
    /// documents this Import Profile produces. Empty = no constraint (today's implicit behavior — the
    /// reviewer picks any Indexing Profile by hand). Not consumed by any classification logic yet — its
    /// one immediate use is constraining the Indexing Profile picker to this list.</summary>
    public List<Guid> IndexingProfileIds { get; set; } = [];

    /// <summary>Which <c>BatchProfile</c> documents this import profile produces should join —
    /// batching is controlled from here rather than selected independently, so a user configuring one
    /// import profile controls both separation and batching (and, via <see cref="IndexingProfileIds"/>,
    /// indexing) from a single place. Null means no batch profile — falls back to
    /// <c>WatchSettings.NoBatchProfileBehavior</c>, same as before this concept existed.</summary>
    public Guid? BatchProfileId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
}
