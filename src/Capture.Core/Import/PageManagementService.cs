using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Redaction;
using Capture.Core.Store;

namespace Capture.Core.Import;

/// <summary>Deletes, reorders, splits, and merges pages of already-imported documents — the post-import
/// counterpart to <see cref="IDocumentImporter"/>'s import-time page splitting
/// (<c>DocumentImporter.MaterializeSplitAsync</c>), whose conventions this mirrors: for a PDF-sourced
/// document, once <c>StoredPath</c> is rewritten to hold exactly the surviving/reordered pages, every
/// remaining page's <c>SourcePageNumber</c> is reset to equal its new <c>PageNumber</c>; for an
/// image-sourced document, <c>StoredPath</c>/<c>SourcePageNumber</c> are left alone, since
/// <see cref="ILatticeBuilder"/> never uses <c>SourcePageNumber</c> for non-PDF OCR routing (each page's
/// own <c>ImagePath</c> is already the OCR source).</summary>
public sealed class PageManagementService : IPageManagementService
{
    private readonly IAppPaths _paths;
    private readonly IDocumentStore _store;
    private readonly ILatticeBuilder _latticeBuilder;
    private readonly ILatticeStore _latticeStore;
    private readonly IPdfSubsetWriter _pdfSubsetWriter;
    private readonly IMergedDocumentWriter _mergedDocumentWriter;
    private readonly IIndexValueStore _indexValues;
    private readonly IRedactionCandidateStore _redactionCandidates;

    public PageManagementService(
        IAppPaths paths,
        IDocumentStore store,
        ILatticeBuilder latticeBuilder,
        ILatticeStore latticeStore,
        IPdfSubsetWriter pdfSubsetWriter,
        IMergedDocumentWriter mergedDocumentWriter,
        IIndexValueStore indexValues,
        IRedactionCandidateStore redactionCandidates)
    {
        _paths = paths;
        _store = store;
        _latticeBuilder = latticeBuilder;
        _latticeStore = latticeStore;
        _pdfSubsetWriter = pdfSubsetWriter;
        _mergedDocumentWriter = mergedDocumentWriter;
        _indexValues = indexValues;
        _redactionCandidates = redactionCandidates;
    }

    public async Task<CaptureDocument> DeletePagesAsync(
        Guid documentId, IReadOnlyList<int> pageNumbers, CancellationToken cancellationToken = default)
    {
        var document = await GetDocumentOrThrowAsync(documentId, cancellationToken).ConfigureAwait(false);
        var pages = await _store.GetPagesAsync(documentId, cancellationToken).ConfigureAwait(false);
        var toDelete = new HashSet<int>(pageNumbers);
        var survivingOldOrder = pages
            .Select(p => p.PageNumber)
            .Where(n => !toDelete.Contains(n))
            .OrderBy(n => n)
            .ToList();

        if (survivingOldOrder.Count == 0)
            throw new InvalidOperationException(
                "Cannot delete every page of a document — remove the document itself instead.");

        return await RewriteDocumentAsync(document, pages, survivingOldOrder, dropUnmappedIndexValues: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<CaptureDocument> ReorderPagesAsync(
        Guid documentId, IReadOnlyList<int> newPageOrder, CancellationToken cancellationToken = default)
    {
        var document = await GetDocumentOrThrowAsync(documentId, cancellationToken).ConfigureAwait(false);
        var pages = await _store.GetPagesAsync(documentId, cancellationToken).ConfigureAwait(false);

        var existing = pages.Select(p => p.PageNumber).OrderBy(n => n).ToList();
        var requested = newPageOrder.OrderBy(n => n).ToList();
        if (newPageOrder.Count != pages.Count || !existing.SequenceEqual(requested))
            throw new ArgumentException(
                "newPageOrder must be a permutation of the document's current page numbers.", nameof(newPageOrder));

        return await RewriteDocumentAsync(document, pages, newPageOrder, dropUnmappedIndexValues: false, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<(CaptureDocument First, CaptureDocument Second)> SplitDocumentAsync(
        Guid documentId, int splitBeforePageNumber, CancellationToken cancellationToken = default)
    {
        var document = await GetDocumentOrThrowAsync(documentId, cancellationToken).ConfigureAwait(false);
        var pages = await _store.GetPagesAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (splitBeforePageNumber <= 1 || splitBeforePageNumber > pages.Count)
            throw new ArgumentOutOfRangeException(
                nameof(splitBeforePageNumber), splitBeforePageNumber, $"Must be between 2 and {pages.Count}.");

        var firstOldOrder = pages.Select(p => p.PageNumber).Where(n => n < splitBeforePageNumber).OrderBy(n => n).ToList();
        var secondOldOrder = pages.Select(p => p.PageNumber).Where(n => n >= splitBeforePageNumber).OrderBy(n => n).ToList();

        // Materialize the second (new) document first — it reads document.StoredPath and the surviving
        // pages' current image files, so it must run before RewriteDocumentAsync mutates either as part
        // of trimming the original down to just the first half.
        var second = await MaterializeSplitDocumentAsync(document, pages, secondOldOrder, cancellationToken)
            .ConfigureAwait(false);
        var first = await RewriteDocumentAsync(document, pages, firstOldOrder, dropUnmappedIndexValues: false, cancellationToken)
            .ConfigureAwait(false);
        return (first, second);
    }

    public async Task<CaptureDocument> MergeDocumentsAsync(
        IReadOnlyList<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        var ids = documentIds.Distinct().ToList();
        if (ids.Count < 2)
            throw new ArgumentException("Select at least two different documents to merge.", nameof(documentIds));

        var documents = new List<CaptureDocument>(ids.Count);
        var pagesByDocument = new List<IReadOnlyList<DocumentPage>>(ids.Count);
        foreach (var id in ids)
        {
            documents.Add(await GetDocumentOrThrowAsync(id, cancellationToken).ConfigureAwait(false));
            pagesByDocument.Add((await _store.GetPagesAsync(id, cancellationToken).ConfigureAwait(false))
                .OrderBy(page => page.PageNumber).ToList());
        }

        var target = documents[0];
        var sourcePages = pagesByDocument.SelectMany(pages => pages).ToList();
        var mergedPdfPath = _paths.DocumentOriginalPath(target.Id, "merged.pdf");
        var tempPdfPath = mergedPdfPath + $".tmp-{Guid.NewGuid():N}";
        await _mergedDocumentWriter.WriteAsync(sourcePages, tempPdfPath, cancellationToken).ConfigureAwait(false);

        var targetPagesDirectory = _paths.DocumentPagesDirectory(target.Id);
        Directory.CreateDirectory(targetPagesDirectory);
        var stagingDirectory = Path.Combine(targetPagesDirectory, $".merge-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);

        var mergedPages = new List<DocumentPage>(sourcePages.Count);
        var mergedLattices = new List<PageLattice>();
        var mergedCandidates = new List<RedactionCandidate>();
        var pageNumber = 1;
        try
        {
            for (var documentIndex = 0; documentIndex < documents.Count; documentIndex++)
            {
                var document = documents[documentIndex];
                var pageOffset = pageNumber - 1;
                foreach (var page in pagesByDocument[documentIndex])
                {
                    var extension = Path.GetExtension(page.ImagePath);
                    var fileName = $"{pageNumber:D4}{extension}";
                    File.Copy(page.ImagePath, Path.Combine(stagingDirectory, fileName), overwrite: true);
                    mergedPages.Add(new DocumentPage
                    {
                        DocumentId = target.Id,
                        PageNumber = pageNumber,
                        SourcePageNumber = pageNumber,
                        ImagePath = Path.Combine(targetPagesDirectory, fileName),
                        Width = page.Width,
                        Height = page.Height,
                        Dpi = page.Dpi
                    });

                    var lattice = await _latticeStore.GetAsync(document.Id, page.PageNumber, cancellationToken)
                        .ConfigureAwait(false);
                    if (lattice is not null)
                    {
                        mergedLattices.Add(new PageLattice
                        {
                            PageNumber = pageNumber,
                            PixelWidth = lattice.PixelWidth,
                            PixelHeight = lattice.PixelHeight,
                            Dpi = lattice.Dpi,
                            Source = lattice.Source,
                            Words = lattice.Words
                        });
                    }

                    pageNumber++;
                }

                foreach (var candidate in await _redactionCandidates.GetAsync(document.Id, cancellationToken)
                             .ConfigureAwait(false))
                {
                    mergedCandidates.Add(CopyCandidate(candidate, candidate.PageNumber + pageOffset));
                }
            }

            foreach (var file in Directory.GetFiles(targetPagesDirectory))
                File.Delete(file);
            foreach (var file in Directory.GetFiles(stagingDirectory))
                File.Move(file, Path.Combine(targetPagesDirectory, Path.GetFileName(file)));
            Directory.Delete(stagingDirectory);

            if (File.Exists(mergedPdfPath))
                File.Delete(mergedPdfPath);
            File.Move(tempPdfPath, mergedPdfPath);
            if (!string.Equals(target.StoredPath, mergedPdfPath, StringComparison.Ordinal)
                && File.Exists(target.StoredPath))
                File.Delete(target.StoredPath);

            if (!string.IsNullOrEmpty(target.RedactedPath) && File.Exists(target.RedactedPath))
                File.Delete(target.RedactedPath);
            target.StoredPath = mergedPdfPath;
            target.PageCount = mergedPages.Count;
            target.Status = DocumentStatus.NeedsReview;
            target.ErrorMessage = null;
            target.RedactedPath = null;
            target.RedactionStatus = mergedCandidates.Count > 0
                ? RedactionStatus.PendingReview
                : RedactionStatus.None;
            target.RedactionError = null;
            await _store.SaveAsync(target, mergedPages, cancellationToken).ConfigureAwait(false);

            var latticeDirectory = _paths.DocumentLatticeDirectory(target.Id);
            if (Directory.Exists(latticeDirectory))
                Directory.Delete(latticeDirectory, recursive: true);
            foreach (var lattice in mergedLattices)
                await _latticeStore.SaveAsync(target.Id, lattice, cancellationToken).ConfigureAwait(false);
            await _redactionCandidates.SaveAsync(target.Id, mergedCandidates, cancellationToken).ConfigureAwait(false);

            var ocrDirectory = _paths.DocumentOcrDirectory(target.Id);
            if (Directory.Exists(ocrDirectory))
                Directory.Delete(ocrDirectory, recursive: true);

            foreach (var absorbed in documents.Skip(1))
                await _store.DeleteAsync(absorbed.Id, cancellationToken).ConfigureAwait(false);

            return target;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, recursive: true);
            if (File.Exists(tempPdfPath))
                File.Delete(tempPdfPath);
        }
    }

    private static RedactionCandidate CopyCandidate(RedactionCandidate candidate, int pageNumber) => new()
    {
        Id = candidate.Id,
        Source = candidate.Source,
        Label = candidate.Label,
        PreviewText = candidate.PreviewText,
        PageNumber = pageNumber,
        X = candidate.X,
        Y = candidate.Y,
        Width = candidate.Width,
        Height = candidate.Height,
        Score = candidate.Score,
        Decision = candidate.Decision
    };

    private async Task<CaptureDocument> GetDocumentOrThrowAsync(Guid documentId, CancellationToken cancellationToken) =>
        await _store.GetAsync(documentId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Document {documentId} not found.");

    /// <summary>Rebuilds an existing document's page set in place to contain exactly
    /// <paramref name="survivingOldOrderPageNumbers"/> (old page numbers, in the desired new order),
    /// renumbered 1..N. Used directly by delete/reorder, and by split for the original document's
    /// remaining first half.</summary>
    private async Task<CaptureDocument> RewriteDocumentAsync(
        CaptureDocument document,
        IReadOnlyList<DocumentPage> oldPages,
        IReadOnlyList<int> survivingOldOrderPageNumbers,
        bool dropUnmappedIndexValues,
        CancellationToken cancellationToken)
    {
        var isPdf = ImportFormats.IsPdf(document.StoredPath);
        var oldToNew = survivingOldOrderPageNumbers
            .Select((old, index) => (old, @new: index + 1))
            .ToDictionary(x => x.old, x => x.@new);

        if (isPdf)
        {
            var sourcePageNumbersInOrder = survivingOldOrderPageNumbers
                .Select(old => oldPages.First(p => p.PageNumber == old).SourcePageNumber)
                .ToList();
            await RewritePdfInPlaceAsync(document, sourcePageNumbersInOrder, cancellationToken).ConfigureAwait(false);
        }

        var newPages = RebuildPageImages(document, oldPages, survivingOldOrderPageNumbers, isPdf);

        DeleteStaleLatticeFiles(document, fromPageNumber: newPages.Count + 1, throughPageNumber: oldPages.Count);
        await _latticeBuilder.BuildDocumentAsync(document, newPages, cancellationToken).ConfigureAwait(false);

        await RemapIndexValuesAsync(document.Id, oldToNew, dropUnmappedIndexValues, cancellationToken).ConfigureAwait(false);
        await RemapRedactionCandidatesAsync(document, oldToNew, cancellationToken).ConfigureAwait(false);

        document.PageCount = newPages.Count;
        await _store.SaveAsync(document, newPages, cancellationToken).ConfigureAwait(false);
        return document;
    }

    /// <summary>Materializes the second half of a split as a brand-new document, mirroring
    /// <c>DocumentImporter.MaterializeSplitAsync</c>: new id, PDF subset (or whole-file copy for image
    /// sources) into its own <c>StoredPath</c>, page images copied into fresh sequential filenames, and
    /// lattices rebuilt from scratch. Inherits the original document's profile/batch assignment.</summary>
    private async Task<CaptureDocument> MaterializeSplitDocumentAsync(
        CaptureDocument original,
        IReadOnlyList<DocumentPage> oldPages,
        IReadOnlyList<int> oldOrderPageNumbers,
        CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_paths.DocumentPagesDirectory(id));
        var storedPath = _paths.DocumentOriginalPath(id, original.OriginalFileName);
        var isPdf = ImportFormats.IsPdf(original.StoredPath);

        if (isPdf)
        {
            var sourcePageNumbersInOrder = oldOrderPageNumbers
                .Select(old => oldPages.First(p => p.PageNumber == old).SourcePageNumber)
                .ToList();
            await _pdfSubsetWriter.WritePagesAsync(original.StoredPath, sourcePageNumbersInOrder, storedPath, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            File.Copy(original.StoredPath, storedPath, overwrite: true);
        }

        var newPages = new List<DocumentPage>(oldOrderPageNumbers.Count);
        var number = 1;
        foreach (var oldNumber in oldOrderPageNumbers)
        {
            var oldPage = oldPages.First(p => p.PageNumber == oldNumber);
            var extension = Path.GetExtension(oldPage.ImagePath);
            var imagePath = Path.Combine(_paths.DocumentPagesDirectory(id), $"{number:D4}{extension}");
            File.Copy(oldPage.ImagePath, imagePath, overwrite: true);
            newPages.Add(new DocumentPage
            {
                DocumentId = id,
                PageNumber = number,
                SourcePageNumber = isPdf ? number : oldPage.SourcePageNumber,
                ImagePath = imagePath,
                Width = oldPage.Width,
                Height = oldPage.Height,
                Dpi = oldPage.Dpi
            });
            number++;
        }

        var document = new CaptureDocument
        {
            Id = id,
            OriginalFileName = original.OriginalFileName,
            StoredPath = storedPath,
            Source = original.Source,
            ProfileId = original.ProfileId,
            BatchId = original.BatchId,
            Status = DocumentStatus.NeedsReview,
            PageCount = newPages.Count,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(document, newPages, cancellationToken).ConfigureAwait(false);

        try
        {
            await _latticeBuilder.BuildDocumentAsync(document, newPages, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await _store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup only — surface the original lattice-build failure below regardless.
            }

            throw;
        }

        // Only zone-based index values (a real box on a real page) can be unambiguously attributed to
        // this half of the split; document-level/non-zonal values (e.g. an AI-extracted field with no
        // drawn zone) aren't attached to a specific page in a meaningful way, so they stay with the
        // original document rather than being guessed at for the new one.
        var oldToNew = oldOrderPageNumbers.Select((old, index) => (old, @new: index + 1)).ToDictionary(x => x.old, x => x.@new);
        await RemapIndexValuesAsync(id, oldToNew, dropUnmapped: true, cancellationToken).ConfigureAwait(false);
        await RemapRedactionCandidatesAsync(document, oldToNew, cancellationToken).ConfigureAwait(false);

        return document;
    }

    private async Task RewritePdfInPlaceAsync(
        CaptureDocument document, IReadOnlyList<int> sourcePageNumbersInOrder, CancellationToken cancellationToken)
    {
        // IPdfSubsetWriter can't write to the same path it reads from (it keeps the source PDF open for
        // the whole call) — write to a temp file alongside it, then swap once the writer's handles are
        // released.
        var tempPath = document.StoredPath + $".tmp-{Guid.NewGuid():N}";
        await _pdfSubsetWriter.WritePagesAsync(document.StoredPath, sourcePageNumbersInOrder, tempPath, cancellationToken)
            .ConfigureAwait(false);
        File.Delete(document.StoredPath);
        File.Move(tempPath, document.StoredPath);
    }

    private List<DocumentPage> RebuildPageImages(
        CaptureDocument document, IReadOnlyList<DocumentPage> oldPages, IReadOnlyList<int> survivingOldOrderPageNumbers, bool isPdf)
    {
        var pagesDir = _paths.DocumentPagesDirectory(document.Id);
        Directory.CreateDirectory(pagesDir);

        // Stage the renumbered copies in a temp folder first — copying straight from an old numbered
        // filename to a new one can clobber a not-yet-read source file when a reorder permutation
        // contains a cycle (e.g. swapping pages 1 and 2).
        var stagingDir = Path.Combine(pagesDir, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);

        var newPages = new List<DocumentPage>(survivingOldOrderPageNumbers.Count);
        var number = 1;
        foreach (var oldNumber in survivingOldOrderPageNumbers)
        {
            var oldPage = oldPages.First(p => p.PageNumber == oldNumber);
            var extension = Path.GetExtension(oldPage.ImagePath);
            var stagedPath = Path.Combine(stagingDir, $"{number:D4}{extension}");
            File.Copy(oldPage.ImagePath, stagedPath, overwrite: true);
            newPages.Add(new DocumentPage
            {
                DocumentId = document.Id,
                PageNumber = number,
                SourcePageNumber = isPdf ? number : oldPage.SourcePageNumber,
                ImagePath = Path.Combine(pagesDir, $"{number:D4}{extension}"),
                Width = oldPage.Width,
                Height = oldPage.Height,
                Dpi = oldPage.Dpi
            });
            number++;
        }

        foreach (var file in Directory.GetFiles(pagesDir))
            File.Delete(file);
        foreach (var file in Directory.GetFiles(stagingDir))
            File.Move(file, Path.Combine(pagesDir, Path.GetFileName(file)));
        Directory.Delete(stagingDir);

        return newPages;
    }

    private void DeleteStaleLatticeFiles(CaptureDocument document, int fromPageNumber, int throughPageNumber)
    {
        var ocrDir = _paths.DocumentOcrDirectory(document.Id);
        for (var n = fromPageNumber; n <= throughPageNumber; n++)
        {
            var latticePath = _paths.DocumentLatticePath(document.Id, n);
            if (File.Exists(latticePath))
                File.Delete(latticePath);

            var ocrPath = Path.Combine(ocrDir, $"{n:D4}.png");
            if (File.Exists(ocrPath))
                File.Delete(ocrPath);
        }
    }

    /// <summary>Remaps <c>indexes.json</c> to the new page numbering. Zone-based values (a real drawn
    /// box, <c>Bounds is not null</c>) are dropped if their page was removed — the zone no longer exists.
    /// Non-zonal/document-level values referencing a removed page are, when <paramref
    /// name="dropUnmapped"/> is false, kept but reattached to page 1 rather than left pointing at a page
    /// number that no longer exists (they aren't tied to a specific visual location, so this is harmless);
    /// when true (building a fresh split-off document), they're dropped instead — see
    /// <see cref="MaterializeSplitDocumentAsync"/>.</summary>
    private async Task RemapIndexValuesAsync(
        Guid documentId, IReadOnlyDictionary<int, int> oldToNew, bool dropUnmapped, CancellationToken cancellationToken)
    {
        var values = await _indexValues.GetAsync(documentId, cancellationToken).ConfigureAwait(false);
        var remapped = new List<IndexValue>(values.Count);
        foreach (var value in values)
        {
            if (oldToNew.TryGetValue(value.PageNumber, out var newPageNumber))
            {
                value.PageNumber = newPageNumber;
                if (value.Bounds is not null)
                    value.Bounds.PageNumber = newPageNumber;
                remapped.Add(value);
            }
            else if (value.Bounds is not null || dropUnmapped)
            {
                continue; // zone-based value on a removed page, or building a fresh split document
            }
            else
            {
                value.PageNumber = 1;
                remapped.Add(value);
            }
        }

        await _indexValues.SaveAsync(documentId, remapped, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Remaps <c>redactions.json</c> to the new page numbering, dropping candidates on removed
    /// pages. <see cref="RedactionCandidate.PageNumber"/> is <c>init</c>-only on a sealed class with no
    /// copy constructor, so remapping means constructing fresh instances field-by-field. If any of the
    /// document's redaction candidates or its rendered output are now stale, resets
    /// <see cref="CaptureDocument.RedactionStatus"/>/<see cref="CaptureDocument.RedactedPath"/>.</summary>
    private async Task RemapRedactionCandidatesAsync(
        CaptureDocument document, IReadOnlyDictionary<int, int> oldToNew, CancellationToken cancellationToken)
    {
        var candidates = await _redactionCandidates.GetAsync(document.Id, cancellationToken).ConfigureAwait(false);
        var remapped = new List<RedactionCandidate>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!oldToNew.TryGetValue(candidate.PageNumber, out var newPageNumber))
                continue;

            remapped.Add(new RedactionCandidate
            {
                Id = candidate.Id,
                Source = candidate.Source,
                Label = candidate.Label,
                PreviewText = candidate.PreviewText,
                PageNumber = newPageNumber,
                X = candidate.X,
                Y = candidate.Y,
                Width = candidate.Width,
                Height = candidate.Height,
                Score = candidate.Score,
                Decision = candidate.Decision
            });
        }

        await _redactionCandidates.SaveAsync(document.Id, remapped, cancellationToken).ConfigureAwait(false);

        // Any page mutation (delete/reorder/split) invalidates a previously rendered whole-document
        // redacted.pdf, even a pure reorder with no candidates dropped — the page order baked into that
        // file is now wrong regardless of whether any individual candidate moved off the document.
        if (!string.IsNullOrEmpty(document.RedactedPath))
        {
            if (File.Exists(document.RedactedPath))
                File.Delete(document.RedactedPath);
            document.RedactedPath = null;
            document.RedactionStatus = remapped.Count > 0 ? RedactionStatus.PendingReview : RedactionStatus.None;
        }
    }
}
