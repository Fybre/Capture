using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class CaptureProfileDesignerView : UserControl
{
    private const string DocumentTypeDragFormat = "capture.designer.document-type";

    private ListBox? _pressDocTypeList;
    private CaptureDesignerNode? _pressDocTypeNode;
    private Point _pressDocTypePoint;
    private bool _docTypeDragging;

    public CaptureProfileDesignerView()
    {
        InitializeComponent();
        WireDocumentTypeDragDrop(DocumentTypeList);
    }

    // The nav column is three ListBoxes (Capture Profile, Document Types, Test Capture) all displaying
    // one shared SelectedNode. A two-way SelectedItem binding on all three fights itself: selecting a
    // document type sets a value that isn't present in the other two lists' ItemsSource, each of those
    // resets its own SelectedItem to null for a value it doesn't contain, and being two-way, that reset
    // writes straight back into SelectedNode — undoing the very selection that was just made. Binding
    // SelectedItem one-way (VM to view only, for highlighting) and writing the other direction here
    // explicitly avoids that feedback loop entirely.
    private void OnNavListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is not CaptureProfileDesignerViewModel viewModel)
            return;
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is CaptureDesignerNode node)
            viewModel.SelectedNode = node;
    }

    // Drag-to-reorder for the document type list, mirroring MainWindow's WirePageDragDrop press/move/
    // release + DragOver/Drop shape. Unlike the page strip, dragging here must be gated behind the small
    // drag-handle glyph (Classes="drag-handle") rather than "press anywhere on the row" — the row itself
    // is also a click target for selecting the document type, so an ungated drag would fight that.
    private void WireDocumentTypeDragDrop(ListBox list)
    {
        DragDrop.SetAllowDrop(list, true);
        list.AddHandler(PointerPressedEvent, OnDocTypePointerPressed, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerMovedEvent, OnDocTypePointerMoved, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerReleasedEvent, OnDocTypePointerReleased, RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DragOverEvent, OnDocTypeDragOver, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DropEvent, OnDocTypeDrop, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
    }

    private void OnDocTypePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        if (!IsOnDragHandle(e.Source))
            return;

        _pressDocTypeList = list;
        _pressDocTypeNode = NodeAt(list, e.Source, e.GetPosition(list));
        _pressDocTypePoint = e.GetPosition(list);
        _docTypeDragging = false;
    }

    private async void OnDocTypePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_docTypeDragging || _pressDocTypeNode?.DocumentType is null || _pressDocTypeList is null || !ReferenceEquals(sender, _pressDocTypeList))
            return;
        if (!e.GetCurrentPoint(_pressDocTypeList).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(_pressDocTypeList) - _pressDocTypePoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        _docTypeDragging = true;
        var data = new DataObject();
        data.Set(DocumentTypeDragFormat, _pressDocTypeNode.DocumentType.Id.ToString());
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _pressDocTypeNode = null;
            _pressDocTypeList = null;
            _docTypeDragging = false;
        }
    }

    private void OnDocTypePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_docTypeDragging)
        {
            _pressDocTypeNode = null;
            _pressDocTypeList = null;
        }
    }

    private void OnDocTypeDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = sender is ListBox list && CanDropDocType(list, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDocTypeDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not ListBox list || DataContext is not CaptureProfileDesignerViewModel viewModel || !TryGetDragTypeId(e.Data, out var fromId))
            return;

        var target = NodeAt(list, e.Source, e.GetPosition(list));
        if (target?.DocumentType is not { } targetType || targetType.Id == fromId)
            return;

        viewModel.ReorderDocumentType(fromId, targetType.Id);
    }

    private bool CanDropDocType(ListBox list, DragEventArgs e)
    {
        if (!TryGetDragTypeId(e.Data, out var fromId))
            return false;

        var target = NodeAt(list, e.Source, e.GetPosition(list));
        return target?.DocumentType is { } targetType && targetType.Id != fromId;
    }

    private static bool TryGetDragTypeId(IDataObject data, out Guid id)
    {
        id = Guid.Empty;
        return data.Contains(DocumentTypeDragFormat)
            && data.Get(DocumentTypeDragFormat) is string text
            && Guid.TryParse(text, out id);
    }

    private static CaptureDesignerNode? NodeAt(ListBox list, object? source, Point position)
    {
        if ((source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is CaptureDesignerNode fromSource)
            return fromSource;

        foreach (var visual in list.GetVisualsAt(position))
        {
            var item = (visual as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (item?.DataContext is CaptureDesignerNode node)
                return node;
        }

        return null;
    }

    // Only the small grip glyph (Classes="drag-handle") should start a drag — clicking anywhere else on
    // the row must keep selecting it, matching normal ListBox behavior. Walks up from the pointer's exact
    // source to the row boundary, stopping there so a handle in an unrelated ancestor can't match.
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
}
