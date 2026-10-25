using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using BlackBox.Core.Media;

namespace BlackBox.App.Views;

/// <summary>
/// A 3D model in the chat or the File inspector, drawn in software (see <see cref="MeshRenderer"/>):
/// drag to turn it, the wheel to come closer, a double click to see it as at first; with
/// <see cref="CanPan"/>, a right or middle drag moves it about. While it moves it is drawn plainly;
/// once still, again with smooth edges. How it is seen is in <see cref="Yaw"/>, <see cref="Pitch"/>,
/// <see cref="Zoom"/> and the pan, which the File inspector binds to say and set it.
/// </summary>
public sealed class ModelViewer : Control
{
    public static readonly StyledProperty<Mesh?> MeshProperty = AvaloniaProperty.Register<ModelViewer, Mesh?>(nameof(Mesh));

    public Mesh? Mesh
    {
        get => GetValue(MeshProperty);
        set => SetValue(MeshProperty, value);
    }

    public static readonly StyledProperty<double> YawProperty = AvaloniaProperty.Register<ModelViewer, double>(nameof(Yaw), StartYaw, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> PitchProperty = AvaloniaProperty.Register<ModelViewer, double>(nameof(Pitch), StartPitch, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<ModelViewer, double>(nameof(Zoom), 1, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> PanXProperty = AvaloniaProperty.Register<ModelViewer, double>(nameof(PanX), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> PanYProperty = AvaloniaProperty.Register<ModelViewer, double>(nameof(PanY), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);
    public static readonly StyledProperty<double> MinZoomProperty = AvaloniaProperty.Register<ModelViewer, double>(nameof(MinZoom), 0.4);
    public static readonly StyledProperty<bool> CanPanProperty = AvaloniaProperty.Register<ModelViewer, bool>(nameof(CanPan));

    /// <summary>Degrees around it: 0 from the front, 90 from its right.</summary>
    public double Yaw { get => GetValue(YawProperty); set => SetValue(YawProperty, value); }

    /// <summary>Degrees above it, -89 to 89.</summary>
    public double Pitch { get => GetValue(PitchProperty); set => SetValue(PitchProperty, value); }

    /// <summary>1 fits the whole model; up to 8.</summary>
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }

    /// <summary>How far it is moved right and down, in parts of the view's shorter side.</summary>
    public double PanX { get => GetValue(PanXProperty); set => SetValue(PanXProperty, value); }

    public double PanY { get => GetValue(PanYProperty); set => SetValue(PanYProperty, value); }

    public double MinZoom { get => GetValue(MinZoomProperty); set => SetValue(MinZoomProperty, value); }

    /// <summary>A right or middle drag moves the model about (the File inspector's; in the chat they do nothing).</summary>
    public bool CanPan { get => GetValue(CanPanProperty); set => SetValue(CanPanProperty, value); }

    public const double StartYaw = -30, StartPitch = 15;
    private Point? _dragFrom;
    private bool _panning;
    private WriteableBitmap? _image;
    private int _request;
    private bool _drawing;
    private bool _again;
    private readonly DispatcherTimer _settle;

    public ModelViewer()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        _settle = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            _settle!.Stop();
            Redraw(smooth: true);
        });
    }

    static ModelViewer()
    {
        AffectsRender<ModelViewer>(MeshProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MeshProperty || change.Property == BoundsProperty)
            Redraw(smooth: true);
        else if (change.Property == YawProperty || change.Property == PitchProperty || change.Property == ZoomProperty
                 || change.Property == PanXProperty || change.Property == PanYProperty)
            Redraw(smooth: _dragFrom is null && !_wheeling);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this).Properties;
        if (e.ClickCount == 2 && point.IsLeftButtonPressed)
        {
            Reset();
            return;
        }
        _panning = !point.IsLeftButtonPressed;
        if (_panning && !CanPan)
            return;
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        var at = e.GetPosition(this);
        _dragFrom = at;
        if (_panning)
        {
            var side = Math.Max(1, Math.Min(Bounds.Width, Bounds.Height));
            PanX += (at.X - from.X) / side;
            PanY += (at.Y - from.Y) / side;
        }
        else
        {
            // Kept within one turn either way, so the angle the File inspector says stays readable.
            var yaw = Yaw - (at.X - from.X) * 0.6;
            Yaw = yaw - 360 * Math.Round(yaw / 360);
            Pitch = Math.Clamp(Pitch + (at.Y - from.Y) * 0.6, -89, 89);
        }
    }

    /// <summary>As at first: from the front left, a little above, the whole model in view.</summary>
    public void Reset()
    {
        (Yaw, Pitch, Zoom, PanX, PanY) = (StartYaw, StartPitch, 1, 0, 0);
    }

    private bool _wheeling;

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragFrom is null)
            return;
        _dragFrom = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _wheeling = true;
        Zoom = Math.Clamp(Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), MinZoom, 8);
        _wheeling = false;
        e.Handled = true; // the chat doesn't scroll while the model is zoomed
    }

    /// <summary>Draws on a background thread; while one drawing runs, only the latest request waits.</summary>
    private void Redraw(bool smooth)
    {
        if (!smooth)
        {
            _settle.Stop();
            _settle.Start();
        }
        var mesh = Mesh;
        var (w, h) = ((int)Bounds.Width, (int)Bounds.Height);
        if (mesh is null || w < 8 || h < 8)
            return;
        if (_drawing)
        {
            _again = true;
            return;
        }
        _drawing = true;
        var request = ++_request;
        var (yaw, pitch, zoom, panX, panY) = (Yaw, Pitch, Zoom, PanX, PanY);
        var scaling = VisualRoot?.RenderScaling ?? 1;
        var (pw, ph) = ((int)(w * scaling), (int)(h * scaling));
        Task.Run(() =>
        {
            byte[]? pixels = null;
            var rowBytes = 0;
            try
            {
                using var picture = MeshRenderer.Render(mesh, pw, ph, yaw, pitch, zoom, smooth ? 2 : 1, panX, panY);
                // Copied out while the bitmap still exists: the UI thread gets only the bytes.
                pixels = new byte[picture.ByteCount];
                rowBytes = picture.RowBytes;
                System.Runtime.InteropServices.Marshal.Copy(picture.GetPixels(), pixels, 0, pixels.Length);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A model that can't be drawn stays blank; the next turn tries again.
            }
            Dispatcher.UIThread.Post(() =>
            {
                _drawing = false;
                if (pixels is not null && request == _request && Mesh == mesh)
                {
                    if (_image is null || _image.PixelSize.Width != pw || _image.PixelSize.Height != ph)
                    {
                        _image?.Dispose();
                        _image = new WriteableBitmap(new PixelSize(pw, ph), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Opaque);
                    }
                    using (var buffer = _image.Lock())
                        for (var y = 0; y < ph; y++)
                            System.Runtime.InteropServices.Marshal.Copy(pixels, y * rowBytes, buffer.Address + y * buffer.RowBytes, Math.Min(rowBytes, buffer.RowBytes));
                    InvalidateVisual();
                }
                if (_again)
                {
                    _again = false;
                    Redraw(smooth: false);
                }
            });
        });
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(36, 36, 40)), new Rect(Bounds.Size));
        if (_image is { } image)
            context.DrawImage(image, new Rect(0, 0, image.PixelSize.Width, image.PixelSize.Height), new Rect(Bounds.Size));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _settle.Stop();
    }
}
