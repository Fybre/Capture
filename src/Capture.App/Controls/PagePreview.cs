using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Capture.Core.Lattice;
using Capture.Core.Profiles;

namespace Capture.App.Controls;

public sealed class PagePreview : Control
{
    public static readonly StyledProperty<Bitmap?> PageImageProperty =
        AvaloniaProperty.Register<PagePreview, Bitmap?>(nameof(PageImage));

    public static readonly StyledProperty<IReadOnlyList<IndexHighlight>?> HighlightsProperty =
        AvaloniaProperty.Register<PagePreview, IReadOnlyList<IndexHighlight>?>(nameof(Highlights));

    public static readonly StyledProperty<IReadOnlyList<LatticeWord>?> OcrWordsProperty =
        AvaloniaProperty.Register<PagePreview, IReadOnlyList<LatticeWord>?>(nameof(OcrWords));

    public static readonly StyledProperty<bool> ShowOcrWordsProperty =
        AvaloniaProperty.Register<PagePreview, bool>(nameof(ShowOcrWords));

    public static readonly StyledProperty<bool> AllowDrawProperty =
        AvaloniaProperty.Register<PagePreview, bool>(nameof(AllowDraw));

    public static readonly StyledProperty<ICommand?> ZoneDrawnCommandProperty =
        AvaloniaProperty.Register<PagePreview, ICommand?>(nameof(ZoneDrawnCommand));

    public static readonly StyledProperty<ICommand?> ZoneChangedCommandProperty =
        AvaloniaProperty.Register<PagePreview, ICommand?>(nameof(ZoneChangedCommand));

    public static readonly StyledProperty<ICommand?> HighlightClickedCommandProperty =
        AvaloniaProperty.Register<PagePreview, ICommand?>(nameof(HighlightClickedCommand));

    public const double MinZoom = 1.0;
    public const double MaxZoom = 6.0;

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<PagePreview, double>(nameof(Zoom), 1.0, coerce: CoerceZoom);

    private static double CoerceZoom(AvaloniaObject sender, double value) =>
        double.IsFinite(value) ? Math.Clamp(value, MinZoom, MaxZoom) : MinZoom;

    private const double HandleSize = 8;
    private const double HandleHit = 10;
    private const double MinScreenSize = 6;
    private const double WheelZoomStep = 1.15;
    private const double WheelPanStep = 50;

    private double _panX;
    private double _panY;
    private bool _panning;
    private Point _panStart;
    private double _panStartX;
    private double _panStartY;

    private static readonly IBrush HighlightFill = new SolidColorBrush(Color.FromArgb(40, 0, 180, 255));
    private static readonly Pen HighlightStroke = new(new SolidColorBrush(Color.FromArgb(180, 0, 180, 255)), 1);
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(56, 255, 170, 0));
    private static readonly Pen SelectedStroke = new(new SolidColorBrush(Color.FromArgb(220, 255, 170, 0)), 1.5);
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly Pen HandleStroke = new(new SolidColorBrush(Color.FromArgb(220, 255, 170, 0)), 1);
    private static readonly IBrush SearchFill = new SolidColorBrush(Color.FromArgb(28, 180, 80, 255));
    private static readonly Pen SearchStroke = new(new SolidColorBrush(Color.FromArgb(200, 180, 80, 255)), 1)
    {
        DashStyle = DashStyle.Dash
    };
    private static readonly IBrush DraftFill = new SolidColorBrush(Color.FromArgb(40, 80, 200, 255));
    private static readonly Pen DraftStroke = new(new SolidColorBrush(Color.FromArgb(220, 80, 200, 255)), 1)
    {
        DashStyle = DashStyle.Dash
    };
    private static readonly IBrush RedactionFill = new SolidColorBrush(Color.FromArgb(70, 220, 40, 40));
    private static readonly Pen RedactionStroke = new(new SolidColorBrush(Color.FromArgb(220, 220, 40, 40)), 1.5);
    private static readonly Pen RedactionRejectedStroke = new(new SolidColorBrush(Color.FromArgb(160, 220, 40, 40)), 1.5)
    {
        DashStyle = DashStyle.Dash
    };
    private static readonly IBrush OcrWordFill = new SolidColorBrush(Color.FromArgb(30, 40, 200, 90));
    private static readonly Pen OcrWordStroke = new(new SolidColorBrush(Color.FromArgb(150, 40, 200, 90)), 1);

    private enum EditMode
    {
        None,
        Draw,
        Move,
        N,
        S,
        E,
        W,
        NW,
        NE,
        SW,
        SE
    }

    private EditMode _mode;
    private Point _start;
    private Point _current;
    private Rect _editOrigin;
    private Rect _editRect;

    static PagePreview()
    {
        AffectsRender<PagePreview>(PageImageProperty, HighlightsProperty, OcrWordsProperty, ShowOcrWordsProperty, AllowDrawProperty, ZoomProperty);
    }

    public Bitmap? PageImage
    {
        get => GetValue(PageImageProperty);
        set => SetValue(PageImageProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public IReadOnlyList<IndexHighlight>? Highlights
    {
        get => GetValue(HighlightsProperty);
        set => SetValue(HighlightsProperty, value);
    }

    public IReadOnlyList<LatticeWord>? OcrWords
    {
        get => GetValue(OcrWordsProperty);
        set => SetValue(OcrWordsProperty, value);
    }

    public bool ShowOcrWords
    {
        get => GetValue(ShowOcrWordsProperty);
        set => SetValue(ShowOcrWordsProperty, value);
    }

    public bool AllowDraw
    {
        get => GetValue(AllowDrawProperty);
        set => SetValue(AllowDrawProperty, value);
    }

    public ICommand? ZoneDrawnCommand
    {
        get => GetValue(ZoneDrawnCommandProperty);
        set => SetValue(ZoneDrawnCommandProperty, value);
    }

    public ICommand? ZoneChangedCommand
    {
        get => GetValue(ZoneChangedCommandProperty);
        set => SetValue(ZoneChangedCommandProperty, value);
    }

    public ICommand? HighlightClickedCommand
    {
        get => GetValue(HighlightClickedCommandProperty);
        set => SetValue(HighlightClickedCommandProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bitmap = PageImage;
        if (bitmap is null)
            return;

        var dest = ImageDestination();
        if (dest.Width <= 0 || dest.Height <= 0)
            return;

        context.DrawImage(bitmap, dest);

        if (ShowOcrWords && OcrWords is not null)
        {
            foreach (var word in OcrWords)
                context.DrawRectangle(OcrWordFill, OcrWordStroke, ToScreen(word, dest));
        }

        if (Highlights is not null)
        {
            foreach (var highlight in Highlights)
            {
                if (_mode is not EditMode.None and not EditMode.Draw && highlight.IsSelected)
                    continue;

                var rect = ToScreen(highlight, dest);
                if (highlight.IsRedaction)
                {
                    context.DrawRectangle(
                        highlight.IsRejected ? null : (highlight.IsSelected ? SelectedFill : RedactionFill),
                        highlight.IsRejected ? RedactionRejectedStroke : (highlight.IsSelected ? SelectedStroke : RedactionStroke),
                        rect);
                }
                else if (highlight.IsSearchZone)
                {
                    context.DrawRectangle(
                        highlight.IsSelected ? SelectedFill : SearchFill,
                        highlight.IsSelected ? SearchStroke : SearchStroke,
                        rect);
                }
                else
                {
                    context.DrawRectangle(
                        highlight.IsSelected ? SelectedFill : HighlightFill,
                        highlight.IsSelected ? SelectedStroke : HighlightStroke,
                        rect);
                }
            }
        }

        if (_mode == EditMode.Draw)
        {
            context.DrawRectangle(DraftFill, DraftStroke, NormalizeScreen(_start, _current));
        }
        else if (_mode != EditMode.None)
        {
            context.DrawRectangle(SelectedFill, SelectedStroke, _editRect);
            DrawHandles(context, _editRect);
        }
        else
        {
            var selected = SelectedHighlight();
            if (selected is { CanEdit: true })
                DrawHandles(context, ToScreen(selected, dest));
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageImageProperty)
        {
            _panX = 0;
            _panY = 0;
            if (Zoom != MinZoom)
                SetCurrentValue(ZoomProperty, MinZoom);
        }
        else if (change.Property == ZoomProperty)
        {
            if (Zoom <= MinZoom)
            {
                _panX = 0;
                _panY = 0;
            }
            else
            {
                ClampPan(FitDestination());
            }
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var bitmap = PageImage;
        if (bitmap is null || e.Delta.Y == 0)
            return;

        var fit = FitDestination();
        if (fit.Width <= 0)
            return;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _panY += e.Delta.Y * WheelPanStep;
            ClampPan(fit);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var point = e.GetPosition(this);
        var before = ImageDestination();
        var relX = before.Width > 0 ? (point.X - before.X) / before.Width : 0.5;
        var relY = before.Height > 0 ? (point.Y - before.Y) / before.Height : 0.5;

        var factor = e.Delta.Y > 0 ? WheelZoomStep : 1 / WheelZoomStep;
        var newZoom = Math.Clamp(Zoom * factor, MinZoom, MaxZoom);
        if (Math.Abs(newZoom - Zoom) < 0.0001)
        {
            e.Handled = true;
            return;
        }

        var width = fit.Width * newZoom;
        var height = fit.Height * newZoom;
        var cx = fit.X + fit.Width / 2;
        var cy = fit.Y + fit.Height / 2;
        _panX = point.X - relX * width - cx + width / 2;
        _panY = point.Y - relY * height - cy + height / 2;

        Zoom = newZoom;
        ClampPan(fit);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (PageImage is not null)
        {
            var panProps = e.GetCurrentPoint(this).Properties;
            if (panProps.IsLeftButtonPressed && !AllowDraw)
            {
                var clickDest = ImageDestination();
                var clickPoint = e.GetPosition(this);
                var clickedHighlight = clickDest.Contains(clickPoint) ? HitHighlight(clickDest, clickPoint) : null;
                if (clickedHighlight is not null)
                {
                    if (HighlightClickedCommand?.CanExecute(clickedHighlight.FieldId) == true)
                        HighlightClickedCommand.Execute(clickedHighlight.FieldId);
                    e.Handled = true;
                    return;
                }
            }

            if (panProps.IsMiddleButtonPressed || (panProps.IsLeftButtonPressed && !AllowDraw))
            {
                _panning = true;
                _panStart = e.GetPosition(this);
                _panStartX = _panX;
                _panStartY = _panY;
                Cursor = new Cursor(StandardCursorType.SizeAll);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        if (!AllowDraw || PageImage is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        var dest = ImageDestination();
        var point = e.GetPosition(this);
        if (!dest.Contains(point))
            return;

        var selected = SelectedHighlight();
        if (selected is { CanEdit: true })
        {
            var selectedRect = ToScreen(selected, dest);
            var handle = HitHandle(selectedRect, point);
            if (handle != EditMode.None)
            {
                BeginEdit(handle, point, selectedRect);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }

            if (selectedRect.Contains(point))
            {
                BeginEdit(EditMode.Move, point, selectedRect);
                e.Pointer.Capture(this);
                e.Handled = true;
                return;
            }
        }

        var hit = HitHighlight(dest, point);
        if (hit is not null)
        {
            if (HighlightClickedCommand?.CanExecute(hit.FieldId) == true)
                HighlightClickedCommand.Execute(hit.FieldId);
            e.Handled = true;
            return;
        }

        _mode = EditMode.Draw;
        _start = point;
        _current = point;
        e.Pointer.Capture(this);
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_panning)
        {
            var current = e.GetPosition(this);
            _panX = _panStartX + (current.X - _panStart.X);
            _panY = _panStartY + (current.Y - _panStart.Y);
            ClampPan(FitDestination());
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        var point = ClampToImage(e.GetPosition(this));

        if (_mode == EditMode.None)
        {
            UpdateHoverCursor(point);
            UpdateOcrWordTooltip(point);
            return;
        }

        _current = point;
        if (_mode == EditMode.Draw)
        {
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _editRect = ApplyEdit(_editOrigin, _start, point, _mode, ImageDestination());
        RaiseZoneChanged(_editRect);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_panning)
        {
            _panning = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;
            e.Handled = true;
            return;
        }

        if (_mode == EditMode.None)
            return;

        var mode = _mode;
        var dest = ImageDestination();
        _mode = EditMode.None;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (mode == EditMode.Draw)
        {
            var screen = NormalizeScreen(_start, _current);
            _start = default;
            _current = default;
            InvalidateVisual();
            if (dest.Width > 0 && screen.Width >= MinScreenSize && screen.Height >= MinScreenSize)
                RaiseCommand(ZoneDrawnCommand, ToNormalized(screen, dest));
            return;
        }

        RaiseZoneChanged(_editRect);
        _editRect = default;
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panning = false;
        Cursor = Cursor.Default;
        if (_mode == EditMode.None)
            return;
        _mode = EditMode.None;
        _start = default;
        _current = default;
        _editRect = default;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        ToolTip.SetIsOpen(this, false);
    }

    private void BeginEdit(EditMode mode, Point point, Rect origin)
    {
        _mode = mode;
        _start = point;
        _current = point;
        _editOrigin = origin;
        _editRect = origin;
        InvalidateVisual();
    }

    private void UpdateHoverCursor(Point point)
    {
        if (!AllowDraw)
        {
            Cursor = Cursor.Default;
            return;
        }

        var dest = ImageDestination();
        var selected = SelectedHighlight();
        if (selected is { CanEdit: true })
        {
            var rect = ToScreen(selected, dest);
            Cursor = HitHandle(rect, point) switch
            {
                EditMode.N or EditMode.S => new Cursor(StandardCursorType.SizeNorthSouth),
                EditMode.E or EditMode.W => new Cursor(StandardCursorType.SizeWestEast),
                EditMode.NW or EditMode.SE => new Cursor(StandardCursorType.TopLeftCorner),
                EditMode.NE or EditMode.SW => new Cursor(StandardCursorType.TopRightCorner),
                EditMode.None when rect.Contains(point) => new Cursor(StandardCursorType.SizeAll),
                _ => new Cursor(StandardCursorType.Cross)
            };
            if (HitHandle(rect, point) != EditMode.None || rect.Contains(point))
                return;
        }

        Cursor = HitHighlight(dest, point) is null
            ? new Cursor(StandardCursorType.Cross)
            : new Cursor(StandardCursorType.Arrow);
    }

    private IndexHighlight? SelectedHighlight() =>
        Highlights?.FirstOrDefault(highlight => highlight.IsSelected);

    private IndexHighlight? HitHighlight(Rect dest, Point point)
    {
        if (Highlights is null)
            return null;

        for (var i = Highlights.Count - 1; i >= 0; i--)
        {
            if (ToScreen(Highlights[i], dest).Contains(point))
                return Highlights[i];
        }

        return null;
    }

    // Manually driven rather than relying on Avalonia's automatic hover-delay ToolTip triggering —
    // this control draws every word itself instead of having one element per word, so there's nothing
    // for the automatic behavior to key off as the pointer moves between words within the same control.
    private void UpdateOcrWordTooltip(Point point)
    {
        if (!ShowOcrWords || OcrWords is null)
        {
            ToolTip.SetIsOpen(this, false);
            return;
        }

        var dest = ImageDestination();
        var word = HitOcrWord(dest, point);
        if (word is null)
        {
            ToolTip.SetIsOpen(this, false);
            return;
        }

        ToolTip.SetTip(this, $"{word.Text} ({word.Confidence:F0}%)");
        ToolTip.SetIsOpen(this, true);
    }

    private LatticeWord? HitOcrWord(Rect dest, Point point)
    {
        if (OcrWords is null)
            return null;

        for (var i = OcrWords.Count - 1; i >= 0; i--)
        {
            if (ToScreen(OcrWords[i], dest).Contains(point))
                return OcrWords[i];
        }

        return null;
    }

    private static EditMode HitHandle(Rect rect, Point point)
    {
        foreach (var (mode, handle) in Handles(rect))
        {
            if (handle.Inflate(HandleHit - HandleSize / 2).Contains(point))
                return mode;
        }

        return EditMode.None;
    }

    private static void DrawHandles(DrawingContext context, Rect rect)
    {
        foreach (var (_, handle) in Handles(rect))
            context.DrawRectangle(HandleFill, HandleStroke, handle);
    }

    private static IEnumerable<(EditMode Mode, Rect Handle)> Handles(Rect rect)
    {
        yield return (EditMode.NW, HandleAt(rect.TopLeft));
        yield return (EditMode.N, HandleAt(new Point(rect.Center.X, rect.Top)));
        yield return (EditMode.NE, HandleAt(rect.TopRight));
        yield return (EditMode.E, HandleAt(new Point(rect.Right, rect.Center.Y)));
        yield return (EditMode.SE, HandleAt(rect.BottomRight));
        yield return (EditMode.S, HandleAt(new Point(rect.Center.X, rect.Bottom)));
        yield return (EditMode.SW, HandleAt(rect.BottomLeft));
        yield return (EditMode.W, HandleAt(new Point(rect.Left, rect.Center.Y)));
    }

    private static Rect HandleAt(Point center)
    {
        var half = HandleSize / 2;
        return new Rect(center.X - half, center.Y - half, HandleSize, HandleSize);
    }

    private static Rect ApplyEdit(Rect origin, Point start, Point current, EditMode mode, Rect dest)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        var left = origin.X;
        var top = origin.Y;
        var right = origin.Right;
        var bottom = origin.Bottom;

        switch (mode)
        {
            case EditMode.Move:
                left += dx;
                right += dx;
                top += dy;
                bottom += dy;
                break;
            case EditMode.N:
                top += dy;
                break;
            case EditMode.S:
                bottom += dy;
                break;
            case EditMode.E:
                right += dx;
                break;
            case EditMode.W:
                left += dx;
                break;
            case EditMode.NW:
                left += dx;
                top += dy;
                break;
            case EditMode.NE:
                right += dx;
                top += dy;
                break;
            case EditMode.SW:
                left += dx;
                bottom += dy;
                break;
            case EditMode.SE:
                right += dx;
                bottom += dy;
                break;
        }

        if (right < left + MinScreenSize)
        {
            if (mode is EditMode.W or EditMode.NW or EditMode.SW)
                left = right - MinScreenSize;
            else
                right = left + MinScreenSize;
        }

        if (bottom < top + MinScreenSize)
        {
            if (mode is EditMode.N or EditMode.NW or EditMode.NE)
                top = bottom - MinScreenSize;
            else
                bottom = top + MinScreenSize;
        }

        if (mode == EditMode.Move)
        {
            var width = right - left;
            var height = bottom - top;
            left = Math.Clamp(left, dest.X, dest.X + dest.Width - width);
            top = Math.Clamp(top, dest.Y, dest.Y + dest.Height - height);
            right = left + width;
            bottom = top + height;
        }
        else
        {
            left = Math.Clamp(left, dest.X, dest.Right);
            right = Math.Clamp(right, dest.X, dest.Right);
            top = Math.Clamp(top, dest.Y, dest.Bottom);
            bottom = Math.Clamp(bottom, dest.Y, dest.Bottom);
        }

        return new Rect(new Point(left, top), new Point(right, bottom));
    }

    private void RaiseZoneChanged(Rect screen)
    {
        var dest = ImageDestination();
        if (dest.Width <= 0)
            return;
        RaiseCommand(ZoneChangedCommand, ToNormalized(screen, dest));
    }

    private static void RaiseCommand(ICommand? command, object parameter)
    {
        if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }

    private static NormalizedRect ToNormalized(Rect screen, Rect dest)
    {
        return new NormalizedRect(
            (float)((screen.X - dest.X) / dest.Width),
            (float)((screen.Y - dest.Y) / dest.Height),
            (float)(screen.Width / dest.Width),
            (float)(screen.Height / dest.Height));
    }

    private Rect ImageDestination()
    {
        var fit = FitDestination();
        if (fit.Width <= 0 || fit.Height <= 0)
            return fit;

        var zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);
        if (zoom <= MinZoom)
            return fit;

        var width = fit.Width * zoom;
        var height = fit.Height * zoom;
        var cx = fit.X + fit.Width / 2 + _panX;
        var cy = fit.Y + fit.Height / 2 + _panY;
        return new Rect(cx - width / 2, cy - height / 2, width, height);
    }

    private Rect FitDestination()
    {
        var bitmap = PageImage;
        if (bitmap is null)
            return default;
        return UniformDestination(Bounds.Size, bitmap.PixelSize.Width, bitmap.PixelSize.Height);
    }

    private void ClampPan(Rect fit)
    {
        if (fit.Width <= 0 || fit.Height <= 0)
        {
            _panX = 0;
            _panY = 0;
            return;
        }

        var zoom = Math.Clamp(Zoom, MinZoom, MaxZoom);
        var width = fit.Width * zoom;
        var height = fit.Height * zoom;
        var maxPanX = Math.Max(0, (width - Bounds.Width) / 2);
        var maxPanY = Math.Max(0, (height - Bounds.Height) / 2);
        _panX = Math.Clamp(_panX, -maxPanX, maxPanX);
        _panY = Math.Clamp(_panY, -maxPanY, maxPanY);
    }

    private Point ClampToImage(Point point)
    {
        var dest = ImageDestination();
        if (dest.Width <= 0)
            return point;
        return new Point(
            Math.Clamp(point.X, dest.X, dest.X + dest.Width),
            Math.Clamp(point.Y, dest.Y, dest.Y + dest.Height));
    }

    private static Rect ToScreen(IndexHighlight highlight, Rect dest)
    {
        return new Rect(
            dest.X + highlight.X * dest.Width,
            dest.Y + highlight.Y * dest.Height,
            Math.Max(1, highlight.Width * dest.Width),
            Math.Max(1, highlight.Height * dest.Height));
    }

    private static Rect ToScreen(LatticeWord word, Rect dest)
    {
        return new Rect(
            dest.X + word.X * dest.Width,
            dest.Y + word.Y * dest.Height,
            Math.Max(1, word.Width * dest.Width),
            Math.Max(1, word.Height * dest.Height));
    }

    private static Rect NormalizeScreen(Point start, Point current)
    {
        var x = Math.Min(start.X, current.X);
        var y = Math.Min(start.Y, current.Y);
        return new Rect(x, y, Math.Abs(current.X - start.X), Math.Abs(current.Y - start.Y));
    }

    private static Rect UniformDestination(Size bounds, int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return default;

        var scale = Math.Min(bounds.Width / imageWidth, bounds.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        var x = (bounds.Width - width) / 2;
        var y = (bounds.Height - height) / 2;
        return new Rect(x, y, width, height);
    }
}
