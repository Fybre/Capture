using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class CaptureProfileDesignerView : UserControl
{
    private const string DocumentTypeDragFormat = "capture.designer.document-type";
    private const string ExportDragFormat = "capture.designer.export";

    private ListBox? _pressDocTypeList;
    private CaptureDesignerNode? _pressDocTypeNode;
    private Point _pressDocTypePoint;
    private bool _docTypeDragging;

    private ItemsControl? _pressExportList;
    private ExportDefinitionRow? _pressExportRow;
    private Point _pressExportPoint;
    private bool _exportDragging;

    public CaptureProfileDesignerView()
    {
        InitializeComponent();
        WireDocumentTypeDragDrop(DocumentTypeList);
        WireExportDragDrop(ExportsList);
    }

    // The "Copy to..." flyout needs both the clicked target document type AND the export row the button
    // was opened from — a combination that doesn't bind cleanly through ExportsList's shared item
    // template (MenuItem.CommandParameter can't reach back to an ancestor outside the Flyout's own visual
    // tree), so it's built directly in code instead of through a bound MenuFlyout.
    private void OnCopyExportClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.DataContext is not ExportDefinitionRow row)
            return;
        if (DataContext is not CaptureProfileDesignerViewModel viewModel)
            return;

        var choices = viewModel.OtherDocumentTypeChoices;
        if (choices.Count == 0)
            return;

        var flyout = new MenuFlyout();
        foreach (var choice in choices)
        {
            var item = new MenuItem { Header = choice.Name };
            item.Click += (_, _) => viewModel.CopyExportToDocumentType(row, choice.Id);
            flyout.Items.Add(item);
        }
        flyout.ShowAt(button);
    }

    // Drag-to-reorder for the export list, mirroring the document-type list's drag wiring above — the
    // one difference is the container type: DocumentExports renders through a plain ItemsControl, whose
    // default item containers are ContentPresenters, not ListBoxItems.
    private void WireExportDragDrop(ItemsControl list)
    {
        DragDrop.SetAllowDrop(list, true);
        list.AddHandler(PointerPressedEvent, OnExportPointerPressed, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerMovedEvent, OnExportPointerMoved, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerReleasedEvent, OnExportPointerReleased, RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DragOverEvent, OnExportDragOver, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DropEvent, OnExportDrop, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
    }

    private void OnExportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ItemsControl list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        if (!IsOnDragHandle(e.Source))
            return;

        _pressExportList = list;
        _pressExportRow = ExportAt(list, e.Source, e.GetPosition(list));
        _pressExportPoint = e.GetPosition(list);
        _exportDragging = false;
    }

    private async void OnExportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_exportDragging || _pressExportRow is null || _pressExportList is null || !ReferenceEquals(sender, _pressExportList))
            return;
        if (!e.GetCurrentPoint(_pressExportList).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(_pressExportList) - _pressExportPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        _exportDragging = true;
        var data = new DataObject();
        data.Set(ExportDragFormat, _pressExportRow.Definition.Id.ToString());
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _pressExportRow = null;
            _pressExportList = null;
            _exportDragging = false;
        }
    }

    private void OnExportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_exportDragging)
        {
            _pressExportRow = null;
            _pressExportList = null;
        }
    }

    private void OnExportDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = sender is ItemsControl list && CanDropExport(list, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnExportDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not ItemsControl list || DataContext is not CaptureProfileDesignerViewModel viewModel || !TryGetDragExportId(e.Data, out var fromId))
            return;

        var target = ExportAt(list, e.Source, e.GetPosition(list));
        if (target is null || target.Definition.Id == fromId)
            return;

        viewModel.ReorderExport(fromId, target.Definition.Id);
    }

    private bool CanDropExport(ItemsControl list, DragEventArgs e)
    {
        if (!TryGetDragExportId(e.Data, out var fromId))
            return false;

        var target = ExportAt(list, e.Source, e.GetPosition(list));
        return target is not null && target.Definition.Id != fromId;
    }

    private static bool TryGetDragExportId(IDataObject data, out Guid id)
    {
        id = Guid.Empty;
        return data.Contains(ExportDragFormat)
            && data.Get(ExportDragFormat) is string text
            && Guid.TryParse(text, out id);
    }

    private static ExportDefinitionRow? ExportAt(ItemsControl list, object? source, Point position)
    {
        if ((source as Control)?.FindAncestorOfType<ContentPresenter>(includeSelf: true)?.DataContext is ExportDefinitionRow fromSource)
            return fromSource;

        foreach (var visual in list.GetVisualsAt(position))
        {
            var item = (visual as Visual)?.FindAncestorOfType<ContentPresenter>(includeSelf: true);
            if (item?.DataContext is ExportDefinitionRow row)
                return row;
        }

        return null;
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
            if (visual is ListBoxItem or ContentPresenter)
                return false;
            visual = visual.GetVisualParent();
        }
        return false;
    }
}
