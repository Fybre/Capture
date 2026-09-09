namespace Capture.Core.Lattice;

public interface ILatticeStore
{
    Task SaveAsync(Guid documentId, PageLattice lattice, CancellationToken cancellationToken = default);

    Task<PageLattice?> GetAsync(Guid documentId, int pageNumber, CancellationToken cancellationToken = default);
}
