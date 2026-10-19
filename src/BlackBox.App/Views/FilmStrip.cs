using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using BlackBox.App.ViewModels;

namespace BlackBox.App.Views;

/// <summary>
/// The film on one line, to read rather than edit: each shot as a block as long as it lasts (a
/// dissolve or fade overlapping the one before), seconds marked above, and the soundtrack's waveform
/// under it on the same scale, so it shows how the shots line up with the music and where the song
/// runs past the film's end or stops before it.
/// </summary>
public sealed class FilmStrip : Control
{
    public static readonly StyledProperty<FilmStripModel?> StripProperty = AvaloniaProperty.Register<FilmStrip, FilmStripModel?>(nameof(Strip));

    public FilmStripModel? Strip
    {
        get => GetValue(StripProperty);
        set => SetValue(StripProperty, value);
    }

    static FilmStrip() => AffectsRender<FilmStrip>(StripProperty);

    // The window's palette (App.axaml).
    private static readonly IBrush Raised = new SolidColorBrush(Color.Parse("#23262A"));
    private static readonly IBrush RaisedOdd = new SolidColorBrush(Color.Parse("#2C3035"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#E0A64B"));
    private static readonly IBrush AccentFaint = new SolidColorBrush(Color.Parse("#40E0A64B"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8B9199"));
    private static readonly IBrush Faint = new SolidColorBrush(Color.Parse("#4A4F56"));
    private static readonly IBrush Text = new SolidColorBrush(Color.Parse("#E6E6E6"));
    private static readonly IPen Border = new Pen(new SolidColorBrush(Color.Parse("#2F3338")), 1);
    private static readonly IPen Tick = new Pen(new SolidColorBrush(Color.Parse("#4A4F56")), 1);
    private static readonly IPen End = new Pen(new SolidColorBrush(Color.Parse("#E0A64B")), 1.5);

    private const double Ruler = 16, ShotRow = 34, Gap = 4;

    public override void Render(DrawingContext context)
    {
        var strip = Strip;
        var (w, h) = (Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h));
        if (strip is null || strip.Shots.Count == 0 || w < 40)
            return;
        // The scale covers the film, and the soundtrack where it runs on past it.
        var span = Math.Max(strip.Length, Math.Min(strip.SoundSeconds, strip.Length * 1.5));
        if (span <= 0)
            return;
        double X(double seconds) => Math.Round(seconds / span * (w - 1)) + 0.5;

        // Seconds along the top: a tick each second (or each five on a long film), a number every so often.
        var every = span > 120 ? 10 : span > 40 ? 5 : 1;
        var labelEvery = span > 120 ? 30 : span > 40 ? 10 : 5;
        for (var s = 0; s <= span; s += every)
        {
            var x = X(s);
            context.DrawLine(Tick, new Point(x, Ruler - (s % labelEvery == 0 ? 6 : 3)), new Point(x, Ruler));
            if (s % labelEvery == 0)
                Draw(context, $"{s} s", x + 3, 0, 10, Muted, w - x - 3);
        }

        // The shots, each from where it starts in the film for as long as it lasts.
        var top = Ruler + Gap;
        for (var i = 0; i < strip.Shots.Count; i++)
        {
            var shot = strip.Shots[i];
            var rect = new Rect(X(shot.Start), top, Math.Max(2, X(shot.Start + shot.Seconds) - X(shot.Start)), ShotRow);
            context.FillRectangle(i % 2 == 0 ? Raised : RaisedOdd, rect);
            if (shot.LipSync)
                context.FillRectangle(AccentFaint, rect);
            context.DrawRectangle(Border, rect);
            // A thin bar along the bottom once its clip is made.
            if (shot.Made)
                context.FillRectangle(Accent, new Rect(rect.X + 1, rect.Bottom - 3, rect.Width - 2, 2));
            using (context.PushClip(rect.Deflate(4)))
            {
                Draw(context, shot.Label, rect.X + 6, rect.Y + 3, 12, Text, rect.Width - 8);
                Draw(context, $"{shot.Seconds.ToString("0.#", CultureInfo.InvariantCulture)} s" + (shot.LipSync ? " · lip sync" : ""), rect.X + 6, rect.Y + 18, 10, Muted, rect.Width - 8);
            }
        }

        // The soundtrack's waveform on the same scale; past the film's end, faint.
        var waveTop = top + ShotRow + Gap;
        var waveHeight = Math.Max(10, h - waveTop);
        var middle = waveTop + waveHeight / 2;
        if (strip.Peaks is { Length: > 0 } peaks && strip.SoundSeconds > 0)
        {
            var last = X(Math.Min(strip.SoundSeconds, span));
            for (var x = 0.5; x <= last; x += 2)
            {
                var t = x / (w - 1) * span;
                var at = (int)Math.Clamp(t / strip.SoundSeconds * peaks.Length, 0, peaks.Length - 1);
                var half = Math.Max(0.5, peaks[at] * (waveHeight / 2 - 1));
                context.DrawLine(new Pen(t <= strip.Length ? Muted : Faint, 1), new Point(x, middle - half), new Point(x, middle + half));
            }
            if (strip.SoundSeconds + 0.05 < strip.Length)
                Draw(context, "the soundtrack ends here", X(strip.SoundSeconds) + 4, middle - 7, 10, Muted, w - X(strip.SoundSeconds) - 4);
        }
        else
            Draw(context, strip.SoundLabel ?? "No soundtrack: each shot keeps its own sound.", 0, middle - 7, 11, Muted, w);

        // Where the film ends.
        var endX = X(strip.Length);
        context.DrawLine(End, new Point(endX, Ruler), new Point(endX, h));
    }

    private static void Draw(DrawingContext context, string text, double x, double y, double size, IBrush brush, double maxWidth)
    {
        if (maxWidth < 12)
            return;
        var formatted = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(formatted, new Point(x, y));
    }
}
