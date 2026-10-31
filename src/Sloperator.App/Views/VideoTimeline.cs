using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Sloperator.App.Views;

/// <summary>The window's palette (App.axaml), for what the timeline draws itself.</summary>
internal static class VideoColors
{
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#A2A2A2"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#F0F0F0"));
    public static readonly IPen WavePen = new Pen(new SolidColorBrush(Color.Parse("#6B8299")), 1);
    public static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#4D4D4D")), 1);
    public static readonly IPen Border = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
    public static readonly IPen Picked = new Pen(new SolidColorBrush(Color.Parse("#FCC21B")), 2);
    // The video's end: red (BbError), not the playhead's yellow, which it was.
    public static readonly IBrush EndBrush = new SolidColorBrush(Color.Parse("#E06C6C"));
    public static readonly IPen End = new Pen(EndBrush, 1);
    public static readonly IBrush OnEnd = new SolidColorBrush(Color.Parse("#1A1A1A"));
    public static readonly IPen Hover = new Pen(Muted, 1, new DashStyle([3, 2], 0));
    public static readonly IBrush HoverBack = new SolidColorBrush(Color.Parse("#333333"));

    public static void Draw(DrawingContext context, string text, double x, double y, double size, IBrush brush, double maxWidth, IBrush? behind = null)
    {
        if (maxWidth < 12)
            return;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (behind is not null)
            context.FillRectangle(behind, new Rect(x - 4, y - 1, formatted.Width + 8, formatted.Height + 2), 3);
        context.DrawText(formatted, new Point(x, y));
    }
}

/// <summary>
/// The seconds along the top of the timeline, on the same scale as its rows: a tick a second (or
/// every few on a long video), a time every so often. Clicking or dragging on it moves the playhead.
/// </summary>
public sealed class VideoRuler : Control
{
    public static readonly StyledProperty<double> PpsProperty = AvaloniaProperty.Register<VideoRuler, double>(nameof(Pps), 64);
    public static readonly StyledProperty<double> LengthProperty = AvaloniaProperty.Register<VideoRuler, double>(nameof(Length));
    public static readonly StyledProperty<double> LeadProperty = AvaloniaProperty.Register<VideoRuler, double>(nameof(Lead), 12);

    /// <summary>Pixels a second.</summary>
    public double Pps { get => GetValue(PpsProperty); set => SetValue(PpsProperty, value); }

    /// <summary>How long the video is: where it ends is marked.</summary>
    public double Length { get => GetValue(LengthProperty); set => SetValue(LengthProperty, value); }

    /// <summary>Where the video's time starts, in pixels.</summary>
    public double Lead { get => GetValue(LeadProperty); set => SetValue(LeadProperty, value); }

    /// <summary>The user clicked or dragged on it: the time there.</summary>
    public event Action<double>? Seeked;

    static VideoRuler() => AffectsRender<VideoRuler>(PpsProperty, LengthProperty, LeadProperty);

    private bool _dragging;

    /// <summary>Where the pointer is over the ruler, in pixels; null when it isn't: its time is shown there.</summary>
    private double? _hover;

    /// <summary>A time on the ruler as the playhead's is written: 0:09.4.</summary>
    public static string TimeText(double seconds) => $"{TimeSpan.FromSeconds(Math.Max(0, seconds)):m\\:ss\\.f}";

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Seek(e);
        // Moving the playhead leaves what is picked as it is (a click on empty timeline picks the video).
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        _hover = e.GetPosition(this).X;
        InvalidateVisual();
        if (_dragging)
            Seek(e);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = false;
        e.Pointer.Capture(null);
    }

    private void Seek(PointerEventArgs e)
    {
        if (Pps > 0)
            Seeked?.Invoke(Math.Max(0, (e.GetPosition(this).X - Lead) / Pps));
    }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        if (Pps <= 0)
            return;
        // A tick each second when there is room, a time where it reads.
        var every = Pps >= 24 ? 1 : Pps >= 8 ? 5 : 10;
        var labelEvery = Pps >= 48 ? 2 : Pps >= 24 ? 5 : Pps >= 8 ? 10 : 30;
        for (var s = 0; Lead + s * Pps <= w; s += every)
        {
            var x = Math.Round(Lead + s * Pps) + 0.5;
            var label = s % labelEvery == 0;
            // Ticks up the row's height, a time's from near its top and the time at its top (the user,
            // 2026-10-31: in the 40-high row, short ticks and a centred time looked lost).
            context.DrawLine(VideoColors.Tick, new Point(x, label ? 4 : h - 14), new Point(x, h));
            if (label)
                VideoColors.Draw(context, $"{s / 60}:{s % 60:00}", x + 3, 3, 10, VideoColors.Muted, 60);
        }
        // The video's end, with how long it is beside it (the user, 2026-10-31, option 4 of the mockups);
        // the line goes on down the rows in the panel.
        if (Length > 0)
        {
            var end = Math.Round(Lead + Length * Pps) + 0.5;
            context.DrawLine(VideoColors.End, new Point(end, 0), new Point(end, h));
            var whole = (int)Math.Round(Length);
            var length = Math.Abs(Length - whole) < 0.05 ? $"{whole / 60}:{whole % 60:00}" : TimeText(Length);
            VideoColors.Draw(context, $"end {length}", end + 8, 22, 10, VideoColors.OnEnd, 80, VideoColors.EndBrush);
        }
        // The time under the pointer, as a dashed line and a time beside it, over the rest.
        if (_hover is { } at && at >= Lead)
        {
            var x = Math.Round(at) + 0.5;
            context.DrawLine(VideoColors.Hover, new Point(x, 0), new Point(x, h));
            VideoColors.Draw(context, TimeText((at - Lead) / Pps), x + 8, 22, 10, VideoColors.Text, 80, VideoColors.HoverBack);
        }
        context.DrawLine(VideoColors.Border, new Point(0, h - 0.5), new Point(w, h - 0.5));
    }
}

/// <summary>A sound's waveform inside its clip on a sound line: its loudness across the clip, as the part of it that plays.</summary>
public sealed class VideoWave : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty = AvaloniaProperty.Register<VideoWave, float[]?>(nameof(Peaks));

    /// <summary>Its loudness from where the clip starts to where it ends, 0 to 1; null while it isn't read.</summary>
    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    static VideoWave() => AffectsRender<VideoWave>(PeaksProperty);

    public VideoWave() => IsHitTestVisible = false;

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        if (Peaks is not { Length: > 0 } peaks || w <= 0)
            return;
        var middle = h / 2;
        for (var x = 0.5; x <= w; x += 2)
        {
            var at = (int)Math.Clamp(x / w * peaks.Length, 0, peaks.Length - 1);
            var half = Math.Max(0.5, peaks[at] * (h / 2 - 2));
            context.DrawLine(VideoColors.WavePen, new Point(x, middle - half), new Point(x, middle + half));
        }
    }
}