using Capture.Core.Import;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Pipeline;

/// <summary>
/// One document/profile segment produced by a pre-index step — the pipeline's generalization of
/// <see cref="Capture.Core.Import.PageSplit"/> that also records which profile (if any) the segment
/// was classified as. A null <see cref="Profile"/> means the pages didn't match any known trigger and
/// should land as an unassigned document (see MainViewModel's "No profile applied" Table-mode grouping).
/// </summary>
public sealed class ClassifiedSplit
{
    public IndexingProfile? Profile { get; init; }

    public required IReadOnlyList<int> SourcePages { get; init; }

    public IReadOnlyDictionary<Guid, string> SeparatorValues { get; init; } = new Dictionary<Guid, string>();
}

public sealed class PreIndexContext
{
    public required IReadOnlyList<RasterPage> Pages { get; init; }

    public required string SourcePath { get; init; }

    /// <summary>
    /// Profiles this step may assign to emerging segments. Today's default step only ever uses the
    /// first (and only) candidate; a future classification step can evaluate several.
    /// </summary>
    public required IReadOnlyList<IndexingProfile> CandidateProfiles { get; init; }

    /// <summary>The profile that actually drives splitting — null means "don't split, append
    /// everything into one document" (today's <see cref="ImportSeparationTrigger.None"/> behavior).</summary>
    public ImportProfile? ImportProfile { get; init; }
}

public interface IPreIndexStep
{
    Task<IReadOnlyList<ClassifiedSplit>> RunAsync(PreIndexContext context, CancellationToken cancellationToken = default);
}
