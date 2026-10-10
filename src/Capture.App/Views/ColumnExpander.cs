using Avalonia.Controls;

namespace Capture.App.Views;

/// <summary>Widens a side panel's grid column to double its width (as far as the grid's other columns
/// allow) and restores it again — the one-click alternative to dragging the panel's divider.</summary>
internal sealed class ColumnExpander
{
    private readonly Grid _grid;
    private readonly ColumnDefinition _column;
    private double? _restoreWidth;

    public ColumnExpander(Grid grid, int columnIndex)
    {
        _grid = grid;
        _column = grid.ColumnDefinitions[columnIndex];
    }

    public bool IsExpanded => _restoreWidth is not null;

    /// <summary>Expands or restores the column; returns whether it's now expanded.</summary>
    public bool Toggle()
    {
        if (_restoreWidth is { } restore)
        {
            _column.Width = new GridLength(restore);
            _restoreWidth = null;
            return false;
        }

        var current = _column.ActualWidth;
        // Everything else keeps its current width, except the flexible (*) column, which may shrink
        // to its minimum.
        var others = _grid.ColumnDefinitions
            .Where(column => !ReferenceEquals(column, _column))
            .Sum(column => column.Width.IsStar ? column.MinWidth : column.ActualWidth);
        var target = Math.Min(current * 2, _grid.Bounds.Width - others);
        if (target <= current + 1)
            return false;

        _restoreWidth = current;
        _column.MaxWidth = Math.Max(_column.MaxWidth, target);
        _column.Width = new GridLength(target);
        return true;
    }

    /// <summary>Points the toggle button's arrow and tooltip at what a click will do next.</summary>
    public static void UpdateButton(Button button, bool expanded)
    {
        button.Content = expanded ? "›" : "‹";
        ToolTip.SetTip(button, expanded ? "Restore this panel's width" : "Widen this panel");
    }
}
