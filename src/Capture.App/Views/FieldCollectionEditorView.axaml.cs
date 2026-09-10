using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Capture.App.Views;

public partial class FieldCollectionEditorView : UserControl
{
    private const double MinFieldListHeight = 80;
    private const double MaxFieldListHeight = 640;

    private RowDefinition? _fieldsRow;
    private Point? _dragStart;
    private double _dragStartHeight;

    public FieldCollectionEditorView()
    {
        InitializeComponent();
        FieldListSplitter.PointerPressed += OnSplitterPointerPressed;
        FieldListSplitter.PointerMoved += OnSplitterPointerMoved;
        FieldListSplitter.PointerReleased += OnSplitterPointerReleased;
        FieldListSplitter.PointerEntered += (_, _) => FieldListSplitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        FieldListSplitter.PointerExited += (_, _) => FieldListSplitter.Cursor = Cursor.Default;
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
