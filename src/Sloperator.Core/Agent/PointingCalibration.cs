using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Sloperator.Core.Agent;

/// <summary>
/// Which coordinates a model points with, measured instead of guessed from its name (the user: no
/// model branching): at a chat's start the model is shown a picture of the screenshots' size with
/// three dots at known places and asked for their x/y in pixels. Some models answer on the 0-1000
/// grid they were trained on whatever they are asked: Nemotron 3.5 Super VL, told it clicks in
/// pixels of a 2560-wide screenshot, clicked a button at x 2468 at 974 (ClickAccuracyTests,
/// 30 October 2026). Answers that fit the grid far better than the pixels make the chat's clicks
/// (and its zoom rulers, find_text and pictures) speak the grid; anything else stays in pixels.
/// </summary>
public static partial class PointingCalibration
{
    /// <summary>The grid models point on.</summary>
    public const int Grid = 1000;

    /// <summary>The dots, at fractions of the picture: away from the middle, the edges and each other, none mirroring another.</summary>
    private static readonly (string Name, SKColor Color, double X, double Y)[] Dots =
    [
        ("red", new SKColor(220, 30, 30), 0.18, 0.27),
        ("green", new SKColor(30, 160, 60), 0.71, 0.64),
        ("blue", new SKColor(40, 70, 220), 0.43, 0.86),
    ];

    /// <summary>White, <paramref name="width"/> by <paramref name="height"/>, with the dots, as JPEG.</summary>
    public static byte[] Picture(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        var radius = Math.Max(6f, height / 60f);
        foreach (var dot in Dots)
        {
            using var paint = new SKPaint { Color = dot.Color, IsAntialias = true };
            canvas.DrawCircle((float)(dot.X * width), (float)(dot.Y * height), radius, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    public static string Question(int width, int height) =>
        $"This picture is {width}x{height} pixels. Give the x/y of the middle of the red, the green and the blue dot, in pixels of this picture " +
        "with (0, 0) its top-left corner, as JSON only: {\"red\": [x, y], \"green\": [x, y], \"blue\": [x, y]}";

    /// <summary>
    /// <see cref="Grid"/> when the answer's x/y lie on the 0-1000 grid over the picture and not on
    /// its pixels; null for pixels, and for an answer that fits neither or isn't there (a failed
    /// request), so nothing changes then.
    /// </summary>
    public static int? Read(string? answer, int width, int height) =>
        Read(answer, width, height, [.. Dots.Select(d => new Mark(d.Name, d.X, d.Y))]);

    /// <summary>Something to point at, at fractions of the picture: a dot, or a word read off a screenshot.</summary>
    public sealed record Mark(string Name, double X, double Y);

    /// <summary>As <see cref="Read(string?, int, int)"/>, for any marks, each answered as "name": [x, y].</summary>
    public static int? Read(string? answer, int width, int height, IReadOnlyList<Mark> marks) =>
        Offsets(answer, width, height, marks) is var (pixelOff, gridOff)
        // The grid only when it fits closely and much better: a picture about 1000 wide fits both.
        && gridOff < 0.06 && gridOff * 3 < pixelOff ? Grid : null;

    /// <summary>
    /// How far the answer's x/y are from the marks, on average as a share of the picture, read as
    /// pixels and as the grid; null when fewer than two marks were answered.
    /// </summary>
    public static (double Pixels, double Grid)? Offsets(string? answer, int width, int height, IReadOnlyList<Mark> marks)
    {
        if (answer is null)
            return null;
        double pixelOff = 0, gridOff = 0;
        var read = 0;
        foreach (var mark in marks)
        {
            if (Pair(mark.Name).Match(answer) is not { Success: true } m)
                continue;
            var x = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var y = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            // How far off each reading is, as a share of the picture.
            pixelOff += Math.Abs(x / width - mark.X) + Math.Abs(y / height - mark.Y);
            gridOff += Math.Abs(x / Grid - mark.X) + Math.Abs(y / Grid - mark.Y);
            read++;
        }
        return read < 2 ? null : (pixelOff / read, gridOff / read);
    }

    /// <summary>The question for <paramref name="marks"/> on a screenshot <paramref name="width"/> by <paramref name="height"/>.</summary>
    public static string Question(int width, int height, IReadOnlyList<Mark> marks) =>
        $"This screenshot is {width}x{height} pixels. Give the x/y of the middle of each of these words on it: " +
        string.Join(", ", marks.Select(m => $"\"{m.Name}\"")) + ". In pixels of this screenshot with (0, 0) its top-left corner, as JSON only: {" +
        string.Join(", ", marks.Select(m => $"\"{m.Name}\": [x, y]")) + "}";

    /// <summary>
    /// Three words read off a screenshot to point at: each on it once, of four letters or more, far
    /// from the middle and from each other, where a grid and a pixel reading differ most. Fewer than
    /// two when the screen has too little text.
    /// </summary>
    public static IReadOnlyList<Mark> Words(IReadOnlyList<Platform.TextLine> lines, int width, int height)
    {
        var words = lines.SelectMany(l => l.Words).ToList();
        var once = words
            .Where(w => w.Text.Length >= 4 && w.Text.All(char.IsLetter)
                && words.Count(o => string.Equals(o.Text, w.Text, StringComparison.OrdinalIgnoreCase)) == 1)
            .Select(w => new Mark(w.Text, (w.X + w.Width / 2) / width, (w.Y + w.Height / 2) / height))
            .ToList();
        var picked = new List<Mark>();
        // Farthest from the middle first, then each farthest from those picked.
        while (picked.Count < 3 && once.Count > 0)
        {
            var next = once.MaxBy(m => picked.Count == 0
                ? Math.Abs(m.X - 0.5) + Math.Abs(m.Y - 0.5)
                : picked.Min(p => Math.Abs(p.X - m.X) + Math.Abs(p.Y - m.Y)))!;
            picked.Add(next);
            once.Remove(next);
        }
        return picked;
    }

    private static Regex Pair(string name) =>
        new($@"{Regex.Escape(name)}""?\s*[:=]\s*[\[(]\s*(\d+(?:\.\d+)?)\s*,\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
}
