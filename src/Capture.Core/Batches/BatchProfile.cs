namespace Capture.Core.Batches;

public enum BatchTrigger
{
    /// <summary>One batch per source file.</summary>
    NewBatchPerFile = 0,

    /// <summary>Start a new batch once the running batch's page count reaches <see cref="BatchProfile.PageCount"/>.</summary>
    EveryNPages = 1,

    /// <summary>Start a new batch when a barcode is detected on a page (whole-page scan, no zone).
    /// Independent of document splitting — see <see cref="BatchProfile.BarcodeFormat"/>/<see cref="BatchProfile.BarcodeValuePattern"/>.</summary>
    Barcode = 2,

    /// <summary>Start a new batch when a page's whole text matches <see cref="BatchProfile.TextPattern"/>.</summary>
    RegexMatch = 3,

    /// <summary>Never auto-create — keep appending to the most recently created batch until the user
    /// starts a new one explicitly.</summary>
    Manual = 4
}

/// <summary>
/// A named, reusable configuration describing when a new <c>CaptureBatch</c> should start. Independent of
/// any <c>IndexingProfile</c> — chosen separately, alongside an indexing profile, per watch folder or per
/// manual import.
/// </summary>
public sealed class BatchProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New batch profile";
    public BatchTrigger Trigger { get; set; } = BatchTrigger.NewBatchPerFile;

    /// <summary>Used when <see cref="Trigger"/> is <see cref="BatchTrigger.EveryNPages"/>.</summary>
    public int PageCount { get; set; } = 1;

    /// <summary>Used when <see cref="Trigger"/> is <see cref="BatchTrigger.Barcode"/> — an optional symbology
    /// filter (e.g. "CODE_128"); null/empty matches any format the decoder returns.</summary>
    public string? BarcodeFormat { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="BatchTrigger.Barcode"/> — an optional regex the
    /// decoded barcode text must match; null/empty matches any value.</summary>
    public string? BarcodeValuePattern { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="BatchTrigger.RegexMatch"/> — a regex evaluated
    /// against the page's whole flattened text.</summary>
    public string? TextPattern { get; set; }

    /// <summary>Used when <see cref="Trigger"/> is <see cref="BatchTrigger.Barcode"/> or <see cref="BatchTrigger.RegexMatch"/>
    /// — when true, the page that triggered the batch boundary is dropped from every resulting document.</summary>
    public bool DiscardSeparatorPage { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Governs batching when nothing tells <see cref="BatchAllocator"/> what to do — a manual
/// import with no BatchProfile selected in the toolbar, or a watch folder with none configured. See
/// <c>WatchSettings.NoBatchProfileBehavior</c> (the setting) and <see cref="BatchProfileResolver"/> (where
/// it's applied).</summary>
public enum NoBatchProfileBehavior
{
    /// <summary>Start a new batch for every imported file, as if <see cref="BatchTrigger.NewBatchPerFile"/>
    /// had been selected.</summary>
    NewBatchPerFile = 0,

    /// <summary>Keep adding to whichever batch is already open, as if <see cref="BatchTrigger.Manual"/>
    /// had been selected — the same batch until the app restarts, or forever for a watch folder (see
    /// <c>IDocumentStore.GetLatestBatchForFolderAsync</c>).</summary>
    AddToOpenBatch = 1
}

/// <summary>Resolves what <see cref="BatchAllocator"/> should actually treat as the batch profile for an
/// import — reusing <see cref="BatchProfile"/>/<see cref="BatchTrigger"/> wholesale for the no-selection
/// case instead of teaching <see cref="BatchAllocator"/> a second, parallel notion of "no profile
/// behavior". A real selected/configured profile always wins outright.</summary>
public static class BatchProfileResolver
{
    // A fixed, never-persisted instance — nothing ever gives this Id significance (it's not saved to
    // the profile store), so reusing one avoids an allocation per import for the common case.
    private static readonly BatchProfile ImplicitNewBatchPerFile = new()
    {
        Name = "(no batch profile — new batch per file)",
        Trigger = BatchTrigger.NewBatchPerFile
    };

    public static BatchProfile? Resolve(BatchProfile? selected, NoBatchProfileBehavior noProfileBehavior) =>
        selected ?? (noProfileBehavior == NoBatchProfileBehavior.NewBatchPerFile ? ImplicitNewBatchPerFile : null);
}
