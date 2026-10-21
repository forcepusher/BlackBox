using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace BlackBox.App.Views;

/// <summary>
/// A picture in the full-screen viewer: as large as the screen allows at first, the wheel coming
/// closer where it points, a drag moving it once it is larger than the screen, a double click
/// between that fit and its own size. From twice its size up its pixels stay square, for pixel art
/// and for a close look at what a model drew. A click beside it (not a drag) says so (<see cref="BesideClicked"/>).
/// </summary>
public sealed class PictureZoom : Control
{
    public static readonly StyledProperty<Bitmap?> SourceProperty = AvaloniaProperty.Register<PictureZoom, Bitmap?>(nameof(Source));

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>A click on the dark around the picture, which closes the viewer.</summary>
    public event EventHandler? BesideClicked;

    /// <summary>Screen pixels per picture pixel; null while it fits the screen.</summary>
    private double? _zoom;

    /// <summary>Where the picture's centre is, from the control's centre, while zoomed.</summary>
    private Vector _offset;

    private Point? _pressedAt;
    private Vector _offsetAtPress;
    private bool _dragged;

    static PictureZoom()
    {
        AffectsRender<PictureZoom>(SourceProperty);
        SourceProperty.Changed.AddClassHandler<PictureZoom>((z, _) => z.Fit());
    }

    public PictureZoom()
    {
        // The whole area takes the pointer, also where nothing is drawn.
        Cursor = new Cursor(StandardCursorType.Arrow);
    }

    private Size PictureSize => Source is { } s ? new Size(s.PixelSize.Width, s.PixelSize.Height) : default;

    private double FitScale
    {
        get
        {
            var p = PictureSize;
            return p.Width <= 0 || p.Height <= 0 ? 1 : Math.Min(Bounds.Width / p.Width, Bounds.Height / p.Height);
        }
    }

    private double Scale => _zoom ?? FitScale;

    /// <summary>Back to fitting the screen.</summary>
    public void Fit()
    {
        _zoom = null;
        _offset = default;
        InvalidateVisual();
    }

    private Rect PictureRect
    {
        get
        {
            var size = PictureSize * Scale;
            var centre = new Point(Bounds.Width / 2, Bounds.Height / 2) + (_zoom is null ? default : _offset);
            return new Rect(centre.X - size.Width / 2, centre.Y - size.Height / 2, size.Width, size.Height);
        }
    }

    public override void Render(DrawingContext context)
    {
        // Transparent, not nothing: the pointer is taken all over, for the wheel and the click beside.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Source is not { } source)
            return;
        var mode = Scale >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
            context.DrawImage(source, new Rect(PictureSize), PictureRect);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is null)
            return;
        var at = e.GetPosition(this);
        var before = Scale;
        var after = Math.Clamp(before * Math.Pow(1.25, e.Delta.Y), Math.Min(FitScale, 1) / 4, 32);
        // The point under the pointer stays under it.
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2) + (_zoom is null ? default : _offset);
        var fromCentre = at - centre;
        _offset = at - new Point(Bounds.Width / 2, Bounds.Height / 2) - fromCentre * (after / before);
        _zoom = after;
        Keep();
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>No further off screen than a picture larger than the screen needs to show its every edge.</summary>
    private void Keep()
    {
        var size = PictureSize * Scale;
        var maxX = Math.Max(0, (size.Width - Bounds.Width) / 2);
        var maxY = Math.Max(0, (size.Height - Bounds.Height) / 2);
        _offset = new Vector(Math.Clamp(_offset.X, -maxX, maxX), Math.Clamp(_offset.Y, -maxY, maxY));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2 && Source is not null)
        {
            // Its own size where it was clicked, or back to the fit.
            var at = e.GetPosition(this);
            if (_zoom is null || Math.Abs(Scale - 1) > 0.01)
            {
                var rect = PictureRect;
                var picturePoint = new Point((at.X - rect.X) / Scale, (at.Y - rect.Y) / Scale);
                _zoom = 1;
                _offset = at - new Point(Bounds.Width / 2, Bounds.Height / 2) - (picturePoint - new Point(PictureSize.Width / 2, PictureSize.Height / 2));
                Keep();
            }
            else
                Fit();
            _pressedAt = null;
            InvalidateVisual();
            e.Handled = true;
            return;
        }
        _pressedAt = e.GetPosition(this);
        _offsetAtPress = _offset;
        _dragged = false;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_pressedAt is not { } from)
            return;
        var moved = e.GetPosition(this) - from;
        if (!_dragged && Math.Abs(moved.X) + Math.Abs(moved.Y) < 4)
            return;
        _dragged = true;
        if (_zoom is null)
            return;
        _offset = _offsetAtPress + moved;
        Keep();
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_pressedAt is null)
            return;
        e.Pointer.Capture(null);
        var clickedBeside = !_dragged && !PictureRect.Contains(e.GetPosition(this));
        _pressedAt = null;
        if (clickedBeside)
            BesideClicked?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        Keep();
    }
}
