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
}
