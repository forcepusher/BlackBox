using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using SkiaSharp;

namespace Sloperator.App.Views;

/// <summary>
/// A picture in the File inspector panel that zooms and pans, and on which an area is marked to ask
/// about it: a picture file, a video's frame, or what the panel showed frozen for marking (a text,
/// Windows' previewer). At zoom 1 it fits, never shown bigger than it is. The wheel zooms about the
/// mouse, a right or middle drag pans, a left drag marks an area (with <see cref="CanMark"/>);
/// zoomed in far enough to see pixels, they are drawn as squares. An SVG (<see cref="Drawing"/>) is
/// drawn as shapes at whatever size it shows, so it stays sharp however far it is zoomed.
/// </summary>
public sealed class ZoomBox : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<ZoomBox, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<SvgPicture?> DrawingProperty = AvaloniaProperty.Register<ZoomBox, SvgPicture?>(nameof(Drawing));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ZoomBox, double>(nameof(Zoom), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay, coerce: (_, z) => Math.Clamp(z, 1, MaxZoom));

    public static readonly StyledProperty<DocumentArea?> MarkProperty = AvaloniaProperty.Register<ZoomBox, DocumentArea?>(nameof(Mark));

    public static readonly StyledProperty<bool> CanMarkProperty = AvaloniaProperty.Register<ZoomBox, bool>(nameof(CanMark));

    /// <summary>How far it zooms in: a pixel of a small picture as a square of tens of the screen's.</summary>
    public const double MaxZoom = 32;

    /// <summary>A wheel notch, or a press of + or −.</summary>
    public const double Step = 1.25;

    public Bitmap? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>An SVG, drawn in place of <see cref="Source"/>, sized in its own units.</summary>
    public SvgPicture? Drawing { get => GetValue(DrawingProperty); set => SetValue(DrawingProperty, value); }

    /// <summary>1 fits it in; more is bigger.</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>The area marked on it, in parts of the picture; null with none.</summary>
    public DocumentArea? Mark { get => GetValue(MarkProperty); set => SetValue(MarkProperty, value); }

    /// <summary>A left drag marks an area (in the panel, which has a chat to ask about it in).</summary>
    public bool CanMark { get => GetValue(CanMarkProperty); set => SetValue(CanMarkProperty, value); }

    /// <summary>An area was marked by a drag (null: a click that marked nothing).</summary>
    public event Action<DocumentArea?>? Marked;

    /// <summary>The point of the picture, in its pixels, at the middle of the box.</summary>
    private Point _center;

    /// <summary>How big what it shows is: a picture's pixels, a drawing's units.</summary>
    private Size _size;

    /// <summary>A picture or a drawing to show.</summary>
    private bool Shows => Source is not null || Drawing is not null;

    static ZoomBox()
    {
        AffectsRender<ZoomBox>(SourceProperty, DrawingProperty, ZoomProperty, MarkProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ZoomBox>(true);
        FocusableProperty.OverrideDefaultValue<ZoomBox>(true);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == DrawingProperty)
        {
            var size = Drawing is { } drawing ? new Size(drawing.Width, drawing.Height) : Source is { } source ? source.PixelSize.ToSize(1) : _size;
            if (size == _size)
                return;
            // Another picture: fitted again. The next frame of a video is as big, and keeps its zoom.
            _size = size;
            _center = new Point(_size.Width / 2.0, _size.Height / 2.0);
            Zoom = 1;
        }
        else if (change.Property == CanMarkProperty)
            Cursor = CanMark ? new Cursor(StandardCursorType.Cross) : null;
    }

    /// <summary>Pixels of the picture to DIPs of the box: as fitted (not bigger than it is), times the zoom.</summary>
    private double Scale
    {
        get
        {
            if (_size.Width <= 0 || _size.Height <= 0)
                return 1;
            var fit = Math.Min(1, Math.Min(Bounds.Width / _size.Width, Bounds.Height / _size.Height));
            return fit * Zoom;
        }
    }

    /// <summary>Where the picture's top left is in the box: its middle at <see cref="_center"/>, kept in view.</summary>
    private Point Origin
    {
        get
        {
            var scale = Scale;
            double Axis(double box, double size, double center)
            {
                var drawn = size * scale;
                if (drawn <= box)
                    return (box - drawn) / 2;
                return Math.Clamp(box / 2 - center * scale, box - drawn, 0);
            }
            return new Point(Axis(Bounds.Width, _size.Width, _center.X), Axis(Bounds.Height, _size.Height, _center.Y));
        }
    }

    private Point ToPicture(Point inBox) => (inBox - Origin) / Scale;

    public override void Render(DrawingContext context)
    {
        // A background, so the presses on it beside the picture reach the box.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (!Shows || _size.Width <= 0)
            return;
        var scale = Scale;
        var origin = Origin;
        var rect = new Rect(origin, new Size(_size.Width * scale, _size.Height * scale));
        if (Drawing is { } drawing)
            context.Custom(new DrawingOperation(drawing.Picture, rect, new Rect(Bounds.Size)));
        else if (Source is { } source)
        {
            var pixel = scale * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = pixel >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality }))
                context.DrawImage(source, new Rect(source.Size), rect);
        }
        if (Mark is { } mark)
            DrawMark(context, new Rect(rect.X + mark.Left * rect.Width, rect.Y + mark.Top * rect.Height,
                (mark.Right - mark.Left) * rect.Width, (mark.Bottom - mark.Top) * rect.Height));
    }

    private static readonly IBrush MarkFill = new SolidColorBrush(Color.FromArgb(0x33, 0xFC, 0xC2, 0x1B));
    private static readonly IPen MarkPen = new Pen(new SolidColorBrush(Color.FromRgb(0xFC, 0xC2, 0x1B)), 2);

    /// <summary>A marked area as every view draws it: the accent's outline over a faint fill of it.</summary>
    internal static void DrawMark(DrawingContext context, Rect rect)
    {
        context.FillRectangle(MarkFill, rect);
        context.DrawRectangle(MarkPen, rect.Deflate(1));
    }

    // ---- the mouse: zoom, pan, mark ---------------------------------------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!Shows)
            return;
        ZoomAt(Zoom * Math.Pow(Step, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    /// <summary>Zooms to <paramref name="zoom"/> keeping the picture's point under <paramref name="at"/> where it is.</summary>
    public void ZoomAt(double zoom, Point at)
    {
        var under = ToPicture(at);
        Zoom = zoom;
        var scale = Scale;
        _center = new Point(under.X + (Bounds.Width / 2 - at.X) / scale, under.Y + (Bounds.Height / 2 - at.Y) / scale);
        Hold();
        InvalidateVisual();
    }

    /// <summary>The middle kept where the picture is in view, so a pan beyond its edge isn't stored up.</summary>
    private void Hold()
    {
        var scale = Scale;
        var o = Origin;
        _center = new Point((Bounds.Width / 2 - o.X) / scale, (Bounds.Height / 2 - o.Y) / scale);
    }

    private Point? _panFrom, _markFrom;

    /// <summary>Where a right or middle drag began, and whether it has moved: a right-click that moved nothing opens the menu (Copy image).</summary>
    private Point _panStart;
    private bool _panned;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!Shows)
            return;
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _panFrom = _panStart = point.Position;
            _panned = false;
            Cursor = new Cursor(StandardCursorType.SizeAll);
        }
        else if (point.Properties.IsLeftButtonPressed && CanMark)
            _markFrom = ToPicture(point.Position);
        else
            return;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var at = e.GetPosition(this);
        if (_panFrom is { } from)
        {
            _panned |= Math.Abs(at.X - _panStart.X) + Math.Abs(at.Y - _panStart.Y) >= 4;
            _center -= (at - from) / Scale;
            _panFrom = at;
            Hold();
            InvalidateVisual();
        }
        else if (_markFrom is { } start)
        {
            var now = ToPicture(at);
            Mark = DocumentArea.Between(start.X / _size.Width, start.Y / _size.Height, now.X / _size.Width, now.Y / _size.Height);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // Handled before the base sees it: a drag that panned opens no right-click menu.
        if (_panFrom is not null && _panned)
            e.Handled = true;
        base.OnPointerReleased(e);
        if (_panFrom is not null)
        {
            _panFrom = null;
            Cursor = CanMark ? new Cursor(StandardCursorType.Cross) : null;
            e.Pointer.Capture(null);
            e.Handled = true;
            return;
        }
        if (_markFrom is not { } start)
            return;
        _markFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
        var end = ToPicture(e.GetPosition(this));
        // A few DIPs or less is a click: it marks nothing, and takes off what was marked.
        var scale = Scale;
        var area = Math.Abs(end.X - start.X) * scale < 4 && Math.Abs(end.Y - start.Y) * scale < 4 ? null
            : DocumentArea.Between(start.X / _size.Width, start.Y / _size.Height, end.X / _size.Width, end.Y / _size.Height);
        Mark = area;
        Marked?.Invoke(area);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _panFrom = null;
        if (_markFrom is not null)
        {
            _markFrom = null;
            Mark = null;
        }
    }

    /// <summary>
    /// A drawing drawn by Skia straight onto the box's canvas, <paramref name="place"/> its place in the
    /// box: its shapes at the size they show, never a picture of them stretched.
    /// </summary>
    private sealed class DrawingOperation(SKPicture picture, Rect place, Rect bounds) : ICustomDrawOperation
    {
        private readonly SKPicture _picture = picture;
        private readonly Rect _place = place;

        public Rect Bounds { get; } = bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => other is DrawingOperation o && o._picture == _picture && o._place == _place && o.Bounds == Bounds;

        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            var cull = _picture.CullRect;
            canvas.Save();
            canvas.ClipRect(new SKRect((float)Bounds.Left, (float)Bounds.Top, (float)Bounds.Right, (float)Bounds.Bottom));
            canvas.Translate((float)_place.X, (float)_place.Y);
            canvas.Scale((float)(_place.Width / cull.Width), (float)(_place.Height / cull.Height));
            canvas.DrawPicture(_picture);
            canvas.Restore();
        }
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? _size.Width : availableSize.Width, double.IsInfinity(availableSize.Height) ? _size.Height : availableSize.Height);
}
