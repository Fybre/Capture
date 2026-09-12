using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
using Capture.Core.Diagnostics;
using Capture.Core.Import;
using Capture.Core.Indexing;
using Capture.Core.Lattice;
using Capture.Core.Models;
using Capture.Core.Paths;
using Capture.Core.Pipeline;
using Capture.Core.Profiles;
using Capture.Core.Redaction;
using Capture.Core.Scripting;
using Capture.Core.Store;
using Capture.Core.Watch;
using Capture.Export;
using Capture.Scanner;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Capture.App.ViewModels;

public partial class MainViewModel
{
    private IReadOnlyList<DocumentPage> _pages = [];
    private int _loadGeneration;

    public ObservableCollection<PageThumbnailRow> PageThumbnails { get; } = [];

    public ObservableCollection<PageThumbnailRow> SelectedPageThumbnails { get; } = [];

    public bool HasPageThumbnails => PageThumbnails.Count > 0;

    public string PageLabel => PageCount == 0 ? "—" : $"{CurrentPageNumber} / {PageCount}";

    public string PreviewMessage
    {
        get
        {
            if (SelectedDocument is null)
                return "Select a document";
            if (SelectedDocument.Document.Status == DocumentStatus.Error)
                return SelectedDocument.Document.ErrorMessage ?? "Import failed";
            if (PageCount == 0)
                return "No pages";
            return string.Empty;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(SplitDocumentAtCurrentPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _currentPageNumber = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(SplitDocumentAtCurrentPageCommand))]
    [NotifyPropertyChangedFor(nameof(PageLabel))]
    private int _pageCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPageWords))]
    private PageLattice? _currentLattice;

    /// <summary>The current page's recognized OCR/PDF-text words, for the same "Show OCR text" overlay
    /// toggle already used in the Profile Designer — lets a reviewer see exactly where extraction
    /// thinks text is, e.g. when a redaction or index highlight looks misplaced.</summary>
    public IReadOnlyList<LatticeWord> CurrentPageWords => CurrentLattice?.Words ?? [];

    [ObservableProperty]
    private bool _showOcrWords;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage()
    {
        CurrentPageNumber--;
        // Each navigation gets its own generation, not just each document load — otherwise rapid
        // clicking shares one generation and a slower earlier page load can finish after a faster
        // later one and overwrite the page the user is actually looking at.
        var generation = Interlocked.Increment(ref _loadGeneration);
        _ = ShowPageAsync(generation);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage()
    {
        CurrentPageNumber++;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _ = ShowPageAsync(generation);
    }

    /// <summary>Moves the main preview to the given page — called from the thumbnail strip's
    /// SelectionChanged handler in code-behind when exactly one thumbnail ends up selected (a plain
    /// click, as opposed to a ctrl/shift-click extending a multi-selection for bulk delete).</summary>
    public void JumpToPage(int pageNumber)
    {
        if (pageNumber == CurrentPageNumber)
            return;

        CurrentPageNumber = pageNumber;
        var generation = Interlocked.Increment(ref _loadGeneration);
        _ = ShowPageAsync(generation);
    }

    private bool CanDeleteSelectedPages() =>
        !IsBusy && SelectedPageThumbnails.Count > 0 && SelectedPageThumbnails.Count < PageThumbnails.Count;

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedPages))]
    private async Task DeleteSelectedPagesAsync()
    {
        if (SelectedDocument is not { } row)
            return;

        var pageNumbers = SelectedPageThumbnails.Select(item => item.PageNumber).ToList();
        var deletedSet = pageNumbers.ToHashSet();
        var originalOrder = _pages.Select(page => page.PageNumber).OrderBy(number => number).ToList();

        // DeletePagesAsync physically deletes these pages' own image files and drops their page-bound
        // index values/redaction candidates from disk — none of that survives the call, so anything an
        // "undo" would need to restore has to be captured here, before it runs.
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"capture-undo-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        var snapshot = new List<(int OldPageNumber, string TempImagePath, int Width, int Height, int Dpi)>();
        foreach (var page in _pages.Where(page => deletedSet.Contains(page.PageNumber)))
        {
            var tempPath = Path.Combine(tempDirectory, $"{page.PageNumber:D4}{Path.GetExtension(page.ImagePath)}");
            if (File.Exists(page.ImagePath))
                File.Copy(page.ImagePath, tempPath, overwrite: true);
            snapshot.Add((page.PageNumber, tempPath, page.Width, page.Height, page.Dpi));
        }
        var existingValues = await _indexes.GetAsync(row.Id).ConfigureAwait(true);
        // Only a zone-bound value is actually dropped by the delete (see PageManagementService.
        // RemapIndexValuesAsync) — a non-zonal value on a "deleted" page number gets reattached to page 1
        // instead of removed, so it isn't lost and doesn't need restoring here.
        var deletedIndexValues = existingValues.Where(value => value.Bounds is not null && deletedSet.Contains(value.PageNumber)).ToList();
        var existingCandidates = await _redactionCandidates.GetAsync(row.Id).ConfigureAwait(true);
        var deletedCandidates = existingCandidates.Where(candidate => deletedSet.Contains(candidate.PageNumber)).ToList();

        IsBusy = true;
        try
        {
            var updated = await _pageManagement.DeletePagesAsync(row.Id, pageNumbers).ConfigureAwait(true);
            await RefreshDocumentRowInPlaceAsync(row, updated).ConfigureAwait(true);
            RefreshDocumentGroups();
            StatusText = pageNumbers.Count == 1 ? "Deleted 1 page" : $"Deleted {pageNumbers.Count} pages";
            StatusIsError = false;
            _toasts.ShowInfo($"{StatusText} — click to undo", onClick: () => _ = UndoDeletePagesAsync(
                row, originalOrder, pageNumbers, tempDirectory, snapshot, deletedIndexValues, deletedCandidates));
            _ = CleanupUndoSnapshotAfterDelayAsync(tempDirectory, TimeSpan.FromSeconds(30));
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
            _toasts.ShowError(StatusText);
            TryDeleteDirectory(tempDirectory);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reverses DeleteSelectedPagesAsync: appends the deleted pages back (from the snapshot
    /// copies made before deletion) and reorders the result to reconstruct the exact original page
    /// arrangement, then re-attaches the zone-bound index values and redaction candidates that were on
    /// those pages. If anything else changed the document's pages between the delete and this undo (the
    /// permutation length no longer matches), the underlying reorder call fails safely with an error
    /// rather than silently producing a wrong page order.</summary>
    private async Task UndoDeletePagesAsync(
        DocumentRow row,
        IReadOnlyList<int> originalOrder,
        IReadOnlyList<int> deletedOldNumbers,
        string tempDirectory,
        IReadOnlyList<(int OldPageNumber, string TempImagePath, int Width, int Height, int Dpi)> snapshot,
        IReadOnlyList<IndexValue> deletedIndexValues,
        IReadOnlyList<RedactionCandidate> deletedCandidates)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var restoredRaster = snapshot
                .OrderBy(page => page.OldPageNumber)
                .Select(page => new RasterPage(page.OldPageNumber, page.TempImagePath, page.Width, page.Height, page.Dpi))
                .ToList();
            await _pageManagement.AppendPagesAsync(row.Id, restoredRaster).ConfigureAwait(true);

            var deletedSet = deletedOldNumbers.ToHashSet();
            var survivorOldNumbers = originalOrder.Where(number => !deletedSet.Contains(number)).ToList();
            var orderedDeleted = deletedOldNumbers.OrderBy(number => number).ToList();
            var finalOrder = originalOrder.Select(oldNumber => survivorOldNumbers.Contains(oldNumber)
                    ? survivorOldNumbers.IndexOf(oldNumber) + 1
                    : survivorOldNumbers.Count + orderedDeleted.IndexOf(oldNumber) + 1)
                .ToList();
            var restored = await _pageManagement.ReorderPagesAsync(row.Id, finalOrder).ConfigureAwait(true);

            var currentValues = await _indexes.GetAsync(row.Id).ConfigureAwait(true);
            await _indexes.SaveAsync(row.Id, currentValues.Concat(deletedIndexValues).ToList()).ConfigureAwait(true);
            var currentCandidates = await _redactionCandidates.GetAsync(row.Id).ConfigureAwait(true);
            await _redactionCandidates.SaveAsync(row.Id, currentCandidates.Concat(deletedCandidates).ToList()).ConfigureAwait(true);

            await RefreshDocumentRowInPlaceAsync(row, restored).ConfigureAwait(true);
            if (SelectedDocument == row)
                await LoadSelectedDocumentAsync(row).ConfigureAwait(true);
            RefreshDocumentGroups();
            StatusText = "Restored deleted page(s)";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't undo: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
            TryDeleteDirectory(tempDirectory);
        }
    }

    private static async Task CleanupUndoSnapshotAfterDelayAsync(string directory, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        TryDeleteDirectory(directory);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup only, matching PageManagementService's own staging-directory cleanup.
        }
    }

    private bool CanSplitAtCurrentPage() =>
        !IsBusy && SelectedDocument is not null && PageCount > 1 && CurrentPageNumber > 1;

    [RelayCommand(CanExecute = nameof(CanSplitAtCurrentPage))]
    private async Task SplitDocumentAtCurrentPageAsync()
    {
        if (SelectedDocument is not { } row)
            return;

        IsBusy = true;
        try
        {
            var (first, second) = await _pageManagement.SplitDocumentAsync(row.Id, CurrentPageNumber).ConfigureAwait(true);
            var firstIndexError = await TryReindexAfterPageSplitAsync(first).ConfigureAwait(true);
            var secondIndexError = await TryReindexAfterPageSplitAsync(second).ConfigureAwait(true);
            var secondRow = await CreateRowAsync(second).ConfigureAwait(true);
            var insertIndex = Documents.IndexOf(row) + 1;
            Documents.Insert(Math.Clamp(insertIndex, 0, Documents.Count), secondRow);
            await RefreshDocumentRowInPlaceAsync(row, first).ConfigureAwait(true);
            RefreshBatchAccents();
            RefreshDocumentGroups();
            var indexErrors = new[] { firstIndexError, secondIndexError }
                .Where(error => !string.IsNullOrWhiteSpace(error))
                .Distinct()
                .ToList();
            StatusText = indexErrors.Count == 0
                ? "Split into two documents and refreshed their indexes"
                : $"Split into two documents, but indexing needs attention: {string.Join("; ", indexErrors)}";
            StatusIsError = indexErrors.Count != 0;
            if (indexErrors.Count == 0)
            {
                _toasts.ShowInfo($"{StatusText} — click to undo", onClick: () => _ = UndoSplitAsync(row, secondRow));
            }
            else
            {
                _toasts.ShowError(StatusText);
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Reverses a split by merging the two resulting documents back together, reusing
    /// MergeDocumentsAsync exactly as the Table-mode "Merge" action does. This recombines the pages
    /// correctly, but — like any merge — it isn't a byte-perfect restore of the pre-split document: the
    /// merged result keeps the first document's own (re-extracted) index values rather than the original
    /// pre-split ones, since MergeDocumentsAsync's documented contract is "target keeps its own
    /// document-level values." Good enough to fix a wrong split point immediately; any index drift is the
    /// same kind of correction normal review already handles.</summary>
    private async Task UndoSplitAsync(DocumentRow first, DocumentRow second)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var merged = await _pageManagement.MergeDocumentsAsync([first.Id, second.Id]).ConfigureAwait(true);
            Documents.Remove(second);
            await RefreshDocumentRowInPlaceAsync(first, merged).ConfigureAwait(true);
            if (SelectedDocument == first || SelectedDocument == second)
            {
                SelectedDocuments.Clear();
                SelectedDocuments.Add(first);
                SelectedDocument = first;
            }
            RefreshBatchAccents();
            RefreshDocumentGroups();
            StatusText = "Undid split — merged back into one document";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't undo: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A split changes which physical page is first/last in both resulting documents. Merely
    /// moving saved zonal values cannot reproduce fields scoped to those positions, so run the assigned
    /// document type's normal extraction again against each new page set. Existing manual and
    /// boundary-rule values are supplied to ProfileApplicator so its established preservation rules
    /// continue to apply; batch values remain stored once at batch scope.</summary>
    private async Task<string?> TryReindexAfterPageSplitAsync(CaptureDocument document)
    {
        var profile = FindDocumentType(document.ProfileId);
        if (profile is null)
            return document.ProfileId is null ? null : "assigned document type is unavailable";

        try
        {
            var pages = (await _store.GetPagesAsync(document.Id).ConfigureAwait(true))
                .OrderBy(page => page.PageNumber)
                .ToList();
            var lattices = await LoadAllLatticesAsync(document).ConfigureAwait(true);
            var existingValues = await _indexes.GetAsync(document.Id).ConfigureAwait(true);
            IReadOnlyList<IndexValue> batchValues = [];
            var batchNumber = 1;
            var documentNumber = 1;
            if (document.BatchId is { } batchId)
            {
                batchValues = await _indexes.GetBatchAsync(batchId).ConfigureAwait(true);
                batchNumber = await _store.GetBatchNumberAsync(batchId).ConfigureAwait(true);
                documentNumber = await _store.GetDocumentNumberInBatchAsync(batchId, document.Id).ConfigureAwait(true);
            }

            var values = await _profileApplicator.ApplyAsync(
                profile,
                lattices,
                context: new DefaultValueContext
                {
                    BatchNumber = batchNumber,
                    DocumentNumber = documentNumber,
                    ScriptScope = ScriptScopeKind.Document,
                    DocumentType = profile.Name,
                    BatchValues = batchValues
                },
                pages: pages,
                existingValues: existingValues,
                document: document).ConfigureAwait(true);

            PreserveInheritedSplitValues(values, existingValues);

            await _indexes.SaveAsync(document.Id, values).ConfigureAwait(true);
            document.Status = IndexFormat.StatusFor(batchValues.Concat(values), profile.AutoReadyThreshold);
            await _store.UpdateAsync(document).ConfigureAwait(true);
            return null;
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Re-indexing split document {document.Id} failed: {ex}");
            return ex.Message;
        }
    }

    /// <summary>Re-indexing should prefer a real value found on the resulting document, but an empty
    /// extraction must not erase metadata inherited from a removed separator/header page. Explicit
    /// manual edits are also authoritative even when extraction finds something else.</summary>
    internal static void PreserveInheritedSplitValues(
        IReadOnlyList<IndexValue> refreshed,
        IReadOnlyList<IndexValue> inherited)
    {
        foreach (var value in refreshed)
        {
            var previous = inherited.FirstOrDefault(candidate => candidate.FieldId == value.FieldId);
            if (previous is null || string.IsNullOrWhiteSpace(previous.Value)
                || (!previous.IsManual && !string.IsNullOrWhiteSpace(value.Value)))
                continue;

            value.Value = previous.Value;
            value.Confidence = previous.Confidence;
            value.IsManual = previous.IsManual;
            value.PageNumber = previous.PageNumber;
            value.Bounds = previous.Bounds;
            value.ValidationError = previous.ValidationError;
        }
    }

    /// <summary>Moves a single page to sit immediately before another page's position, or to the very end
    /// when <paramref name="toPageNumber"/> is null, called from the thumbnail strip's drag-and-drop
    /// handler in code-behind — everything after the drop point shifts along by one.</summary>
    public async Task ReorderPagesAsync(int fromPageNumber, int? toPageNumber)
    {
        // Unlike DeleteSelectedPagesAsync/SplitDocumentAtCurrentPageAsync, this isn't a [RelayCommand]
        // gated on CanExecute(!IsBusy) — it's called directly from the drop handler in code-behind, so a
        // drop landing mid-operation would otherwise start a second concurrent RewriteDocumentAsync over
        // a stale _pages snapshot with nothing downstream to serialize it. The PageThumbnailStrip is also
        // now disabled (IsEnabled="{Binding !IsBusy}") while busy, so this should be unreachable via the
        // UI; the check stays as a direct guard against the underlying race regardless.
        if (IsBusy || SelectedDocument is not { } row || fromPageNumber == toPageNumber)
            return;

        var newOrder = _pages.Select(page => page.PageNumber).OrderBy(number => number).ToList();
        if (toPageNumber is { } target && !newOrder.Contains(target))
            return;
        if (!newOrder.Remove(fromPageNumber))
            return;
        var insertAt = toPageNumber is { } insertBefore ? newOrder.IndexOf(insertBefore) : newOrder.Count;
        newOrder.Insert(insertAt < 0 ? newOrder.Count : insertAt, fromPageNumber);

        // The inverse permutation of newOrder: undoOrder[i] is the page that should sit at position i+1
        // to put every page back exactly where it was before this move — reordering is lossless (no page
        // content is ever deleted), so undo is just applying this permutation, no snapshot needed.
        var undoOrder = new int[newOrder.Count];
        for (var i = 0; i < newOrder.Count; i++)
            undoOrder[newOrder[i] - 1] = i + 1;

        IsBusy = true;
        try
        {
            var updated = await _pageManagement.ReorderPagesAsync(row.Id, newOrder).ConfigureAwait(true);
            await RefreshDocumentRowInPlaceAsync(row, updated).ConfigureAwait(true);
            StatusText = "Reordered pages";
            _toasts.ShowInfo($"{StatusText} — click to undo", onClick: () => _ = UndoReorderPagesAsync(row, undoOrder));
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UndoReorderPagesAsync(DocumentRow row, IReadOnlyList<int> undoOrder)
    {
        if (IsBusy)
            return;

        IsBusy = true;
        try
        {
            var restored = await _pageManagement.ReorderPagesAsync(row.Id, undoOrder).ConfigureAwait(true);
            await RefreshDocumentRowInPlaceAsync(row, restored).ConfigureAwait(true);
            if (SelectedDocument == row)
                await LoadSelectedDocumentAsync(row).ConfigureAwait(true);
            StatusText = "Undid page reorder";
            StatusIsError = false;
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = $"Couldn't undo: {ex.Message}";
            StatusIsError = true;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanGoPrevious() => !IsBusy && CurrentPageNumber > 1;

    private bool CanGoNext() => !IsBusy && CurrentPageNumber < PageCount;

    private async Task LoadSelectedDocumentAsync(DocumentRow? row)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        _pages = [];
        PageCount = 0;
        CurrentPageNumber = 1;
        CurrentLattice = null;
        IndexHighlights = [];
        SetPageImage(null);
        PageThumbnails.Clear();
        SelectedPageThumbnails.Clear();
        OnPropertyChanged(nameof(PreviewMessage));

        if (row is null)
            return;

        try
        {
            var pages = await _store.GetPagesAsync(row.Id).ConfigureAwait(true);
            if (generation != _loadGeneration)
                return;

            _pages = pages;
            PageCount = pages.Count;
            CurrentPageNumber = pages.Count == 0 ? 1 : 1;
            foreach (var page in pages)
                PageThumbnails.Add(new PageThumbnailRow(page));
            await ShowPageAsync(generation).ConfigureAwait(true);
            _ = LoadPageThumbnailsAsync(pages, generation);
        }
        catch (Exception ex)
        {
            if (generation == _loadGeneration)
                StatusText = ex.Message;
                StatusIsError = true;
        }
        finally
        {
            OnPropertyChanged(nameof(PreviewMessage));
        }
    }

    private const int ThumbnailPixelWidth = 120;

    private async Task LoadPageThumbnailsAsync(IReadOnlyList<DocumentPage> pages, int generation)
    {
        foreach (var page in pages)
        {
            if (generation != _loadGeneration)
                return;
            if (!File.Exists(page.ImagePath))
                continue;

            Bitmap thumbnail;
            try
            {
                thumbnail = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(page.ImagePath);
                    return Bitmap.DecodeToWidth(stream, ThumbnailPixelWidth);
                }).ConfigureAwait(true);
            }
            catch (Exception)
            {
                continue; // skip an unreadable page's thumbnail rather than failing the whole strip
            }

            if (generation != _loadGeneration)
            {
                thumbnail.Dispose();
                return;
            }

            var thumbnailRow = PageThumbnails.FirstOrDefault(item => item.PageNumber == page.PageNumber);
            if (thumbnailRow is not null)
                thumbnailRow.Thumbnail = thumbnail;
            else
                thumbnail.Dispose();
        }
    }

    private async Task ShowPageAsync(int? generation = null)
    {
        generation ??= _loadGeneration;
        var page = _pages.FirstOrDefault(item => item.PageNumber == CurrentPageNumber);
        if (page is null || !File.Exists(page.ImagePath))
        {
            SetPageImage(null);
            OnPropertyChanged(nameof(PreviewMessage));
            return;
        }

        var bitmap = await Task.Run(() =>
        {
            using var stream = File.OpenRead(page.ImagePath);
            return new Bitmap(stream);
        }).ConfigureAwait(true);

        if (generation != _loadGeneration)
        {
            bitmap.Dispose();
            return;
        }

        SetPageImage(bitmap);
        await LoadLatticeAsync(page, generation.Value).ConfigureAwait(true);
        RefreshIndexHighlights();
        OnPropertyChanged(nameof(PreviewMessage));
    }

    private async Task LoadLatticeAsync(DocumentPage page, int generation)
    {
        if (SelectedDocument is null)
        {
            CurrentLattice = null;
            return;
        }

        var lattice = await _latticeStore.GetAsync(SelectedDocument.Id, page.PageNumber).ConfigureAwait(true);
        if (generation != _loadGeneration)
            return;

        if (lattice is null)
        {
            try
            {
                StatusText = $"Reading page {page.PageNumber}…";
                lattice = await _latticeBuilder.BuildPageAsync(SelectedDocument.Document, page).ConfigureAwait(true);
                await _latticeStore.SaveAsync(SelectedDocument.Id, lattice).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                if (generation == _loadGeneration)
                    StatusText = ex.Message;
                    StatusIsError = true;
                return;
            }
        }

        if (generation != _loadGeneration)
            return;

        CurrentLattice = lattice;
    }

    private void SetPageImage(Bitmap? bitmap)
    {
        var previous = PageImage;
        PageImage = bitmap;
        previous?.Dispose();
    }
}
