using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Capture.App.ViewModels;

namespace Capture.App.Views;

public partial class RuleSetEditorView : UserControl
{
    private const string StrategyDragFormat = "capture.ruleset.strategy";

    private ListBox? _pressStrategyList;
    private SeparationStrategyRow? _pressStrategyRow;
    private Point _pressStrategyPoint;
    private bool _strategyDragging;

    public RuleSetEditorView()
    {
        InitializeComponent();
        WireStrategyDragDrop(StrategiesList);
    }

    // Drag-to-reorder for the strategy list, mirroring the same drag-handle pattern used for document
    // types (CaptureProfileDesignerView) and index fields (FieldCollectionEditorView) — gated behind the
    // small grip glyph so a normal row click still just selects/expands a strategy.
    private void WireStrategyDragDrop(ListBox list)
    {
        DragDrop.SetAllowDrop(list, true);
        list.AddHandler(PointerPressedEvent, OnStrategyPointerPressed, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerMovedEvent, OnStrategyPointerMoved, RoutingStrategies.Tunnel, true);
        list.AddHandler(PointerReleasedEvent, OnStrategyPointerReleased, RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DragOverEvent, OnStrategyDragOver, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
        list.AddHandler(DragDrop.DropEvent, OnStrategyDrop, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
    }

    private void OnStrategyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not ListBox list || !e.GetCurrentPoint(list).Properties.IsLeftButtonPressed)
            return;
        if (!IsOnDragHandle(e.Source))
            return;

        _pressStrategyList = list;
        _pressStrategyRow = StrategyAt(list, e.Source, e.GetPosition(list));
        _pressStrategyPoint = e.GetPosition(list);
        _strategyDragging = false;
    }

    private async void OnStrategyPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_strategyDragging || _pressStrategyRow is null || _pressStrategyList is null || !ReferenceEquals(sender, _pressStrategyList))
            return;
        if (!e.GetCurrentPoint(_pressStrategyList).Properties.IsLeftButtonPressed)
            return;

        var delta = e.GetPosition(_pressStrategyList) - _pressStrategyPoint;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6)
            return;

        _strategyDragging = true;
        var data = new DataObject();
        data.Set(StrategyDragFormat, _pressStrategyRow.Id.ToString());
        try
        {
            await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        }
        finally
        {
            _pressStrategyRow = null;
            _pressStrategyList = null;
            _strategyDragging = false;
        }
    }

    private void OnStrategyPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_strategyDragging)
        {
            _pressStrategyRow = null;
            _pressStrategyList = null;
        }
    }

    private void OnStrategyDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = sender is ListBox list && CanDropStrategy(list, e) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnStrategyDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (sender is not ListBox list || DataContext is not RuleSetEditorViewModel viewModel || !TryGetDragStrategyId(e.Data, out var fromId))
            return;

        var target = StrategyAt(list, e.Source, e.GetPosition(list));
        if (target is null || target.Id == fromId)
            return;

        viewModel.ReorderStrategy(fromId, target.Id);
    }

    private bool CanDropStrategy(ListBox list, DragEventArgs e)
    {
        if (!TryGetDragStrategyId(e.Data, out var fromId))
            return false;

        var target = StrategyAt(list, e.Source, e.GetPosition(list));
        return target is not null && target.Id != fromId;
    }

    private static bool TryGetDragStrategyId(IDataObject data, out Guid id)
    {
        id = Guid.Empty;
        return data.Contains(StrategyDragFormat)
            && data.Get(StrategyDragFormat) is string text
            && Guid.TryParse(text, out id);
    }

    private static SeparationStrategyRow? StrategyAt(ListBox list, object? source, Point position)
    {
        if ((source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is SeparationStrategyRow fromSource)
            return fromSource;

        foreach (var visual in list.GetVisualsAt(position))
        {
            var item = (visual as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
            if (item?.DataContext is SeparationStrategyRow row)
                return row;
        }

        return null;
    }

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
