using Capture.Core.Lattice;
using Capture.Core.Paths;

namespace Capture.Storage;

public sealed class JsonLatticeStore : ILatticeStore
{
    private readonly IAppPaths _paths;

    public JsonLatticeStore(IAppPaths paths)
    {
        _paths = paths;
    }

    public Task SaveAsync(Guid documentId, PageLattice lattice, CancellationToken cancellationToken = default)
    {
        return LatticeJson.WriteAsync(_paths.DocumentLatticePath(documentId, lattice.PageNumber), lattice, cancellationToken);
    }

    public Task<PageLattice?> GetAsync(Guid documentId, int pageNumber, CancellationToken cancellationToken = default)
    {
        return LatticeJson.ReadAsync(_paths.DocumentLatticePath(documentId, pageNumber), cancellationToken);
    }
}
