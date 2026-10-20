using System.Globalization;
using System.Text;

namespace BlackBox.Core.Media;

/// <summary>
/// How things move in a clip, measured from its pixels: the whole picture moving (the camera
/// panning, tilting, pushing in) and what moves on its own, in which part of the picture, which way
/// and how far. The fresh looks judge motion from 8 or 12 still frames, and the user found them
/// often blind to motion gone wrong (the wrong way, a body bending wrong, a prop moving by itself);
/// a model reads "the middle moves up 9% of the height while the rest stays" where it can't see a
/// fox float between two frames a second apart.
/// </summary>
public static class VideoMotion
{
    /// <summary>The size frames are measured at: small enough to be quick, big enough for a block to hold detail.</summary>
    public const int Width = 160, Height = 90;

    private const int Block = 10, Radius = 8, Columns = Width / Block, Rows = Height / Block;

    /// <summary>A frame at <see cref="Width"/> x <see cref="Height"/>, grey, and its time in seconds; its brightness was <paramref name="Mean"/>.</summary>
    public sealed record Frame(double Time, float[] Gray, float Mean = 100);

    /// <summary>
    /// A clip's frames from its start, small and grey, each measured against its own brightness
    /// (100 is its mean), so a frame that brightens as a whole doesn't count as movement.
    /// </summary>
    public static List<Frame> Read(IVideoReader reader, int limit = 24 * 60)
    {
        var frames = new List<Frame>();
        reader.Seek(TimeSpan.Zero);
        while (frames.Count < limit && reader.Next() is { } next)
        {
            using var bitmap = MediaPreview.ToBitmap(next.Frame);
            using var small = bitmap.Resize(new SkiaSharp.SKImageInfo(Width, Height), SkiaSharp.SKFilterQuality.Low);
            if (small is null)
                continue;
            var gray = new float[Width * Height];
            var pixels = small.Pixels;
            float sum = 0;
            for (var i = 0; i < gray.Length; i++)
                sum += gray[i] = 0.299f * pixels[i].Red + 0.587f * pixels[i].Green + 0.114f * pixels[i].Blue;
            var mean = sum / gray.Length;
            for (var i = 0; i < gray.Length; i++)
                gray[i] = gray[i] / Math.Max(mean, 4) * 100;
            frames.Add(new Frame(next.Time.TotalSeconds, gray, mean));
        }
        return frames;
    }

    /// <summary>
    /// The movement from one frame to another, in pixels at <see cref="Width"/> x <see cref="Height"/>:
    /// the whole picture's shift and growth (1 for none), and what moves against it in each ninth of
    /// the picture (0 to 8, left to right, top to bottom), with how many blocks of it move.
    /// </summary>
    public sealed record Step(double From, double To, double ShiftX, double ShiftY, double Grow, IReadOnlyDictionary<int, (double X, double Y, int Blocks)> Moving);

    /// <summary>The movement between frames about <paramref name="every"/> seconds apart.</summary>
    public static IReadOnlyList<Step> Measure(IReadOnlyList<Frame> frames, double every = 0.25)
    {
        var steps = new List<Step>();
        if (frames.Count < 2)
            return steps;
        var span = Math.Max(0.01, (frames[^1].Time - frames[0].Time) / Math.Max(1, frames.Count - 1));
        var k = Math.Max(1, (int)Math.Round(every / span));
        for (var i = 0; i + k < frames.Count; i += k)
            steps.Add(Between(frames[i], frames[i + k]));
        return steps;
    }

    /// <summary>The movement from one frame to the next: each textured block found again nearby, the whole picture's share taken out.</summary>
    internal static Step Between(Frame a, Frame b)
    {
        var found = new List<(double Cx, double Cy, double Dx, double Dy)>();
        for (var by = 0; by < Rows; by++)
            for (var bx = 0; bx < Columns; bx++)
                if (Match(a.Gray, b.Gray, bx * Block, by * Block) is { } d)
                    found.Add((bx * Block + Block / 2.0, by * Block + Block / 2.0, d.Dx, d.Dy));
        if (found.Count < 6)
            return new Step(a.Time, b.Time, 0, 0, 1, new Dictionary<int, (double, double, int)>());
        // The camera: what most of the picture does (a moving subject is the smaller part).
        var shiftX = Median(found.Select(f => f.Dx));
        var shiftY = Median(found.Select(f => f.Dy));
        var (cx, cy) = (Width / 2.0, Height / 2.0);
        double num = 0, den = 0;
        foreach (var f in found)
        {
            var (px, py) = (f.Cx - cx, f.Cy - cy);
            num += px * (f.Dx - shiftX) + py * (f.Dy - shiftY);
            den += px * px + py * py;
        }
        var scale = den > 0 ? num / den : 0;
        // A growth as big as most blocks' own movement is a push in, not a guess from a few blocks moving out.
        var residuals = found.Select(f => (f, Rx: f.Dx - shiftX - scale * (f.Cx - cx), Ry: f.Dy - shiftY - scale * (f.Cy - cy))).ToList();
        var steady = residuals.Count(r => Math.Abs(r.Rx) < 1 && Math.Abs(r.Ry) < 1);
        if (steady < residuals.Count / 2)
        {
            scale = 0;
            residuals = found.Select(f => (f, Rx: f.Dx - shiftX, Ry: f.Dy - shiftY)).ToList();
        }
        var moving = new Dictionary<int, (double X, double Y, int Blocks)>();
        foreach (var group in residuals.Where(r => Math.Sqrt(r.Rx * r.Rx + r.Ry * r.Ry) >= 1.0)
                     .GroupBy(r => Math.Min(2, (int)(r.f.Cy / Height * 3)) * 3 + Math.Min(2, (int)(r.f.Cx / Width * 3))))
            if (group.Count() >= 2)
                moving[group.Key] = (group.Average(r => r.Rx), group.Average(r => r.Ry), group.Count());
        return new Step(a.Time, b.Time, shiftX, shiftY, 1 + scale, moving);
    }

    /// <summary>Where a block of one frame is in the next, to a tenth of a pixel; null for a block too plain to tell.</summary>
    private static (double Dx, double Dy)? Match(float[] a, float[] b, int x0, int y0)
    {
        double sum = 0, squares = 0;
        for (var y = y0; y < y0 + Block; y++)
            for (var x = x0; x < x0 + Block; x++)
            {
                var v = a[y * Width + x];
                sum += v;
                squares += v * v;
            }
        var n = Block * Block;
        var mean = sum / n;
        if (squares / n - mean * mean < 9 * 9)
            return null; // sky, a plain wall: anywhere matches
        var size = 2 * Radius + 1;
        var costs = new double[size * size];
        var (best, bestCost) = ((0, 0), double.MaxValue);
        for (var dy = -Radius; dy <= Radius; dy++)
            for (var dx = -Radius; dx <= Radius; dx++)
            {
                var cost = double.MaxValue;
                if (x0 + dx >= 0 && y0 + dy >= 0 && x0 + dx + Block <= Width && y0 + dy + Block <= Height)
                {
                    cost = 0;
                    for (var y = 0; y < Block; y++)
                    {
                        var ra = (y0 + y) * Width + x0;
                        var rb = (y0 + y + dy) * Width + x0 + dx;
                        for (var x = 0; x < Block; x++)
                            cost += Math.Abs(a[ra + x] - b[rb + x]);
                    }
                }
                costs[(dy + Radius) * size + dx + Radius] = cost;
                if (cost < bestCost || cost == bestCost && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(best.Item1) + Math.Abs(best.Item2))
                    (best, bestCost) = ((dx, dy), cost);
            }
        if (bestCost == double.MaxValue)
            return null;
        // A match no better than plenty of others (a repeating pattern, noise) says nothing.
        var sorted = costs.Where(c => c < double.MaxValue).Order().ToList();
        if (sorted.Count > 4 && sorted[Math.Min(sorted.Count - 1, 4)] - bestCost < 0.05 * bestCost && Math.Abs(best.Item1) + Math.Abs(best.Item2) > 0)
            return null;
        double Sub(int d, Func<int, double> at) =>
            d <= -Radius || d >= Radius ? d
            : at(d - 1) is var l && at(d + 1) is var r && l < double.MaxValue && r < double.MaxValue && l + r - 2 * at(d) > 0 ? d + 0.5 * (l - r) / (l + r - 2 * at(d)) : d;
        var (bx, by) = best;
        return (Sub(bx, d => costs[(by + Radius) * size + d + Radius]), Sub(by, d => costs[(d + Radius) * size + bx + Radius]));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static readonly string[] Places = ["the upper left", "the top middle", "the upper right", "the left", "the middle", "the right", "the lower left", "the bottom middle", "the lower right"];

    /// <summary>
    /// The movement in words, for a fresh look: in stretches of half a second (a second for a long
    /// clip), what the whole picture does and what moves on its own where, and over the whole clip.
    /// Empty when nothing could be measured.
    /// </summary>
    public static string Describe(IReadOnlyList<Step> steps)
    {
        if (steps.Count == 0)
            return "";
        var length = steps[^1].To - steps[0].From;
        var window = length > 7 ? 1.0 : 0.5;
        var sb = new StringBuilder();
        sb.AppendLine("Measured movement (from the pixels, not the prompt; left, right, up and down as seen on screen; how far as a share of the frame's width or height):");
        for (var start = steps[0].From; start < steps[^1].To - 0.05; start += window)
        {
            var part = steps.Where(s => s.From >= start - 1e-6 && s.From < start + window - 1e-6).ToList();
            if (part.Count > 0)
                sb.AppendLine($"- {N(start)}–{N(Math.Min(part[^1].To, steps[^1].To))} s: {Said(part, 1.0)}");
        }
        sb.Append($"Over the whole clip: {Said(steps, 2.0)}");
        return sb.ToString();
    }

    /// <summary>What happens over some steps, in words; <paramref name="least"/> is the least share (in %) worth saying.</summary>
    private static string Said(IReadOnlyList<Step> steps, double least)
    {
        var parts = new List<string>();
        var (x, y) = (steps.Sum(s => s.ShiftX) / Width * 100, steps.Sum(s => s.ShiftY) / Height * 100);
        var grow = (steps.Aggregate(1.0, (g, s) => g * s.Grow) - 1) * 100;
        if (Math.Abs(x) >= least || Math.Abs(y) >= least)
            parts.Add("the whole picture moves " + Way(x, y) + (Math.Abs(x) >= Math.Abs(y) ? x < 0 ? " (as when the camera pans right)" : " (as when the camera pans left)" : y < 0 ? " (as when the camera tilts down)" : " (as when the camera tilts up)"));
        if (Math.Abs(grow) >= least * 2)
            parts.Add($"the whole picture {(grow > 0 ? "grows" : "shrinks")} by {N(Math.Abs(grow))}% (as when the camera {(grow > 0 ? "pushes in" : "pulls back")})");
        for (var place = 0; place < 9; place++)
        {
            var (mx, my) = (steps.Sum(s => s.Moving.TryGetValue(place, out var m) ? m.X : 0) / Width * 100, steps.Sum(s => s.Moving.TryGetValue(place, out var m) ? m.Y : 0) / Height * 100);
            if (Math.Abs(mx) >= least || Math.Abs(my) >= least)
                parts.Add($"in {Places[place]} something moves {Way(mx, my)} against the rest");
        }
        return parts.Count == 0 ? "nothing moves much." : string.Join("; ", parts) + ".";
    }

    /// <summary>"up 6% of the height", "left 3% of the width and down 2% of the height".</summary>
    private static string Way(double x, double y)
    {
        var h = $"{(x < 0 ? "left" : "right")} {N(Math.Abs(x))}% of the width";
        var v = $"{(y < 0 ? "up" : "down")} {N(Math.Abs(y))}% of the height";
        return Math.Abs(x) < 0.5 ? v : Math.Abs(y) < 0.5 ? h : Math.Abs(x) >= Math.Abs(y) ? h + " and " + v : v + " and " + h;
    }

    private static string N(double d) => d.ToString(d < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
}
