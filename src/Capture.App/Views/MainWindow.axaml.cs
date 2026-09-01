using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Capture.App.Converters;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class MainWindow : Window
{
    private const string DocumentDragFormat = "capture.document";
    private const string PageDragFormat = "capture.page";

    // Rail, File, Pages, Status, Issues — the columns each Table-mode group DataGrid declares in XAML,
    // before per-profile index-field columns are appended in code-behind.
    private const int TableModeStaticColumnCount = 5;

    private readonly List<DataGrid> _groupGrids = [];
    private DataGrid? _pressGrid;
    private DocumentRow? _pressRow;
    private Point _pressPoint;
    private bool _dragging;

    private ListBox? _pressPageStrip;
    private PageThumbnailRow? _pressPageThumbnail;
    private Point _pressPagePoint;
    private bool _pageDragging;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        WireDragDrop(InboxGrid);
        WirePageDragDrop(PageThumbnailStrip);
        WireFileDrop();
    }

    // Lets a user drag file(s)/folder(s) from Finder/Explorer anywhere onto the window to import them
    // — functionally the same as the Import Files toolbar button. Uses the OS-level DataFormats.Files
    // format, distinct from DocumentDragFormat/PageDragFormat above (in-app row/page reordering via a
    // custom format string), so the two coexist without conflict.
    private void WireFileDrop()
    {
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnWindowDragOver);
        AddHandler(DragDrop.DropEvent, OnWindowDrop);
    }

    private void OnWindowDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnWindowDrop(object? sender, DragEventArgs e) => await TryImportDroppedFilesAsync(e);

    // Shared by the window-level drop target and OnGridDrop below — the Inbox grid (and every Table-
    // mode group grid, via the same WireDragDrop wiring) marks DragOver/Drop events Handled
    // unconditionally for its own in-app row-reorder format, which stops the window-level handler from
    // ever seeing a Files-format drop over those areas. Checking Files here first, before falling back
    // to the row-reorder logic, is what lets dropping files directly onto the Inbox work too.
    private async Task<bool> TryImportDroppedFilesAsync(DragEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || !e.Data.Contains(DataFormats.Files))
            return false;

        var paths = (e.Data.GetFiles() ?? [])
            .Select(item => item.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();

        if (paths.Count > 0)
            await viewModel.ImportDroppedPathsAsync(paths);
        return true;
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        viewModel.AttachHost(this);
        await viewModel.InitializeAsync();
    }

    // Shared drag-to-move-batch wiring for the Inbox grid (Preview mode) and every
    // per-profile group grid in Table mode — dropping a document (or the whole current
    // multi-selection) onto another row moves it into that row's batch.
    private void WireDragDrop(DataGrid grid)
    {
        grid.LoadingRow += (_, args) => DragDrop.SetAllowDrop(args.Row, true);
        grid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel, true);
        grid.AddHandler(PointerMovedEvent, OnGridPointerMoved, RoutingStrategies.Tunnel, true);
        grid.AddHandler(PointerReleasedEvent, OnGridPointerReleased, RoutingStrategies.Tunnel, true);
        grid.AddHandler(DragDrop.DragOverEvent, OnGridDragOver, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
        grid.AddHandler(DragDrop.DropEvent, OnGridDrop, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed)
            return;

        // Presses on the grid's own scrollbars (thumb, track, arrow buttons) must reach the
        // scrollbar untouched — don't start tracking a potential document drag for them.
        if (IsOnScrollBar(e.Source))
            return;

        _pressGrid = grid;
        _pressRow = RowAt(grid, e.Source, e.GetPosition(grid));
        _pressPoint = e.GetPosition(grid);
        _dragging = false;

        // Each Table-mode section is a separate DataGrid. A plain click starts a fresh selection,
        // while Ctrl/Command/Shift-click extends the selection across section boundaries just as it
        // does within one grid.
        if (_pressRow is not null
            && _groupGrids.Contains(grid)
            && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) == 0)
        {
            foreach (var other in _groupGrids)
            {
                if (other != grid)
                    other.SelectedItems.Clear();
            }
        }

        // A left-click that lands on neither a row nor a column header is empty space below/around
        // the rows — Avalonia's DataGrid has no built-in "click empty space to deselect" behavior,
        // so without this a selection (the single Preview-mode document, or the whole Table-mode
        // multi-selection) could never be cleared once made.
        if (_pressRow is null && !IsOnColumnHeader(e.Source) && DataContext is MainViewModel viewModel)
        {
            // DataGridSelectedItemsCollection.Clear() throws in Single selection mode; the current
            // document grids are Extended, but keep this safe for any future single-select reuse.
            if (grid.SelectionMode == DataGridSelectionMode.Single)
                grid.SelectedItem = null;
            else
                grid.SelectedItems.Clear();
            viewModel.SelectedDocument = null;
            viewModel.SelectedDocuments.Clear();
        }
    }

    private static bool IsOnScrollBar(object? source) =>
        (source as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;

    private static bool IsOnColumnHeader(object? source) =>
        (source as Visual)?.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true) is not null;

    // Table mode's group DataGrids each auto-size to their own rows (no filler space inside their own
    // bounds), so a click in the gaps between group cards or below the last one never reaches any
    // DataGrid at all — OnGridPointerPressed's own empty-space handling can't see it. This handler,
    // wired to the ScrollViewer that hosts every group card, catches exactly that remaining case.
    private void OnGroupsAreaPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel || sender is not Visual root)
            return;
        if (!e.GetCurrentPoint(root).Properties.IsLeftButtonPressed)
            return;
        if (IsOnScrollBar(e.Source) || IsOnColumnHeader(e.Source))
            return;
        // A click that did land inside some group's DataGrid (on a row or its own internal empty
        // space) is that grid's own concern — OnGridPointerPressed already handles both cases.
        if ((e.Source as Visual)?.FindAncestorOfType<DataGrid>(includeSelf: true) is not null)
            return;

        foreach (var grid in _groupGrids)
            grid.SelectedItems.Clear();
        viewModel.SelectedDocument = null;
        viewModel.SelectedDocuments.Clear();
    }

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging || _pressRow is null || _pressGrid is null || !ReferenceEquals(sender, _pressGrid))
            return;
        if (!e.GetCurrentPoint(_pressGrid).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(_pressGrid) - _pressPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        _dragging = true;
        var ids = DragSourceIds(_pressRow);
        var data = new DataObject();
        data.Set(DocumentDragFormat, string.Join(",", ids.Select(id => id.ToString("D"))));
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _pressRow = null;
            _pressGrid = null;
            _dragging = false;
        }
    }

    private void OnGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            _pressRow = null;
            _pressGrid = null;
        }
    }

    private void OnGridDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }
        e.DragEffects = sender is DataGrid grid && CanDrop(grid, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnGridDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (await TryImportDroppedFilesAsync(e))
            return;
        if (sender is not DataGrid grid || DataContext is not MainViewModel viewModel || !TryGetDragIds(e.Data, out var ids))
            return;

        var target = RowAt(grid, e.Source, e.GetPosition(grid));
        if (target?.Document.BatchId is not { } batchId)
            return;

        foreach (var id in ids)
        {
            if (id == target.Id)
                continue;
            await viewModel.MoveDocumentToBatchAsync(id, batchId);
        }
    }

    // Dragging a row that's part of the current multi-selection carries the whole
    // selection; dragging any other row carries just that one document.
    private IReadOnlyList<Guid> DragSourceIds(DocumentRow pressed)
    {
        if (DataContext is MainViewModel viewModel
            && viewModel.SelectedDocuments.Count > 1
            && viewModel.SelectedDocuments.Contains(pressed))
            return viewModel.SelectedDocuments.Select(row => row.Id).ToList();
        return [pressed.Id];
    }

    private bool CanDrop(DataGrid grid, DragEventArgs e)
    {
        if (!TryGetDragIds(e.Data, out var ids) || DataContext is not MainViewModel viewModel)
            return false;

        var target = RowAt(grid, e.Source, e.GetPosition(grid));
        if (target is null || ids.Contains(target.Id))
            return false;

        return ids.Any(id =>
        {
            var source = viewModel.Documents.FirstOrDefault(item => item.Id == id);
            return source is not null && source.Document.BatchId != target.Document.BatchId;
        });
    }

    private static bool TryGetDragIds(IDataObject data, out IReadOnlyList<Guid> ids)
    {
        ids = [];
        if (!data.Contains(DocumentDragFormat) || data.Get(DocumentDragFormat) is not string text)
            return false;

        var parsed = new List<Guid>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (Guid.TryParse(part, out var id))
                parsed.Add(id);
        }

        ids = parsed;
        return parsed.Count > 0;
    }

    private static DocumentRow? RowAt(DataGrid grid, object? source, Point position)
    {
        if ((source as Control)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is DocumentRow fromSource)
            return fromSource;

        foreach (var visual in grid.GetVisualsAt(position))
        {
            var row = (visual as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true);
            if (row?.DataContext is DocumentRow document)
                return document;
        }

        return null;
    }

    // Jumps the main preview to whatever single thumbnail the user just plain-clicked. Ctrl/shift-click
    // extending the ListBox's own multi-selection (for a bulk delete) leaves more than one item selected,
    // in which case the preview deliberately stays put rather than following the most recent click.
    private void OnPageThumbnailSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox strip || DataContext is not MainViewModel viewModel)
            return;
        if (strip.SelectedItems is { Count: 1 } selected && selected[0] is PageThumbnailRow only)
            viewModel.JumpToPage(only.PageNumber);
    }

    private void OnInboxSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid
            || DataContext is not MainViewModel viewModel
            || !viewModel.IsPreviewMode)
            return;

        viewModel.SelectedDocuments.Clear();
        foreach (var item in grid.SelectedItems)
        {
            if (item is DocumentRow row)
                viewModel.SelectedDocuments.Add(row);
        }

        viewModel.SelectedDocument = grid.SelectedItems.Count == 1
            ? grid.SelectedItems[0] as DocumentRow
            : grid.SelectedItems.Count == 0
                ? null
                : grid.SelectedItem as DocumentRow;
    }

    // Right-click (or the keyboard menu key) on a thumbnail that isn't part of the current multi-selection
    // replaces the selection with just that page before its context menu opens — matching the usual
    // file-manager convention — so "Delete page(s)"/"Split here" always act on the page under the pointer
    // rather than some earlier, unrelated selection. Right-clicking within an existing multi-selection
    // leaves it alone, so "Delete page(s)" still deletes the whole selection.
    private void OnPageThumbnailContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Border border || border.DataContext is not PageThumbnailRow row || DataContext is not MainViewModel viewModel)
            return;

        if (!viewModel.SelectedPageThumbnails.Contains(row))
        {
            viewModel.SelectedPageThumbnails.Clear();
            viewModel.SelectedPageThumbnails.Add(row);
        }

        viewModel.JumpToPage(row.PageNumber);
    }

    // Drag-to-reorder wiring for the page thumbnail strip, mirroring WireDragDrop's press/move/release +
    // DragOver/Drop shape for the Inbox grid above — dropping one thumbnail onto another moves it to sit
    // at that page's position.
    private void WirePageDragDrop(ListBox strip)
    {
        DragDrop.SetAllowDrop(strip, true);
        strip.AddHandler(PointerPressedEvent, OnPageStripPointerPressed, RoutingStrategies.Tunnel, true);
        strip.AddHandler(PointerMovedEvent, OnPageStripPointerMoved, RoutingStrategies.Tunnel, true);
        strip.AddHandler(PointerReleasedEvent, OnPageStripPointerReleased, RoutingStrategies.Tunnel, true);
        strip.AddHandler(DragDrop.DragOverEvent, OnPageStripDragOver, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
        strip.AddHandler(DragDrop.DropEvent, OnPageStripDrop, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
    }

    private void OnPageStripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox strip || !e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed)
            return;
        if (IsOnScrollBar(e.Source))
            return;

        _pressPageStrip = strip;
        _pressPageThumbnail = ThumbnailAt(strip, e.Source, e.GetPosition(strip));
        _pressPagePoint = e.GetPosition(strip);
        _pageDragging = false;
    }

    private async void OnPageStripPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pageDragging || _pressPageThumbnail is null || _pressPageStrip is null || !ReferenceEquals(sender, _pressPageStrip))
            return;
        if (!e.GetCurrentPoint(_pressPageStrip).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(_pressPageStrip) - _pressPagePoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        _pageDragging = true;
        var data = new DataObject();
        data.Set(PageDragFormat, _pressPageThumbnail.PageNumber.ToString());
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _pressPageThumbnail = null;
            _pressPageStrip = null;
            _pageDragging = false;
        }
    }

    private void OnPageStripPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_pageDragging)
        {
            _pressPageThumbnail = null;
            _pressPageStrip = null;
        }
    }

    private void OnPageStripDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = sender is ListBox strip && CanDropPage(strip, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnPageStripDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not ListBox strip || DataContext is not MainViewModel viewModel || !TryGetDragPageNumber(e.Data, out var fromPageNumber))
            return;

        var target = ThumbnailAt(strip, e.Source, e.GetPosition(strip));
        if (target is null || target.PageNumber == fromPageNumber)
            return;

        await viewModel.ReorderPagesAsync(fromPageNumber, target.PageNumber);
    }

    private bool CanDropPage(ListBox strip, DragEventArgs e)
    {
        if (!TryGetDragPageNumber(e.Data, out var fromPageNumber))
            return false;

        var target = ThumbnailAt(strip, e.Source, e.GetPosition(strip));
        return target is not null && target.PageNumber != fromPageNumber;
    }

    private static bool TryGetDragPageNumber(IDataObject data, out int pageNumber)
    {
        pageNumber = 0;
        return data.Contains(PageDragFormat)
            && data.Get(PageDragFormat) is string text
            && int.TryParse(text, out pageNumber);
    }

    private static PageThumbnailRow? ThumbnailAt(ListBox strip, object? source, Point position)
    {
        if ((source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is PageThumbnailRow fromSource)
            return fromSource;

        foreach (var visual in strip.GetVisualsAt(position))
        {
            var item = (visual as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (item?.DataContext is PageThumbnailRow row)
                return row;
        }

        return null;
    }

    private void OnGroupTableLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.DataContext is not DocumentGroupViewModel group)
            return;

        if (!_groupGrids.Contains(grid))
        {
            _groupGrids.Add(grid);
            grid.Unloaded += OnGroupTableUnloaded;
            grid.DoubleTapped += OnGroupTableDoubleTapped;
            WireDragDrop(grid);
        }

        // Loaded can fire more than once (e.g. re-parenting during scroll virtualization) —
        // don't duplicate the dynamic columns we already appended.
        if (grid.Columns.Count > TableModeStaticColumnCount)
            return;

        var monoFont = ResolveMonoFont();
        foreach (var fieldName in group.BatchFieldNames)
            grid.Columns.Add(BuildIndexColumn(fieldName, monoFont, isBatchField: true));

        if (group.HasBatchFields && group.DocumentFieldNames.Count > 0)
        {
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = string.Empty,
                Width = new DataGridLength(1),
                CanUserResize = false,
                CanUserSort = false,
                CellTemplate = new FuncDataTemplate<DocumentRow>((_, _) =>
                    new Border { Background = IndexCellLookup.ResolveBrush("BorderBrush1") })
            });
        }

        foreach (var fieldName in group.DocumentFieldNames)
            grid.Columns.Add(BuildIndexColumn(fieldName, monoFont, isBatchField: false));
    }

    private static DataGridTemplateColumn BuildIndexColumn(string fieldName, FontFamily monoFont, bool isBatchField)
    {
        return new DataGridTemplateColumn
        {
            Header = new TextBlock
            {
                Text = fieldName,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
                Foreground = IndexCellLookup.ResolveBrush(isBatchField ? "AccentBrush1" : "MutedBrush")
            },
            Width = new DataGridLength(150),
            CellTemplate = new FuncDataTemplate<DocumentRow>((_, _) =>
            {
                var text = new TextBlock
                {
                    FontFamily = monoFont,
                    FontSize = 12,
                    Margin = new Thickness(10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                text.Bind(TextBlock.TextProperty, new Binding
                {
                    Converter = IndexCellTextConverter.Instance,
                    ConverterParameter = fieldName
                });
                text.Bind(TextBlock.ForegroundProperty, new Binding
                {
                    Converter = IndexCellForegroundConverter.Instance,
                    ConverterParameter = fieldName
                });
                return text;
            })
        };
    }

    private void OnGroupTableUnloaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid)
            return;
        grid.Unloaded -= OnGroupTableUnloaded;
        grid.DoubleTapped -= OnGroupTableDoubleTapped;
        _groupGrids.Remove(grid);
    }

    private void OnGroupTableSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid
            || DataContext is not MainViewModel viewModel
            || !viewModel.IsTableMode)
            return;

        // Table mode presents one DataGrid per profile group. Rebuild the shared selection from every
        // grid so Ctrl/Command/Shift-click can extend it across section boundaries.
        viewModel.SelectedDocuments.Clear();
        foreach (var groupGrid in _groupGrids)
        {
            foreach (var item in groupGrid.SelectedItems)
            {
                if (item is DocumentRow row)
                    viewModel.SelectedDocuments.Add(row);
            }
        }

        // Always assign, including null — grid.SelectedItem going empty (e.g. an empty-space click
        // clearing the selection) must clear SelectedDocument too, not leave it pointing at whatever
        // was selected before.
        viewModel.SelectedDocument = grid.SelectedItem as DocumentRow;
    }

    private void OnSelectAllGroupClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DocumentGroupViewModel group })
            return;

        var grid = _groupGrids.FirstOrDefault(item => ReferenceEquals(item.DataContext, group));
        if (grid is null)
            return;

        // Selection is owned by the individual DataGrids. Populate the matching grid so both the
        // row highlights and the shared selection used by bulk actions stay in sync.
        foreach (var other in _groupGrids)
        {
            if (other != grid)
                other.SelectedItems.Clear();
        }

        grid.SelectedItems.Clear();
        foreach (var document in group.Documents)
            grid.SelectedItems.Add(document);
    }

    private void OnGroupTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not DataGrid || DataContext is not MainViewModel viewModel)
            return;
        if (viewModel.SelectedDocument is { } row)
            viewModel.OpenInPreviewCommand.Execute(row);
    }

    private const double ZoomButtonStep = 1.25;

    private void OnZoomInClick(object? sender, RoutedEventArgs e) =>
        PreviewImage.Zoom *= ZoomButtonStep;

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) =>
        PreviewImage.Zoom /= ZoomButtonStep;

    private void OnZoomResetClick(object? sender, RoutedEventArgs e) =>
        PreviewImage.Zoom = Capture.App.Controls.PagePreview.MinZoom;

    private static FontFamily ResolveMonoFont()
    {
        if (Application.Current?.TryFindResource("PlexMonoFontFamily", out var resource) == true
            && resource is FontFamily font)
            return font;
        return FontFamily.Default;
    }
}
