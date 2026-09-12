using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class FieldCollectionEditorView : UserControl
{
    private const double MinFieldListHeight = 80;
    private const double MaxFieldListHeight = 640;
    private const string FieldDragFormat = "capture.field-editor.field";

    private RowDefinition? _fieldsRow;
    private Point? _dragStart;
    private double _dragStartHeight;

    private ListBox? _pressFieldList;
    private FieldRow? _pressFieldNode;
    private Point _pressFieldPoint;
    private bool _fieldDragging;

    public FieldCollectionEditorView()
    {
        InitializeComponent();
        FieldListSplitter.PointerPressed += OnSplitterPointerPressed;
        FieldListSplitter.PointerMoved += OnSplitterPointerMoved;
        FieldListSplitter.PointerReleased += OnSplitterPointerReleased;
        FieldListSplitter.PointerEntered += (_, _) => FieldListSplitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        FieldListSplitter.PointerExited += (_, _) => FieldListSplitter.Cursor = Cursor.Default;
        WireFieldDragDrop(FieldsList);
    }

    // Drag-to-reorder for the field list, mirroring MainWindow's WirePageDragDrop and
    // CaptureProfileDesignerView's document-type drag wiring. Gated behind the small drag-handle glyph
    // (Classes="drag-handle") so a normal click still just selects/expands a field row.
    private void WireFieldDragDrop(ListBox list)
    {
        DragDrop.SetAllowDrop(list, true);
        list.AddHandler(PointerPressedEvent, OnFieldPointerPressed, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerMovedEvent, OnFieldPointerMoved, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerReleasedEvent, OnFieldPointerReleased, RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DragOverEvent, OnFieldDragOver, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DropEvent, OnFieldDrop, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
    }

    private void OnFieldPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        if (!IsOnDragHandle(e.Source))
            return;

        _pressFieldList = list;
        _pressFieldNode = FieldAt(list, e.Source, e.GetPosition(list));
        _pressFieldPoint = e.GetPosition(list);
        _fieldDragging = false;
    }

    private async void OnFieldPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_fieldDragging || _pressFieldNode is null || _pressFieldList is null || !ReferenceEquals(sender, _pressFieldList))
            return;
        if (!e.GetCurrentPoint(_pressFieldList).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(_pressFieldList) - _pressFieldPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        _fieldDragging = true;
        var data = new DataObject();
        data.Set(FieldDragFormat, _pressFieldNode.Field.Id.ToString());
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _pressFieldNode = null;
            _pressFieldList = null;
            _fieldDragging = false;
        }
    }

    private void OnFieldPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_fieldDragging)
        {
            _pressFieldNode = null;
            _pressFieldList = null;
        }
    }

    private void OnFieldDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = sender is ListBox list && CanDropField(list, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFieldDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not ListBox list || DataContext is not FieldCollectionEditorViewModel viewModel || !TryGetDragFieldId(e.Data, out var fromId))
            return;

        var target = FieldAt(list, e.Source, e.GetPosition(list));
        if (target is null || target.Field.Id == fromId)
            return;

        viewModel.ReorderField(fromId, target.Field.Id);
    }

    private bool CanDropField(ListBox list, DragEventArgs e)
    {
        if (!TryGetDragFieldId(e.Data, out var fromId))
            return false;

        var target = FieldAt(list, e.Source, e.GetPosition(list));
        return target is not null && target.Field.Id != fromId;
    }

    private static bool TryGetDragFieldId(IDataObject data, out Guid id)
    {
        id = Guid.Empty;
        return data.Contains(FieldDragFormat)
            && data.Get(FieldDragFormat) is string text
            && Guid.TryParse(text, out id);
    }

    private static FieldRow? FieldAt(ListBox list, object? source, Point position)
    {
        if ((source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is FieldRow fromSource)
            return fromSource;

        foreach (var visual in list.GetVisualsAt(position))
        {
            var item = (visual as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (item?.DataContext is FieldRow row)
                return row;
        }

        return null;
    }

    // Only the small grip glyph (Classes="drag-handle") should start a drag — clicking anywhere else on
    // the row must keep selecting/expanding it. Walks up from the pointer's exact source to the row
    // boundary, stopping there so a handle in an unrelated ancestor can't match.
    private static bool IsOnDragHandle(object? source)
    {
        var visual = source as Visual;
        while (visual is not null)
        {
            if (visual is Control control && control.Classes.Contains("drag-handle"))
                return true;
            if (visual is ListBoxItem)
                return false;
            visual = visual.GetVisualParent();
        }
        return false;
    }

    private RowDefinition FieldsRow => _fieldsRow ??= ((Grid)FieldsList.Parent!).RowDefinitions[1];

    private void OnSplitterPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragStart = e.GetPosition(this);
        _dragStartHeight = FieldsRow.Height.Value;
        e.Pointer.Capture(FieldListSplitter);
    }

    private void OnSplitterPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start)
            return;
        var delta = e.GetPosition(this).Y - start.Y;
        FieldsRow.Height = new GridLength(
            Math.Clamp(_dragStartHeight + delta, MinFieldListHeight, MaxFieldListHeight));
    }

    private void OnSplitterPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragStart = null;
        e.Pointer.Capture(null);
    }
}
