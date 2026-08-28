using Capture.Core.Import;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Profiles;

namespace Capture.Storage;

public sealed class ProfileSampleService : IProfileSampleService
{
    private readonly IAppPaths _paths;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly ILatticeBuilder _latticeBuilder;

    public ProfileSampleService(
        IAppPaths paths,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        ILatticeBuilder latticeBuilder)
    {
        _paths = paths;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _latticeBuilder = latticeBuilder;
    }

    public async Task PrepareAsync(
        IndexingProfile profile,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!File.Exists(sourcePath))
            throw new FileNotFoundException("Sample file not found.", sourcePath);

        _paths.EnsureCreated();
        var pagesDirectory = _paths.ProfilePagesDirectory(profile.Id);
        Directory.CreateDirectory(pagesDirectory);
        Directory.CreateDirectory(_paths.ProfileLatticeDirectory(profile.Id));

        var originalName = Path.GetFileName(sourcePath);
        var samplePath = _paths.ProfileSamplePath(profile.Id, originalName);
        File.Copy(sourcePath, samplePath, overwrite: true);
        profile.SampleFileName = originalName;

        var rasters = ImportFormats.IsPdf(sourcePath)
            ? await _pdfRasterizer.RasterizeAsync(samplePath, pagesDirectory, DocumentImporter.PreviewDpi, cancellationToken)
                .ConfigureAwait(false)
            : await _imageImporter.ImportAsync(samplePath, pagesDirectory, cancellationToken)
                .ConfigureAwait(false);

        var document = new CaptureDocument
        {
            Id = profile.Id,
            OriginalFileName = originalName,
            StoredPath = samplePath,
            Source = DocumentSource.Import
        };

        foreach (var raster in rasters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = new DocumentPage
            {
                DocumentId = profile.Id,
                PageNumber = raster.PageNumber,
                SourcePageNumber = raster.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            };

            var lattice = await _latticeBuilder.BuildPageAsync(document, page, cancellationToken)
                .ConfigureAwait(false);
            await LatticeJson.WriteAsync(
                    _paths.ProfileLatticePath(profile.Id, raster.PageNumber),
                    lattice,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public IReadOnlyList<string> GetPageImagePaths(Guid profileId)
    {
        var directory = _paths.ProfilePagesDirectory(profileId);
        if (!Directory.Exists(directory))
            return [];

        return Directory.EnumerateFiles(directory, "*.png")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<PageLattice?> GetLatticeAsync(
        Guid profileId,
        int pageNumber,
        CancellationToken cancellationToken = default)
    {
        return LatticeJson.ReadAsync(_paths.ProfileLatticePath(profileId, pageNumber), cancellationToken);
    }
}
