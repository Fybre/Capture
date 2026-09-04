using Capture.Core.Import;

namespace Capture.Core.Batches;

public enum BatchMode
{
    /// <summary>One batch per source file.</summary>
    NewBatchPerFile = 0,

    /// <summary>Never auto-create — keep appending to the most recently created batch until the user
    /// starts a new one explicitly.</summary>
    Manual = 1,

    /// <summary>A new batch starts when <see cref="BatchProfile.Strategies"/> (combined via
    /// <see cref="BatchProfile.MatchMode"/>) hit on a page — the same buildable-condition-list system
    /// <c>ImportProfile</c> uses for document splitting, applied here to batch boundaries instead.</summary>
    UseStrategies = 2
}

/// <summary>
/// A named, reusable configuration describing when a new <c>CaptureBatch</c> should start. Independent of
/// any <c>IndexingProfile</c> for document-level field extraction — chosen separately, alongside an
/// indexing profile, per watch folder or per manual import. Can, however, designate its own
/// <see cref="IndexingProfileId"/> for batch-level fields — see that property.
/// </summary>
public sealed class BatchProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New batch profile";

    public BatchMode Mode { get; set; } = BatchMode.NewBatchPerFile;

    /// <summary>The sample document every zone-based strategy in <see cref="Strategies"/> (Barcode,
    /// OcrZone) draws against — one shared sample per profile, same convention <c>ImportProfile</c>
    /// uses. Only meaningful when <see cref="Mode"/> is <see cref="BatchMode.UseStrategies"/>.</summary>
    public string? SampleFileName { get; set; }

    /// <summary>The buildable list of batch-boundary conditions — see <see cref="SeparationStrategy"/>.
    /// Only evaluated when <see cref="Mode"/> is <see cref="BatchMode.UseStrategies"/>.</summary>
    public List<SeparationStrategy> Strategies { get; set; } = [];

    /// <summary>How <see cref="Strategies"/> combine into a single "does this page start a new batch"
    /// decision.</summary>
    public SeparationMatchMode MatchMode { get; set; } = SeparationMatchMode.Any;

    /// <summary>Only meaningful when <see cref="MatchMode"/> is <see cref="SeparationMatchMode.AtLeast"/>.</summary>
    public int MatchMinimum { get; set; } = 1;

    /// <summary>Which <c>IndexingProfile</c>'s batch-level (<c>IndexLevel.Batch</c>, including
    /// <c>FieldKind.BatchSeparatorValue</c>) fields get extracted once and shared with every document
    /// that joins a batch created under this profile — see
    /// <c>MainViewModel.ApplyBatchFieldsAsync</c>/<c>IIndexValueStore.GetBatchAsync</c>. Null means
    /// today's implicit behavior: whatever <c>IndexingProfile</c> the import itself resolved for
    /// document-level extraction is used for batch-level fields too.</summary>
    public Guid? IndexingProfileId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Short display summary for list views (e.g. the Batch Profiles window's grid) — mirrors
    /// <c>ImportProfile.StrategySummary</c>, but also has to represent <see cref="Mode"/> when it isn't
    /// <see cref="BatchMode.UseStrategies"/> (there's no strategy list to summarize in that case).</summary>
    public string StrategySummary => Mode switch
    {
        BatchMode.NewBatchPerFile => "New batch per file",
        BatchMode.Manual => "Manual",
        _ => Strategies.Count switch
        {
            0 => "No strategies",
            1 => Strategies[0].Type.ToString(),
            _ => $"{Strategies.Count} strategies ({MatchMode})"
        }
    };
}

/// <summary>Governs batching when nothing tells <see cref="BatchAllocator"/> what to do — a manual
/// import with no BatchProfile selected in the toolbar, or a watch folder with none configured. See
/// <c>WatchSettings.NoBatchProfileBehavior</c> (the setting) and <see cref="BatchProfileResolver"/> (where
/// it's applied).</summary>
public enum NoBatchProfileBehavior
{
    /// <summary>Start a new batch for every imported file, as if <see cref="BatchMode.NewBatchPerFile"/>
    /// had been selected.</summary>
    NewBatchPerFile = 0,

    /// <summary>Keep adding to whichever batch is already open, as if <see cref="BatchMode.Manual"/>
    /// had been selected — the same batch until the app restarts, or forever for a watch folder (see
    /// <c>IDocumentStore.GetLatestBatchForFolderAsync</c>).</summary>
    AddToOpenBatch = 1
}

/// <summary>Resolves what <see cref="BatchAllocator"/> should actually treat as the batch profile for an
/// import — reusing <see cref="BatchProfile"/>/<see cref="BatchMode"/> wholesale for the no-selection
/// case instead of teaching <see cref="BatchAllocator"/> a second, parallel notion of "no profile
/// behavior". A real selected/configured profile always wins outright.</summary>
public static class BatchProfileResolver
{
    // A fixed, never-persisted instance — nothing ever gives this Id significance (it's not saved to
    // the profile store), so reusing one avoids an allocation per import for the common case.
    private static readonly BatchProfile ImplicitNewBatchPerFile = new()
    {
        Name = "(no batch profile — new batch per file)",
        Mode = BatchMode.NewBatchPerFile
    };

    public static BatchProfile? Resolve(BatchProfile? selected, NoBatchProfileBehavior noProfileBehavior) =>
        selected ?? (noProfileBehavior == NoBatchProfileBehavior.NewBatchPerFile ? ImplicitNewBatchPerFile : null);
}
