using Capture.Core.Lattice;

namespace Capture.Core.Profiles;

public interface IProfileSampleService
{
    Task PrepareAsync(IndexingProfile profile, string sourcePath, CancellationToken cancellationToken = default);

    IReadOnlyList<string> GetPageImagePaths(Guid profileId);

    Task<PageLattice?> GetLatticeAsync(Guid profileId, int pageNumber, CancellationToken cancellationToken = default);
}
