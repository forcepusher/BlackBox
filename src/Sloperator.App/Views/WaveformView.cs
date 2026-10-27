using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Sloperator.App.ViewModels;
using Sloperator.Core.Media;

namespace Sloperator.App.Views;

/// <summary>
/// A sound's waveform in the File inspector (<see cref="InspectorSoundViewModel"/>): a ruler of times
/// over it, the playhead, and the stretch picked. A click on the waveform sets the playhead, a drag
/// over it picks a stretch, a click or drag on the ruler scrubs; the wheel zooms about the mouse
/// (Shift+wheel, or a right or middle drag, scrolls along), down to its samples.
/// </summary>
public sealed class WaveformView : Control
{
    /// <summary>How tall the ruler of times along the top is.</summary>
    private const double RulerHeight = 24;

    public static readonly StyledProperty<InspectorSoundViewModel?> SoundProperty = AvaloniaProperty.Register<WaveformView, InspectorSoundViewModel?>(nameof(Sound));

    public InspectorSoundViewModel? Sound { get => GetValue(SoundProperty); set => SetValue(SoundProperty, value); }

    private InspectorSoundViewModel? _sound;
    private WriteableBitmap? _wave;
    private (double Start, double Length, int Width, int Height)? _drawn;

    static WaveformView()
    {
        ClipToBoundsProperty.OverrideDefaultValue<WaveformView>(true);
        FocusableProperty.OverrideDefaultValue<WaveformView>(true);
    }

    public WaveformView()
    {
        Cursor = new Cursor(StandardCursorType.Ibeam);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != SoundProperty)
            return;
        if (_sound is not null)
            _sound.PropertyChanged -= OnSoundChanged;
        _sound = Sound;
        if (_sound is not null)
            _sound.PropertyChanged += OnSoundChanged;
        _drawn = null;
        InvalidateVisual();
    }

    private void OnSoundChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private double Width0 => Math.Max(1, Bounds.Width);

    private double TimeAt(double x) => _sound is { } s ? s.ViewStart + Math.Clamp(x, 0, Width0) / Width0 * s.ViewLength : 0;

    private double XOf(double seconds) => _sound is { } s ? (seconds - s.ViewStart) / s.ViewLength * Width0 : 0;

    // ---- drawing --------------------------------------------------------------------------------

    private static readonly IBrush Back = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1B));
    private static readonly IBrush RulerBack = new SolidColorBrush(Color.FromRgb(0x1A, 0x1C, 0x1F));
    private static readonly IPen Tick = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IPen Grid = new Pen(new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF)), 1);
    private static readonly IBrush Label = new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0x99));
    private static readonly IBrush RangeFill = new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0xA6, 0x4B));
    private static readonly IPen RangeEdge = new Pen(new SolidColorBrush(Color.FromRgb(0xE0, 0xA6, 0x4B)), 1.5);
    private static readonly IPen Playhead = new Pen(Brushes.White, 1.5);

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        context.FillRectangle(Back, new Rect(size));
        context.FillRectangle(RulerBack, new Rect(0, 0, size.Width, RulerHeight));
        if (_sound is not { Samples.Length: > 0 } sound)
            return;
        var waveRect = new Rect(0, RulerHeight, size.Width, Math.Max(0, size.Height - RulerHeight));
        DrawRuler(context, sound, size);
        context.DrawLine(Grid, new Point(0, waveRect.Center.Y), new Point(size.Width, waveRect.Center.Y));
        if (Wave(sound, waveRect) is { } wave)
            context.DrawImage(wave, new Rect(0, 0, wave.PixelSize.Width, wave.PixelSize.Height), waveRect);
        if (sound.Range is { } r)
        {
            var (x0, x1) = (XOf(r.From), XOf(r.To));
            context.FillRectangle(RangeFill, new Rect(x0, 0, Math.Max(1, x1 - x0), size.Height));
            context.DrawLine(RangeEdge, new Point(x0, 0), new Point(x0, size.Height));
            context.DrawLine(RangeEdge, new Point(x1, 0), new Point(x1, size.Height));
        }
        var x = XOf(sound.Position);
        if (x >= -1 && x <= size.Width + 1)
        {
            context.DrawLine(Playhead, new Point(x, 0), new Point(x, size.Height));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(new Point(x - 5, 0), true);
                g.LineTo(new Point(x + 5, 0));
                g.LineTo(new Point(x, 7));
                g.EndFigure(true);
            }
            context.DrawGeometry(Brushes.White, null, head);
        }
    }

    /// <summary>Steps between the ruler's marks, in seconds: the first that leaves room for its label.</summary>
    private static readonly double[] Steps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 1200];

    private void DrawRuler(DrawingContext context, InspectorSoundViewModel sound, Size size)
    {
        var perDip = sound.ViewLength / Width0;
        var step = Steps.FirstOrDefault(s => s / perDip >= 80, Steps[^1]);
        var decimals = step < 0.01 ? 3 : step < 0.1 ? 2 : step < 1 ? 1 : 0;
        var typeface = new Typeface(FontFamily.Default);
        for (var n = (long)Math.Ceiling(sound.ViewStart / step); n * step <= sound.ViewStart + sound.ViewLength; n++)
        {
            var x = Math.Round(XOf(n * step)) + 0.5;
            context.DrawLine(Tick, new Point(x, RulerHeight - 7), new Point(x, RulerHeight));
            context.DrawLine(Grid, new Point(x, RulerHeight), new Point(x, size.Height));
            var at = n * step;
            var label = at < 60 ? at.ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture) + " s" : SoundRange.Clock(at, decimals);
            var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, Label);
            context.DrawText(text, new Point(x + 4, 4));
        }
    }

    /// <summary>The waveform of the stretch in view, drawn at the screen's pixels again when the view or the size changes.</summary>
    private WriteableBitmap? Wave(InspectorSoundViewModel sound, Rect rect)
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var (w, h) = ((int)Math.Ceiling(rect.Width * scaling), (int)Math.Ceiling(rect.Height * scaling));
        if (w < 1 || h < 2)
            return null;
        var key = (sound.ViewStart, sound.ViewLength, w, h);
        if (_wave is not null && _drawn == key)
            return _wave;
        if (_wave is null || _wave.PixelSize.Width != w || _wave.PixelSize.Height != h)
        {
            _wave?.Dispose();
            _wave = new WriteableBitmap(new PixelSize(w, h), new Vector(96 * scaling, 96 * scaling), PixelFormat.Bgra8888, AlphaFormat.Premul);
        }
        var pixels = new uint[w * h];
        const uint color = 0xFFE0A64B;
        var samples = sound.Samples;
        var blocks = sound.Blocks;
        var rate = (double)sound.Rate;
        var mid = h / 2.0;
        var gain = sound.Gain * (h / 2.0 - 2);
        for (var x = 0; x < w; x++)
        {
            var t0 = sound.ViewStart + x / (double)w * sound.ViewLength;
            var t1 = sound.ViewStart + (x + 1) / (double)w * sound.ViewLength;
            // The samples in the column, and one either side: zoomed in past them, a line from one to the next.
            var a = Math.Clamp((long)Math.Floor(t0 * rate), 0, samples.Length - 1);
            var b = Math.Clamp((long)Math.Ceiling(t1 * rate), a, samples.Length - 1);
            float lo = float.MaxValue, hi = float.MinValue;
            if (b - a > 4 * InspectorSoundViewModel.BlockSize)
                for (var k = a / InspectorSoundViewModel.BlockSize; k <= b / InspectorSoundViewModel.BlockSize && k < blocks.Length; k++)
                {
                    lo = Math.Min(lo, blocks[k].Min);
                    hi = Math.Max(hi, blocks[k].Max);
                }
            else
                for (var i = a; i <= b; i++)
                {
                    lo = Math.Min(lo, samples[i]);
                    hi = Math.Max(hi, samples[i]);
                }
            if (t0 * rate >= samples.Length)
                continue;
            var top = (int)Math.Clamp(Math.Floor(mid - hi * gain), 0, h - 1);
            var bottom = (int)Math.Clamp(Math.Ceiling(mid - lo * gain), top + 1, h);
            for (var y = top; y < bottom; y++)
                pixels[y * w + x] = color;
        }
        using (var buffer = _wave.Lock())
            for (var y = 0; y < h; y++)
                System.Runtime.InteropServices.Marshal.Copy((int[])(object)pixels, y * w, buffer.Address + y * buffer.RowBytes, w);
        _drawn = key;
        return _wave;
    }

    // ---- the mouse ----------------------------------------------------------------------------

    private enum Drag { None, Scrub, Pick, Pan }

    private Drag _drag;
    private Point _from;
    private double _fromTime;
    private bool _moved, _wasPlaying;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_sound is not { Samples.Length: > 0 } sound)
            return;
        var point = e.GetCurrentPoint(this);
        _from = point.Position;
        _fromTime = TimeAt(_from.X);
        _moved = false;
        if (point.Properties.IsRightButtonPressed || point.Properties.IsMiddleButtonPressed)
        {
            _drag = Drag.Pan;
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }
        else if (!point.Properties.IsLeftButtonPressed)
            return;
        else if (_from.Y < RulerHeight)
        {
            // Scrubbing while it plays would open the sound again at every step: it pauses, and plays on from where it is let go.
            _drag = Drag.Scrub;
            _wasPlaying = sound.IsPlaying;
            sound.Pause();
            sound.Position = _fromTime;
        }
        else
            _drag = Drag.Pick;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_sound is not { } sound || _drag == Drag.None)
            return;
        var at = e.GetPosition(this);
        _moved |= Math.Abs(at.X - _from.X) > 3;
        switch (_drag)
        {
            case Drag.Pan:
                sound.ViewStart = sound.Clamp(sound.ViewStart - (at.X - _from.X) / Width0 * sound.ViewLength);
                _from = at;
                break;
            case Drag.Scrub:
                sound.Position = TimeAt(at.X);
                break;
            case Drag.Pick when _moved:
                var t = TimeAt(at.X);
                sound.Range = (Math.Min(_fromTime, t), Math.Max(_fromTime, t));
                // Past the edge, the view scrolls along.
                if (at.X < 0 || at.X > Width0)
                    sound.ViewStart = sound.Clamp(sound.ViewStart + (at.X < 0 ? at.X : at.X - Width0) / Width0 * sound.ViewLength * 0.1);
                break;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_sound is not { } sound || _drag == Drag.None)
            return;
        var drag = _drag;
        _drag = Drag.None;
        e.Pointer.Capture(null);
        e.Handled = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        switch (drag)
        {
            case Drag.Scrub:
                sound.Seek(TimeAt(e.GetPosition(this).X));
                if (_wasPlaying)
                    sound.PlayCommand.Execute(null);
                break;
            case Drag.Pick when _moved && sound.Range is { } r && r.To - r.From > 0:
                sound.Seek(r.From);
                sound.Pick(r);
                break;
            case Drag.Pick:
                // A click: the playhead goes there, and the stretch picked (if one) is let go.
                sound.Seek(_fromTime);
                sound.Pick(null);
                break;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = Drag.None;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_sound is not { Samples.Length: > 0 } sound)
            return;
        var along = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? e.Delta.Y : e.Delta.X;
        if (along != 0)
            sound.ViewStart = sound.Clamp(sound.ViewStart - along * 0.1 * sound.ViewLength);
        else
        {
            sound.ZoomAnchor = TimeAt(e.GetPosition(this).X);
            sound.Zoom = Math.Clamp(sound.Zoom * Math.Pow(ZoomBox.Step, e.Delta.Y), 1, sound.MaxZoom);
            sound.ZoomAnchor = null;
        }
        e.Handled = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _wave?.Dispose();
        _wave = null;
        _drawn = null;
    }
}
