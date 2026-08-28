using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Capture.App.Converters;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class MainWindow : Window
{
    private const string DocumentDragFormat = "capture.document";

    // Rail, File, Pages, Status, Issues — the columns each Table-mode group DataGrid declares in XAML,
    // before per-profile index-field columns are appended in code-behind.
    private const int TableModeStaticColumnCount = 5;

    private readonly List<DataGrid> _groupGrids = [];
    private DataGrid? _pressGrid;
    private DocumentRow? _pressRow;
    private Point _pressPoint;
    private bool _dragging;

    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        WireDragDrop(InboxGrid);
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
    }

    private static bool IsOnScrollBar(object? source) =>
        (source as Visual)?.FindAncestorOfType<ScrollBar>(includeSelf: true) is not null;

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
        e.DragEffects = sender is DataGrid grid && CanDrop(grid, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnGridDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
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
        if (sender is not DataGrid grid || DataContext is not MainViewModel viewModel)
            return;

        // Table mode presents one DataGrid per profile group; treat selection as a single
        // set spanning the whole view, so selecting in one group clears any other.
        foreach (var other in _groupGrids)
        {
            if (other != grid)
                other.SelectedItems.Clear();
        }

        viewModel.SelectedDocuments.Clear();
        foreach (var item in grid.SelectedItems)
        {
            if (item is DocumentRow row)
                viewModel.SelectedDocuments.Add(row);
        }

        if (grid.SelectedItem is DocumentRow current)
            viewModel.SelectedDocument = current;
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
