using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public interface IProfileApplicator
{
    IReadOnlyList<IndexValue> Apply(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null);

    Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null,
        CancellationToken cancellationToken = default);

    /// <summary>Field-list-based entry point for a caller with no whole <see cref="IndexingProfile"/> —
    /// today, just <c>BatchProfile</c>'s own <c>Fields</c>/<c>Scripts</c>/<c>SharedScriptSource</c>,
    /// captured once at batch-boundary detection time. The <see cref="IndexingProfile"/>-based
    /// overload above is a thin wrapper over this one.</summary>
    Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IReadOnlyList<IndexField> fields,
        IReadOnlyList<FieldScript> scripts,
        string sharedScriptSource,
        IReadOnlyList<PageLattice> lattices,
        string? profileName = null,
        string? locale = null,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CaptureDocument? document = null,
        CancellationToken cancellationToken = default);
}
