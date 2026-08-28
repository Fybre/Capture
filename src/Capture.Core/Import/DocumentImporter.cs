using System.Diagnostics;
using Capture.Core.Batches;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Store;

namespace Capture.Core.Import;

public sealed class DocumentImporter : IDocumentImporter
{
    public const int PreviewDpi = 150;

    private readonly IAppPaths _paths;
    private readonly IDocumentStore _store;
    private readonly IPdfRasterizer _pdfRasterizer;
    private readonly IImagePageImporter _imageImporter;
    private readonly ILatticeBuilder _latticeBuilder;
    private readonly IPdfSubsetWriter _pdfSubsetWriter;
    private readonly IPreIndexStep _preIndexStep;
    private readonly IBarcodeDecoder? _barcodes;

    public DocumentImporter(
        IAppPaths paths,
        IDocumentStore store,
        IPdfRasterizer pdfRasterizer,
        IImagePageImporter imageImporter,
        ILatticeBuilder latticeBuilder,
        IPdfSubsetWriter pdfSubsetWriter,
        IBarcodeDecoder? barcodes = null,
        IBlankPageDetector? blanks = null,
        IPreIndexStep? preIndexStep = null)
    {
        _paths = paths;
        _store = store;
        _pdfRasterizer = pdfRasterizer;
        _imageImporter = imageImporter;
        _latticeBuilder = latticeBuilder;
        _pdfSubsetWriter = pdfSubsetWriter;
        _barcodes = barcodes;
        _preIndexStep = preIndexStep ?? new ClassicSeparatorStep(barcodes, blanks);
    }

    public async Task<CaptureDocument> ImportFileAsync(
        string path,
        DocumentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _paths.EnsureCreated();

        var id = Guid.NewGuid();
        var originalName = Path.GetFileName(path);
        Directory.CreateDirectory(_paths.DocumentPagesDirectory(id));

        var storedPath = _paths.DocumentOriginalPath(id, originalName);
        var document = new CaptureDocument
        {
            Id = id,
            OriginalFileName = originalName,
            StoredPath = storedPath,
            Source = source,
            Status = DocumentStatus.Processing,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        try
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("File not found.", path);

            if (!ImportFormats.IsSupported(path))
                throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(path)}");

            File.Copy(path, storedPath, overwrite: true);

            var pagesDirectory = _paths.DocumentPagesDirectory(id);
            var rasters = ImportFormats.IsPdf(path)
                ? await _pdfRasterizer.RasterizeAsync(storedPath, pagesDirectory, PreviewDpi, cancellationToken)
                    .ConfigureAwait(false)
                : await _imageImporter.ImportAsync(storedPath, pagesDirectory, cancellationToken)
                    .ConfigureAwait(false);

            if (rasters.Count == 0)
                throw new InvalidOperationException("No pages were produced from the file.");

            var pages = rasters.Select(raster => new DocumentPage
            {
                DocumentId = id,
                PageNumber = raster.PageNumber,
                SourcePageNumber = raster.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            }).ToList();

            document.PageCount = pages.Count;
            document.Status = DocumentStatus.NeedsReview;
            await _store.SaveAsync(document, pages, cancellationToken).ConfigureAwait(false);
            await _latticeBuilder.BuildDocumentAsync(document, pages, cancellationToken).ConfigureAwait(false);
            return document;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Import failed for '{path}' (document {id}): {ex}");
            document.Status = DocumentStatus.Error;
            document.ErrorMessage = ex.Message;
            try
            {
                await _store.SaveAsync(document, Array.Empty<DocumentPage>(), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception saveEx)
            {
                Trace.TraceError($"Failed to persist error state for document {id}: {saveEx}");
            }

            return document;
        }
    }

    public async Task<IReadOnlyList<ImportedDocument>> ImportAsync(
        string path,
        DocumentSource source,
        IndexingProfile? profile = null,
        BatchProfile? batchProfile = null,
        CancellationToken cancellationToken = default)
    {
        if (!PageSeparator.Enabled(profile) && !BatchSeparator.NeedsPageScan(batchProfile))
        {
            var document = await ImportFileAsync(path, source, cancellationToken).ConfigureAwait(false);
            return [new ImportedDocument { Document = document }];
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _paths.EnsureCreated();
        if (!File.Exists(path))
            throw new FileNotFoundException("File not found.", path);
        if (!ImportFormats.IsSupported(path))
            throw new NotSupportedException($"Unsupported file type: {Path.GetExtension(path)}");

        var temp = Path.Combine(_paths.WorkDirectory, "split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var rasters = ImportFormats.IsPdf(path)
                ? await _pdfRasterizer.RasterizeAsync(path, temp, PreviewDpi, cancellationToken)
                    .ConfigureAwait(false)
                : await _imageImporter.ImportAsync(path, temp, cancellationToken)
                    .ConfigureAwait(false);
            if (rasters.Count == 0)
                throw new InvalidOperationException("No pages were produced from the file.");

            var preIndexContext = new PreIndexContext
            {
                Pages = rasters,
                SourcePath = path,
                CandidateProfiles = profile is null ? [] : [profile]
            };
            var splits = await _preIndexStep.RunAsync(preIndexContext, cancellationToken).ConfigureAwait(false);

            var batchHitsByPage = new Dictionary<int, BatchTriggerHit>();
            if (BatchSeparator.NeedsPageScan(batchProfile))
            {
                var hits = await BatchSeparator.DetectAsync(
                        rasters, batchProfile!, _barcodes, CreatePageTextProvider(path), cancellationToken)
                    .ConfigureAwait(false);
                foreach (var hit in hits)
                    batchHitsByPage[hit.PageNumber] = hit;
            }

            // A CaptureDocument can only ever belong to one batch, so a batch-trigger page that falls
            // mid-document (e.g. no indexing profile is splitting documents at all) has to force a
            // document break there too — otherwise every batch after the first has nowhere to attach to.
            splits = BatchSeparator.ExpandSplitsAtBoundaries(splits, batchHitsByPage);

            var results = new List<ImportedDocument>(splits.Count);
            var originalName = Path.GetFileName(path);

            // When an indexing profile also splits every page, a batch-trigger page (e.g. a barcode
            // separator) can end up as its own single-page split. If that trigger discards its page,
            // the split ends up with zero pages and produces no document — without carrying the hit
            // forward, the batch boundary it represents (and its captured value) would simply vanish,
            // leaving every subsequent document in the prior batch with no separator value at all.
            BatchTriggerHit? pendingHit = null;
            foreach (var split in splits)
            {
                cancellationToken.ThrowIfCancellationRequested();

                BatchTriggerHit? hit = null;
                foreach (var sourcePage in split.SourcePages)
                {
                    if (batchHitsByPage.TryGetValue(sourcePage, out var found))
                    {
                        hit = found;
                        break;
                    }
                }

                hit ??= pendingHit;

                var effectiveSplit = hit is { DiscardPage: true }
                    ? new ClassifiedSplit
                    {
                        Profile = split.Profile,
                        SourcePages = split.SourcePages.Where(page => page != hit.PageNumber).ToList(),
                        SeparatorValues = split.SeparatorValues
                    }
                    : split;

                if (effectiveSplit.SourcePages.Count == 0)
                {
                    pendingHit = hit;
                    continue;
                }

                pendingHit = null;
                results.Add(await MaterializeSplitAsync(
                    path, originalName, source, rasters, effectiveSplit, hit, cancellationToken).ConfigureAwait(false));
            }

            return results.Count == 0
                ? [new ImportedDocument { Document = await ImportFileAsync(path, source, cancellationToken).ConfigureAwait(false) }]
                : results;
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch
            {
            }
        }
    }

    // Never persisted — used only to reuse ILatticeBuilder's OCR/PDF-text extraction for whole-page
    // regex batch-trigger matching before any document exists yet.
    private Func<RasterPage, CancellationToken, Task<string>> CreatePageTextProvider(string sourcePath) =>
        async (raster, ct) =>
        {
            var throwawayId = Guid.NewGuid();
            var throwawayDocument = new CaptureDocument { OriginalFileName = string.Empty, StoredPath = sourcePath };
            var throwawayPage = new DocumentPage
            {
                DocumentId = throwawayId,
                PageNumber = raster.PageNumber,
                SourcePageNumber = raster.PageNumber,
                ImagePath = raster.ImagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            };
            var lattice = await _latticeBuilder.BuildPageAsync(throwawayDocument, throwawayPage, ct).ConfigureAwait(false);
            return LatticeText.Build(lattice.Words).Text;
        };

    private async Task<ImportedDocument> MaterializeSplitAsync(
        string path,
        string originalName,
        DocumentSource source,
        IReadOnlyList<RasterPage> rasters,
        ClassifiedSplit split,
        BatchTriggerHit? batchHit,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_paths.DocumentPagesDirectory(id));
        var storedPath = _paths.DocumentOriginalPath(id, originalName);

        // For PDFs, write a real trimmed copy containing only this split's own pages (in order) so
        // StoredPath actually means "this document's file" — its own page numbering then starts at 1,
        // same as PageNumber. Image sources have no subset-writing capability available, so they keep the
        // previous (known-limited) whole-file copy; SourcePageNumber stays the *original* page position
        // for those since the file itself wasn't trimmed.
        var isPdfSplit = ImportFormats.IsPdf(path);
        if (isPdfSplit)
            await _pdfSubsetWriter.WritePagesAsync(path, split.SourcePages, storedPath, cancellationToken).ConfigureAwait(false);
        else
            File.Copy(path, storedPath, overwrite: true);

        var pages = new List<DocumentPage>(split.SourcePages.Count);
        var number = 1;
        foreach (var sourcePage in split.SourcePages)
        {
            var raster = rasters.First(item => item.PageNumber == sourcePage);
            var imagePath = Path.Combine(_paths.DocumentPagesDirectory(id), $"{number:D4}{Path.GetExtension(raster.ImagePath)}");
            File.Copy(raster.ImagePath, imagePath, overwrite: true);
            pages.Add(new DocumentPage
            {
                DocumentId = id,
                PageNumber = number,
                SourcePageNumber = isPdfSplit ? number : sourcePage,
                ImagePath = imagePath,
                Width = raster.Width,
                Height = raster.Height,
                Dpi = raster.Dpi
            });
            number++;
        }

        var document = new CaptureDocument
        {
            Id = id,
            OriginalFileName = originalName,
            StoredPath = storedPath,
            Source = source,
            Status = DocumentStatus.NeedsReview,
            PageCount = pages.Count,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(document, pages, cancellationToken).ConfigureAwait(false);
        await _latticeBuilder.BuildDocumentAsync(document, pages, cancellationToken).ConfigureAwait(false);
        return new ImportedDocument
        {
            Document = document,
            SeparatorValues = split.SeparatorValues,
            StartsNewBatch = batchHit is not null,
            BatchSeparatorValue = batchHit?.CapturedValue
        };
    }

    public async Task<IReadOnlyList<CaptureDocument>> ImportFolderAsync(
        string folder,
        DocumentSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (!Directory.Exists(folder))
            throw new DirectoryNotFoundException(folder);

        var files = Directory.EnumerateFiles(folder)
            .Where(ImportFormats.IsSupported)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new List<CaptureDocument>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await ImportFileAsync(file, source, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }
}
