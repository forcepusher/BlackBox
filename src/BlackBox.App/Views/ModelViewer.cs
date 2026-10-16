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
/// A 3D model in the chat, drawn in software (see <see cref="MeshRenderer"/>): drag to turn it,
/// the wheel to come closer, a double click to see it as at first. While it turns it is drawn
/// plainly; once still, again with smooth edges.
/// </summary>
public sealed class ModelViewer : Control
{
    public static readonly StyledProperty<Mesh?> MeshProperty = AvaloniaProperty.Register<ModelViewer, Mesh?>(nameof(Mesh));

    public Mesh? Mesh
    {
        get => GetValue(MeshProperty);
        set => SetValue(MeshProperty, value);
    }

    private const double StartYaw = -30, StartPitch = 15;
    private double _yaw = StartYaw, _pitch = StartPitch, _zoom = 1;
    private Point? _dragFrom;
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
        if (change.Property == MeshProperty)
        {
            (_yaw, _pitch, _zoom) = (StartYaw, StartPitch, 1);
            Redraw(smooth: true);
        }
        else if (change.Property == BoundsProperty)
            Redraw(smooth: true);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.ClickCount == 2)
        {
            (_yaw, _pitch, _zoom) = (StartYaw, StartPitch, 1);
            Redraw(smooth: true);
            return;
        }
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
        _yaw -= (at.X - from.X) * 0.6;
        _pitch = Math.Clamp(_pitch + (at.Y - from.Y) * 0.6, -89, 89);
        _dragFrom = at;
        Redraw(smooth: false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.4, 8);
        Redraw(smooth: false);
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
        var (yaw, pitch, zoom) = (_yaw, _pitch, _zoom);
        var scaling = VisualRoot?.RenderScaling ?? 1;
        var (pw, ph) = ((int)(w * scaling), (int)(h * scaling));
        Task.Run(() =>
        {
            byte[]? pixels = null;
            var rowBytes = 0;
            try
            {
                using var picture = MeshRenderer.Render(mesh, pw, ph, yaw, pitch, zoom, smooth ? 2 : 1);
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
