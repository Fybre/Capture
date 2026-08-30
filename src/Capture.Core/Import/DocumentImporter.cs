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
        CancellationToken cancellationToken = default,
        int? imageDpiOverride = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _paths.EnsureCreated();

        var id = Guid.NewGuid();
        var originalName = Path.GetFileName(path);
        Trace.TraceInformation($"Importing '{originalName}' (document {id}, source {source})");
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
                : await _imageImporter.ImportAsync(storedPath, pagesDirectory, cancellationToken, imageDpiOverride)
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
            Trace.TraceInformation($"Imported '{originalName}' (document {id}, {pages.Count} page(s))");
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
        CancellationToken cancellationToken = default,
        int? imageDpiOverride = null)
    {
        if (!PageSeparator.Enabled(profile) && !BatchSeparator.NeedsPageScan(batchProfile))
        {
            var document = await ImportFileAsync(path, source, cancellationToken, imageDpiOverride).ConfigureAwait(false);
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
                : await _imageImporter.ImportAsync(path, temp, cancellationToken, imageDpiOverride)
                    .ConfigureAwait(false);
            if (rasters.Count == 0)
                throw new InvalidOperationException("No pages were produced from the file.");

            var originalName = Path.GetFileName(path);
            var results = await ImportRastersWithSplittingAsync(
                    rasters, path, originalName, source, profile, batchProfile, cancellationToken)
                .ConfigureAwait(false);

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

    public async Task<IReadOnlyList<ImportedDocument>> ImportScannedPagesAsync(
        IReadOnlyList<ScannedPageInfo> pages,
        DocumentSource source,
        IndexingProfile? profile = null,
        BatchProfile? batchProfile = null,
        CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0)
            throw new ArgumentException("At least one scanned page is required.", nameof(pages));

        _paths.EnsureCreated();
        var rasters = pages
            .Select((page, index) => new RasterPage(index + 1, page.ImagePath, page.Width, page.Height, page.Dpi))
            .ToList();
        // No natural "original file name" for a scan job made of several independent page images —
        // this becomes both the display name and the extension DocumentOriginalPath derives the stored
        // "original" copy's filename from (see MaterializeSplitAsync's no-source-path branch).
        var originalName = $"Scan {DateTimeOffset.Now:yyyy-MM-dd HHmmss}.png";

        if (!PageSeparator.Enabled(profile) && !BatchSeparator.NeedsPageScan(batchProfile))
        {
            var wholeDocument = new ClassifiedSplit { SourcePages = rasters.Select(raster => raster.PageNumber).ToList() };
            var imported = await MaterializeSplitAsync(
                    null, originalName, source, rasters, wholeDocument, batchHit: null, cancellationToken)
                .ConfigureAwait(false);
            return [imported];
        }

        var results = await ImportRastersWithSplittingAsync(
                rasters, sourcePath: null, originalName, source, profile, batchProfile, cancellationToken)
            .ConfigureAwait(false);

        return results.Count == 0
            ? [await MaterializeSplitAsync(
                null, originalName, source, rasters,
                new ClassifiedSplit { SourcePages = rasters.Select(raster => raster.PageNumber).ToList() },
                batchHit: null, cancellationToken).ConfigureAwait(false)]
            : results;
    }

    /// <summary>The splitting/batching core shared by <see cref="ImportAsync"/> (a single source file
    /// already rasterized into <paramref name="rasters"/>) and <see cref="ImportScannedPagesAsync"/> (no
    /// single source file — <paramref name="sourcePath"/> is null). Classifies pages into document
    /// splits, detects batch-trigger pages, then materializes each surviving split as its own document.</summary>
    private async Task<IReadOnlyList<ImportedDocument>> ImportRastersWithSplittingAsync(
        IReadOnlyList<RasterPage> rasters,
        string? sourcePath,
        string originalName,
        DocumentSource source,
        IndexingProfile? profile,
        BatchProfile? batchProfile,
        CancellationToken cancellationToken)
    {
        var preIndexContext = new PreIndexContext
        {
            Pages = rasters,
            SourcePath = sourcePath ?? originalName,
            CandidateProfiles = profile is null ? [] : [profile]
        };
        var splits = await _preIndexStep.RunAsync(preIndexContext, cancellationToken).ConfigureAwait(false);

        var batchHitsByPage = new Dictionary<int, BatchTriggerHit>();
        if (BatchSeparator.NeedsPageScan(batchProfile))
        {
            var hits = await BatchSeparator.DetectAsync(
                    rasters, batchProfile!, _barcodes, CreatePageTextProvider(sourcePath ?? originalName), cancellationToken)
                .ConfigureAwait(false);
            foreach (var hit in hits)
                batchHitsByPage[hit.PageNumber] = hit;
        }

        // A CaptureDocument can only ever belong to one batch, so a batch-trigger page that falls
        // mid-document (e.g. no indexing profile is splitting documents at all) has to force a
        // document break there too — otherwise every batch after the first has nowhere to attach to.
        splits = BatchSeparator.ExpandSplitsAtBoundaries(splits, batchHitsByPage);

        var results = new List<ImportedDocument>(splits.Count);

        // When an indexing profile also splits every page, a batch-trigger page (e.g. a barcode
        // separator) can end up as its own single-page split. If that trigger discards its page,
        // the split ends up with zero pages and produces no document — without carrying the hit
        // forward, the batch boundary it represents (and its captured value) would simply vanish,
        // leaving every subsequent document in the prior batch with no separator value at all.
        BatchTriggerHit? pendingHit = null;
        try
        {
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
                    sourcePath, originalName, source, rasters, effectiveSplit, hit, cancellationToken).ConfigureAwait(false));
            }
        }
        catch
        {
            // A split failing partway through (disk full, OCR error, cancellation, ...) used to
            // leave every split materialized before it as a permanently-committed "ghost" document —
            // present in the store but never surfaced to the caller — and retrying the same source
            // would then duplicate them. Roll back everything this file has committed so far instead,
            // so the whole file either imports completely or leaves nothing behind to retry against.
            foreach (var document in results)
            {
                try
                {
                    await _store.DeleteAsync(document.Document.Id, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception cleanupEx)
                {
                    Trace.TraceError($"Failed to roll back partially-imported document {document.Document.Id} for '{sourcePath ?? originalName}': {cleanupEx}");
                }
            }

            throw;
        }

        return results;
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
        string? sourcePath,
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
        // for those since the file itself wasn't trimmed. When there's no single source file at all (a
        // scan job assembled from independent page images), fall back to copying the split's own first
        // page image as a nominal "original" stand-in.
        var isPdfSplit = sourcePath is not null && ImportFormats.IsPdf(sourcePath);
        if (isPdfSplit)
            await _pdfSubsetWriter.WritePagesAsync(sourcePath!, split.SourcePages, storedPath, cancellationToken).ConfigureAwait(false);
        else if (sourcePath is not null)
            File.Copy(sourcePath, storedPath, overwrite: true);
        else
            File.Copy(rasters.First(item => item.PageNumber == split.SourcePages[0]).ImagePath, storedPath, overwrite: true);

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
        try
        {
            await _latticeBuilder.BuildDocumentAsync(document, pages, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Already persisted by SaveAsync above but never returned to the caller — clean it up
            // ourselves so it doesn't linger as an orphaned document ImportAsync's own rollback (which
            // only knows about splits that made it into its results list) can't see.
            try
            {
                await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception cleanupEx)
            {
                Trace.TraceError($"Failed to roll back partially-imported document {id} for '{sourcePath ?? originalName}': {cleanupEx}");
            }

            throw;
        }

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
