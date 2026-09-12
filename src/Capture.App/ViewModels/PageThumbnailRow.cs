using Avalonia.Media.Imaging;
using Capture.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Capture.App.ViewModels;

/// <summary>One entry in the Preview pane's page thumbnail strip — a lightweight per-page row analogous
/// to <see cref="DocumentRow"/>, wrapping a <see cref="DocumentPage"/> with a lazily-loaded, downscaled
/// thumbnail bitmap. Selection state lives in the strip's own ListBox (bound to
/// <c>MainViewModel.SelectedPageThumbnails</c>), not on this row.</summary>
public sealed partial class PageThumbnailRow : ObservableObject
{
    public PageThumbnailRow(DocumentPage page)
    {
        Page = page;
    }

    public DocumentPage Page { get; }

    public int PageNumber => Page.PageNumber;

    [ObservableProperty]
    private Bitmap? _thumbnail;

    /// <summary>True while a page drag is hovering over this row as the drop target — drives a thin
    /// insertion-line indicator above the thumbnail in <c>MainWindow.axaml</c>'s page strip, showing where
    /// the dragged page will land (it inserts immediately before this row's page).</summary>
    [ObservableProperty]
    private bool _showDropIndicatorAbove;

    /// <summary>True only for the last row while a drag is being dropped past the end of the strip —
    /// there's no "next" row to show an above-indicator on, so moving a page to the very last position
    /// needs its own below-the-last-thumbnail indicator instead.</summary>
    [ObservableProperty]
    private bool _showDropIndicatorBelow;
}
