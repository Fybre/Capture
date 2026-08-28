using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Profiles;

namespace Capture.Core.Indexing;

public interface IProfileApplicator
{
    IReadOnlyList<IndexValue> Apply(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        MacroContext? macro = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null);

    Task<IReadOnlyList<IndexValue>> ApplyAsync(
        IndexingProfile profile,
        IReadOnlyList<PageLattice> lattices,
        MacroContext? macro = null,
        IReadOnlyList<DocumentPage>? pages = null,
        string? batchSeparatorValue = null,
        CancellationToken cancellationToken = default);
}
