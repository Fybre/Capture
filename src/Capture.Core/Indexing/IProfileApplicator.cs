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
        IReadOnlyList<IndexValue>? existingValues = null);

    Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        DefaultValueContext? context = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        IReadOnlyList<IndexValue>? existingValues = null,
        CancellationToken cancellationToken = default);
}
