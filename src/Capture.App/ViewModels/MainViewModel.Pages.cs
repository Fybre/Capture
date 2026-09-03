using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Capture.App.Services;
using Capture.Core.Batches;
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
        IsBusy = true;
        try
        {
            var updated = await _pageManagement.DeletePagesAsync(row.Id, pageNumbers).ConfigureAwait(true);
            await RefreshDocumentRowInPlaceAsync(row, updated).ConfigureAwait(true);
            RefreshDocumentGroups();
            StatusText = pageNumbers.Count == 1 ? "Deleted 1 page" : $"Deleted {pageNumbers.Count} pages";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
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
            var secondRow = await CreateRowAsync(second).ConfigureAwait(true);
            var insertIndex = Documents.IndexOf(row) + 1;
            Documents.Insert(Math.Clamp(insertIndex, 0, Documents.Count), secondRow);
            await RefreshDocumentRowInPlaceAsync(row, first).ConfigureAwait(true);
            RefreshBatchAccents();
            RefreshDocumentGroups();
            StatusText = "Split into two documents";
            _toasts.ShowSuccess(StatusText);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            _toasts.ShowError(StatusText);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Moves a single page to sit at another page's position, called from the thumbnail strip's
    /// drag-and-drop handler in code-behind — everything after the drop point shifts along by one.</summary>
    public async Task ReorderPagesAsync(int fromPageNumber, int toPageNumber)
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
        if (!newOrder.Contains(toPageNumber) || !newOrder.Remove(fromPageNumber))
            return;
        var insertAt = newOrder.IndexOf(toPageNumber);
        newOrder.Insert(insertAt < 0 ? newOrder.Count : insertAt, fromPageNumber);

        IsBusy = true;
        try
        {
            var updated = await _pageManagement.ReorderPagesAsync(row.Id, newOrder).ConfigureAwait(true);
            await RefreshDocumentRowInPlaceAsync(row, updated).ConfigureAwait(true);
            StatusText = "Reordered pages";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
