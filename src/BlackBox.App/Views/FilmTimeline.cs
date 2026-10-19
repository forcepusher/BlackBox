using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace BlackBox.App.Views;

/// <summary>The window's palette (App.axaml), for what the timeline draws itself.</summary>
internal static class FilmColors
{
    public static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B9199"));
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E6E8EB"));
    public static readonly IBrush Wave = new SolidColorBrush(Color.Parse("#8B9199"));
    public static readonly IBrush WaveFaint = new SolidColorBrush(Color.Parse("#4A4F56"));
    public static readonly IBrush Duck = new SolidColorBrush(Color.Parse("#66000000"));
    public static readonly IBrush Lane = new SolidColorBrush(Color.Parse("#1E2024"));
    public static readonly IBrush Label = new SolidColorBrush(Color.Parse("#C0111214"));
    public static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#4A4F56")), 1);
    public static readonly IPen Border = new Pen(new SolidColorBrush(Color.Parse("#2F3338")), 1);
    public static readonly IPen Picked = new Pen(new SolidColorBrush(Color.Parse("#E0A64B")), 2);
    public static readonly IPen End = new Pen(new SolidColorBrush(Color.Parse("#E0A64B")), 1);

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
/// every few on a long film), a time every so often. Clicking or dragging on it moves the playhead.
/// </summary>
public sealed class FilmRuler : Control
{
    public static readonly StyledProperty<double> PpsProperty = AvaloniaProperty.Register<FilmRuler, double>(nameof(Pps), 64);
    public static readonly StyledProperty<double> LengthProperty = AvaloniaProperty.Register<FilmRuler, double>(nameof(Length));
    public static readonly StyledProperty<double> LeadProperty = AvaloniaProperty.Register<FilmRuler, double>(nameof(Lead), 12);

    /// <summary>Pixels a second.</summary>
    public double Pps { get => GetValue(PpsProperty); set => SetValue(PpsProperty, value); }

    /// <summary>How long the film is: where it ends is marked.</summary>
    public double Length { get => GetValue(LengthProperty); set => SetValue(LengthProperty, value); }

    /// <summary>Where the film's time starts, in pixels.</summary>
    public double Lead { get => GetValue(LeadProperty); set => SetValue(LeadProperty, value); }

    /// <summary>The user clicked or dragged on it: the time there.</summary>
    public event Action<double>? Seeked;

    static FilmRuler() => AffectsRender<FilmRuler>(PpsProperty, LengthProperty, LeadProperty);

    private bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _dragging = true;
        e.Pointer.Capture(this);
        Seek(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
            Seek(e);
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
            context.DrawLine(FilmColors.Tick, new Point(x, h - (label ? 10 : 5)), new Point(x, h));
            if (label)
                FilmColors.Draw(context, $"{s / 60}:{s % 60:00}", x + 3, 2, 10, FilmColors.Muted, 60);
        }
        if (Length > 0)
        {
            var end = Math.Round(Lead + Length * Pps) + 0.5;
            context.DrawLine(FilmColors.End, new Point(end, 0), new Point(end, h));
        }
        context.DrawLine(FilmColors.Border, new Point(0, h - 0.5), new Point(w, h - 0.5));
    }
}

/// <summary>
/// The soundtrack's row: its waveform on the timeline's scale, quieter where the lines are said,
/// what it is written over it. Clicking it picks it.
/// </summary>
public sealed class FilmWave : Control
{
    public static readonly StyledProperty<float[]?> PeaksProperty = AvaloniaProperty.Register<FilmWave, float[]?>(nameof(Peaks));
    public static readonly StyledProperty<double> SecondsProperty = AvaloniaProperty.Register<FilmWave, double>(nameof(Seconds));
    public static readonly StyledProperty<double> PpsProperty = AvaloniaProperty.Register<FilmWave, double>(nameof(Pps), 64);
    public static readonly StyledProperty<double> LeadProperty = AvaloniaProperty.Register<FilmWave, double>(nameof(Lead), 12);
    public static readonly StyledProperty<double> LengthProperty = AvaloniaProperty.Register<FilmWave, double>(nameof(Length));
    public static readonly StyledProperty<IReadOnlyList<(double X, double Width)>?> DucksProperty =
        AvaloniaProperty.Register<FilmWave, IReadOnlyList<(double X, double Width)>?>(nameof(Ducks));
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<FilmWave, string?>(nameof(Label));
    public static readonly StyledProperty<bool> IsPickedProperty = AvaloniaProperty.Register<FilmWave, bool>(nameof(IsPicked));

    /// <summary>Its loudness from start to end, 0 to 1, ten a second; null with no sound.</summary>
    public float[]? Peaks { get => GetValue(PeaksProperty); set => SetValue(PeaksProperty, value); }

    /// <summary>How long the sound is.</summary>
    public double Seconds { get => GetValue(SecondsProperty); set => SetValue(SecondsProperty, value); }

    public double Pps { get => GetValue(PpsProperty); set => SetValue(PpsProperty, value); }
    public double Lead { get => GetValue(LeadProperty); set => SetValue(LeadProperty, value); }

    /// <summary>How long the film is: past it the sound is drawn faint.</summary>
    public double Length { get => GetValue(LengthProperty); set => SetValue(LengthProperty, value); }

    /// <summary>Where the lines are said, in pixels: the soundtrack is quieter there.</summary>
    public IReadOnlyList<(double X, double Width)>? Ducks { get => GetValue(DucksProperty); set => SetValue(DucksProperty, value); }

    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsPicked { get => GetValue(IsPickedProperty); set => SetValue(IsPickedProperty, value); }

    /// <summary>The user clicked it.</summary>
    public event Action? Picked;

    static FilmWave() => AffectsRender<FilmWave>(PeaksProperty, SecondsProperty, PpsProperty, LeadProperty, LengthProperty, DucksProperty, LabelProperty, IsPickedProperty);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Picked?.Invoke();
    }

    public override void Render(DrawingContext context)
    {
        var (w, h) = (Bounds.Width, Bounds.Height);
        var lane = new Rect(Lead, 0, Math.Max(0, w - Lead - 4), h);
        context.FillRectangle(FilmColors.Lane, lane, 4);
        if (Peaks is { Length: > 0 } peaks && Seconds > 0 && Pps > 0)
        {
            var middle = h / 2;
            var last = Lead + Seconds * Pps;
            for (var x = Lead + 0.5; x <= Math.Min(last, w); x += 2)
            {
                var t = (x - Lead) / Pps;
                var at = (int)Math.Clamp(t / Seconds * peaks.Length, 0, peaks.Length - 1);
                var half = Math.Max(0.5, peaks[at] * (h / 2 - 4));
                context.DrawLine(new Pen(t <= Length + 0.01 ? FilmColors.Wave : FilmColors.WaveFaint, 1), new Point(x, middle - half), new Point(x, middle + half));
            }
        }
        foreach (var (x, width) in Ducks ?? [])
            context.FillRectangle(FilmColors.Duck, new Rect(x, 0, width, h));
        if (Label is { Length: > 0 } label)
            FilmColors.Draw(context, label, Lead + 8, 5, 11, FilmColors.Text, Math.Min(w - Lead - 16, 900), FilmColors.Label);
        context.DrawRectangle(IsPicked ? FilmColors.Picked : FilmColors.Border, lane, 4);
    }
}
