using Capture.Core.Import;
using Capture.Core.Profiles;

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
/// indexing profile, per watch folder or per manual import. Configures its own batch-level indexing
/// directly — see <see cref="Fields"/> — rather than referencing an external <c>IndexingProfile</c>.
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

    /// <summary>Batch-level index fields — the same <see cref="IndexField"/> type, zone-drawing, and
    /// editing UI <c>IndexingProfile</c> uses, configured directly on this profile rather than by
    /// referencing an external <c>IndexingProfile</c>. Captured once, at the moment the batch boundary
    /// is detected (against the raw triggering page, before any document is materialized or the page
    /// possibly discarded — see <c>BatchSeparator.DetectAsync</c>/<c>BatchTriggerHit.CapturedFields</c>),
    /// and shared with every document that joins the batch — write-once, never re-extracted later,
    /// unlike an <c>IndexingProfile</c>'s own document-level fields.</summary>
    public List<IndexField> Fields { get; set; } = [];

    /// <summary>Profile-level C# scripts for <see cref="Fields"/> — same shape as
    /// <c>IndexingProfile.Scripts</c>. Only <c>ScriptTrigger.AfterFieldsPopulated</c> is meaningful
    /// here; there's no batch-level export for <c>BeforeExport</c>/<c>AfterExport</c> to hook into.</summary>
    public List<FieldScript> Scripts { get; set; } = [];

    /// <summary>C# helper functions available to every script/expression that runs against
    /// <see cref="Fields"/>/<see cref="Scripts"/> — same convention as
    /// <c>IndexingProfile.SharedScriptSource</c>.</summary>
    public string SharedScriptSource { get; set; } = "";

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
