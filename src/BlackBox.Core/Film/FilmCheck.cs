using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BlackBox.Core.Media;
using SkiaSharp;

namespace BlackBox.Core.Film;

/// <summary>What a look at a shot or a film found: in words, with the sheet of frames it is based on.</summary>
/// <param name="Sheet">A JPEG: the storyboard's frames above the clip's (or each shot of the film), with their times.</param>
/// <param name="Faults">What is surely wrong (a clip that doesn't start on its frame, a black or frozen clip), to fix before anything else.</param>
public sealed record FilmLook(string Text, byte[]? Sheet, IReadOnlyList<string> Faults);

/// <summary>
/// Looks at what was made against the storyboard: whether a clip starts and ends on its frames, how
/// it moves (frozen, a sudden jump inside a shot), how long it is, whether it has sound; and for the
/// whole film, each shot side by side and the joins where the camera should go on. The sheet of
/// frames goes to a fresh look by the model (see <see cref="JudgeShot"/>), which judges what
/// numbers can't: whether the action happens, warped faces, the same people all through.
/// </summary>
public static partial class FilmCheck
{
    private const int SheetWidth = 1024, Small = 48;

    // ---- a shot ------------------------------------------------------------------------------------

    public static FilmLook Shot(Storyboard board, FilmShot shot, string clip, IMediaDecoder decoder)
    {
        var info = decoder.Probe(clip) ?? throw new FilmException($"{clip} can't be read on this PC (ffmpeg in BlackBox's Data\\tools would).");
        var text = new StringBuilder();
        var faults = new List<string>();
        text.AppendLine($"Shot {shot.Id} ({Path.GetFileName(clip)}): {info.Describe()}; the storyboard gives it {N(shot.Seconds)} s.");
        if (info.Duration.TotalSeconds + 0.25 < shot.Seconds)
            text.AppendLine($"It is {N(shot.Seconds - info.Duration.TotalSeconds)} s shorter than its shot: the film holds its last frame for the rest.");
        using var reader = decoder.OpenVideo(clip, 512) ?? throw new FilmException($"{clip} has no picture this PC can read.");
        var frames = Frames(reader, info, 16);
        try
        {
            if (frames.Count == 0)
                throw new FilmException($"No frame of {clip} could be read.");
            var first = board.Frame(shot.First);
            var last = shot.LipSync ? null : board.Frame(shot.Last);
            var firstPicture = first is null ? null : board.FramePicture(first);
            var lastPicture = last is null ? null : board.FramePicture(last);
            using var start = firstPicture is not null && File.Exists(firstPicture) ? SKBitmap.Decode(firstPicture) : null;
            using var end = lastPicture is not null && File.Exists(lastPicture) ? SKBitmap.Decode(lastPicture) : null;
            if (start is not null)
            {
                var alike = Alike(start, frames[0].Frame);
                text.AppendLine($"Its first frame is {Words(alike)} {first!.Label} ({alike:0.00} alike).");
                if (alike < 0.5)
                    faults.Add($"it doesn't start on {first.Label}");
            }
            if (end is not null)
            {
                var alike = Alike(end, frames[^1].Frame);
                text.AppendLine($"Its last frame is {Words(alike)} {last!.Label} ({alike:0.00} alike).");
                if (alike < 0.5)
                    faults.Add($"it doesn't end on {last.Label}");
            }
            DescribeMotion(frames, text, faults);
            if (info.HasAudio && decoder.OpenAudio(clip) is { } sound)
                using (sound)
                {
                    var (mono, rate) = MediaPreview.ReadMono(sound, TimeSpan.FromMinutes(2));
                    if (mono.Length > 0)
                        text.AppendLine("Its sound: " + SoundStats.Of(mono, rate).Describe() + ".");
                }
            var sheet = ShotSheet(start, end, first?.Label, last?.Label, frames, reader.Width, reader.Height);
            return new FilmLook(text.ToString().TrimEnd(), sheet, faults);
        }
        finally
        {
            foreach (var (_, bitmap) in frames)
                bitmap.Dispose();
        }
    }

    /// <summary>What a fresh look is asked about a shot, with its sheet.</summary>
    public static string JudgeShot(Storyboard board, FilmShot shot)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You check one shot of a film for the person making it, from a sheet of frames.");
        About(board, sb);
        sb.AppendLine($"This shot lasts {N(shot.Seconds)} s and should show: {shot.Prompt.Trim()}");
        if (shot.Camera.Trim().Length > 0)
            sb.AppendLine($"The camera: {shot.Camera.Trim()}.");
        var first = board.Frame(shot.First);
        var last = shot.LipSync ? null : board.Frame(shot.Last);
        sb.AppendLine(first is null && last is null
            ? "The picture shows frames of the clip that was made, in order, with their times."
            : "In the picture, the top row is the storyboard: " +
              (first is not null ? "the frame the shot should start on" : "") + (first is not null && last is not null ? " (left) and " : "") +
              (last is not null ? "the frame it should end on" : "") + (first is not null && last is not null ? " (right)" : "") +
              ". Under it are frames of the clip that was made, in order, with their times.");
        if (Storyboard.Speaks(shot))
            sb.AppendLine($"The person speaking in it says \"{shot.Line.Trim()}\": their mouth should move with the words, and they should look like they say them.");
        else if (shot.LipSync)
            sb.AppendLine("The people in it should sing or speak along with the soundtrack, their mouths moving.");
        sb.AppendLine("Say briefly and concretely:");
        sb.AppendLine("1. Does what should happen happen, clearly?");
        sb.AppendLine("2. Faults: warped or melting faces, hands, limbs or bodies; people or things appearing, vanishing or changing; flicker; garbled text.");
        sb.AppendLine("3. Do the people, their clothes, the things and the place stay the same as in the storyboard's frames and all through the clip?");
        sb.AppendLine("4. Does the camera do what was asked?");
        sb.AppendLine("Ask for a redo only for what spoils the shot for a viewer: the action not happening, a warped face or body, a person or thing that changes. " +
                      "Small things (a detail that leaves the frame as the camera moves, a pose a little off) are worth saying, but no reason to redo it.");
        sb.Append("Then end with exactly one line: VERDICT: good, or VERDICT: redo: and the main reason.");
        return sb.ToString();
    }

    // ---- the whole film --------------------------------------------------------------------------------

    public static FilmLook Film(Storyboard board, string film, IMediaDecoder decoder)
    {
        var info = decoder.Probe(film) ?? throw new FilmException($"{film} can't be read on this PC.");
        var (starts, length) = board.Timing();
        var text = new StringBuilder();
        var faults = new List<string>();
        text.AppendLine($"The film ({Path.GetFileName(film)}): {info.Describe()}; the storyboard makes it {N(length)} s in {board.Shots.Count} shots.");
        if (Math.Abs(info.Duration.TotalSeconds - length) > 1)
            faults.Add($"it is {N(info.Duration.TotalSeconds)} s long where the storyboard makes it {N(length)} s");
        using var reader = decoder.OpenVideo(film, 512) ?? throw new FilmException($"{film} has no picture this PC can read.");
        var middles = new List<(TimeSpan Time, SKBitmap Frame)>();
        try
        {
            for (var i = 0; i < board.Shots.Count; i++)
                if (FrameAt(reader, starts[i] + board.Shots[i].Seconds / 2) is { } frame)
                    middles.Add(frame);
            // Where the camera should go on from one shot into the next, the picture should too.
            for (var i = 1; i < board.Shots.Count; i++)
            {
                var (before, after) = (board.Shots[i - 1], board.Shots[i]);
                if (after.First is null || after.First != before.Last || after.Transition != FilmShot.Cut)
                    continue;
                using var a = FrameAt(reader, starts[i] - 0.06)?.Frame;
                using var b = FrameAt(reader, starts[i] + 0.06)?.Frame;
                if (a is null || b is null)
                    continue;
                var alike = Alike(a, b);
                text.AppendLine($"Shot {after.Id} goes on from shot {before.Id} on {board.Frame(after.First)!.Label}: the join is {(alike >= 0.7 ? "smooth" : alike >= 0.5 ? "a small jump" : "a jump")} ({alike:0.00} alike).");
                if (alike < 0.5)
                    faults.Add($"a jump between shots {before.Id} and {after.Id}, where the camera should go on");
            }
            if (info.HasAudio && decoder.OpenAudio(film) is { } sound)
                using (sound)
                {
                    var (mono, rate) = MediaPreview.ReadMono(sound, TimeSpan.FromMinutes(20));
                    if (mono.Length > 0)
                        text.AppendLine("Its sound: " + SoundStats.Of(mono, rate).Describe() + ".");
                }
            else
                text.AppendLine("It has no sound.");
            var labels = board.Shots.Select((s, i) => $"{s.Id} at {N(starts[i] + s.Seconds / 2)} s").ToList();
            var sheet = middles.Count == 0 ? null : Grid(middles.Select((m, i) => (i < labels.Count ? labels[i] : "", m.Frame)).ToList(), reader.Width, reader.Height);
            return new FilmLook(text.ToString().TrimEnd(), sheet, faults);
        }
        finally
        {
            foreach (var (_, bitmap) in middles)
                bitmap.Dispose();
        }
    }

    public static string JudgeFilm(Storyboard board)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You check a short film for the person making it, from a sheet with one frame from the middle of each shot, in order, labelled with the shot and its time.");
        About(board, sb);
        sb.AppendLine("The shots should show:");
        foreach (var s in board.Shots)
            sb.AppendLine($"- {s.Id}: {s.Prompt.Trim().ReplaceLineEndings(" ")}");
        sb.AppendLine("Say briefly and concretely: do the same people look the same in every shot (face, hair, clothes, colours)? Does the look stay the same? " +
                      "Does each shot show what it should, and do they tell the story in order? Which shots stand out as wrong, and why?");
        sb.AppendLine("Ask for a redo only for what a viewer would clearly notice: someone who looks like another person or animal, a place that changes where it should be the same, " +
                      "a shot that doesn't show its action. A part of someone out of frame in a close-up, or small differences in pose and framing, are no reason to redo a shot.");
        sb.Append("Then end with exactly one line: VERDICT: good, or VERDICT: redo shots s1, s2…: and the main reason.");
        return sb.ToString();
    }

    /// <summary>The verdict at the end of a fresh look's answer: good, redo, or null when it gave none.</summary>
    public static string? Verdict(string answer) =>
        VerdictLine().Matches(answer).LastOrDefault() is { } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    [GeneratedRegex(@"VERDICT\s*:\s*\**\s*(good|redo)", RegexOptions.IgnoreCase)]
    private static partial Regex VerdictLine();

    // ---- frames and how alike they are ------------------------------------------------------------------

    /// <summary>
    /// How alike two pictures are, 0 to 1: their brightness patterns' correlation and how near
    /// their colours are, at a small size (where a re-encode or a slight shift doesn't count).
    /// </summary>
    public static double Alike(SKBitmap a, SKBitmap b)
    {
        // Both at the first one's shape: a clip made at 1280x704 from a 1280x720 frame is the same picture.
        var height = SmallHeight(a);
        var (x, y) = (Shrink(a, height), Shrink(b, height));
        double meanX = x.Gray.Average(), meanY = y.Gray.Average(), sxy = 0, sxx = 0, syy = 0, colour = 0;
        for (var i = 0; i < x.Gray.Length; i++)
        {
            var (dx, dy) = (x.Gray[i] - meanX, y.Gray[i] - meanY);
            sxy += dx * dy;
            sxx += dx * dx;
            syy += dy * dy;
        }
        for (var i = 0; i < x.Rgb.Length; i++)
            colour += Math.Abs(x.Rgb[i] - y.Rgb[i]);
        var correlation = sxx < 1e-6 || syy < 1e-6 ? (Math.Abs(meanX - meanY) < 8 ? 1 : 0) : sxy / Math.Sqrt(sxx * syy);
        var near = 1 - colour / x.Rgb.Length / 255.0 * 2.5;
        return Math.Clamp(0.65 * Math.Max(0, correlation) + 0.35 * Math.Clamp(near, 0, 1), 0, 1);
    }

    private static int SmallHeight(SKBitmap source) => Math.Clamp((int)Math.Round(Small * (double)source.Height / Math.Max(1, source.Width)), 8, Small);

    /// <summary>A picture at <see cref="Small"/> pixels wide, in grey and in colour.</summary>
    private static (double[] Gray, double[] Rgb) Shrink(SKBitmap source, int? height = null)
    {
        var (w, h) = (Small, height ?? SmallHeight(source));
        using var small = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(small))
        using (var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium })
            canvas.DrawBitmap(source, new SKRect(0, 0, w, h), paint);
        var gray = new double[w * h];
        var rgb = new double[w * h * 3];
        for (var yy = 0; yy < h; yy++)
            for (var xx = 0; xx < w; xx++)
            {
                var c = small.GetPixel(xx, yy);
                var i = yy * w + xx;
                gray[i] = 0.299 * c.Red + 0.587 * c.Green + 0.114 * c.Blue;
                (rgb[i * 3], rgb[i * 3 + 1], rgb[i * 3 + 2]) = (c.Red, c.Green, c.Blue);
            }
        return (gray, rgb);
    }

    private static string Words(double alike) => alike >= 0.85 ? "the same as" : alike >= 0.7 ? "close to" : alike >= 0.5 ? "loosely like" : "not like";

    /// <summary><paramref name="count"/> frames from the first to the last, with their times.</summary>
    private static List<(TimeSpan Time, SKBitmap Frame)> Frames(IVideoReader reader, MediaInfo info, int count)
    {
        var frames = new List<(TimeSpan, SKBitmap)>();
        var seconds = Math.Max(info.Duration.TotalSeconds, reader.Duration.TotalSeconds);
        var step = reader.FrameRate > 0 ? 1 / reader.FrameRate : 1 / 24.0;
        for (var i = 0; i < count; i++)
        {
            // The very first frame, then evenly to the last one.
            var t = i == 0 ? 0 : Math.Max(0, (seconds - step * 1.5) * i / (count - 1));
            if (FrameAt(reader, t, first: i == 0) is { } frame)
                frames.Add(frame);
        }
        // The last frame is the last one there is: read on to the end.
        if (frames.Count > 0)
        {
            (TimeSpan, SKBitmap)? last = null;
            reader.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds - step * 3)));
            while (reader.Next() is { } next)
            {
                last?.Item2.Dispose();
                last = (next.Time, MediaPreview.ToBitmap(next.Frame));
            }
            if (last is { } end && end.Item1 > frames[^1].Item1)
                frames.Add(end);
            else
                last?.Item2.Dispose();
        }
        return frames;
    }

    private static (TimeSpan Time, SKBitmap Frame)? FrameAt(IVideoReader reader, double seconds, bool first = false)
    {
        if (!first)
            reader.Seek(TimeSpan.FromSeconds(Math.Max(0, seconds)));
        else
            reader.Seek(TimeSpan.Zero);
        return reader.Next() is { } frame ? (frame.Time, MediaPreview.ToBitmap(frame.Frame)) : null;
    }

    /// <summary>How the clip moves, from how much each frame differs from the one before: frozen, or a sudden jump.</summary>
    private static void DescribeMotion(List<(TimeSpan Time, SKBitmap Frame)> frames, StringBuilder text, List<string> faults)
    {
        if (frames.Count < 3)
            return;
        var shrunk = frames.Select(f => Shrink(f.Frame).Gray).ToList();
        var dark = shrunk.Count(g => g.Average() < 12);
        if (dark == shrunk.Count)
        {
            faults.Add("it is black all through");
            text.AppendLine("It is black all through.");
            return;
        }
        if (dark > 0)
            text.AppendLine($"{dark} of the {shrunk.Count} frames looked at are black.");
        var changes = new List<double>();
        for (var i = 1; i < shrunk.Count; i++)
            changes.Add(shrunk[i].Zip(shrunk[i - 1], (a, b) => Math.Abs(a - b)).Average());
        var ordered = changes.Order().ToList();
        var median = ordered[ordered.Count / 2];
        var total = changes.Sum();
        if (total < 4)
        {
            text.AppendLine("It hardly moves: almost the same picture all through.");
            faults.Add("it hardly moves");
            return;
        }
        var biggest = changes.Select((c, i) => (Change: c, At: frames[i + 1].Time)).MaxBy(c => c.Change);
        if (biggest.Change > 18 && biggest.Change > 4 * Math.Max(median, 1.5))
        {
            text.AppendLine($"It changes suddenly at about {biggest.At.TotalSeconds:0.0} s, far more than anywhere else: a cut or a jump inside the shot.");
            faults.Add($"a sudden jump at {biggest.At.TotalSeconds:0.0} s");
        }
        else
            text.AppendLine(total / changes.Count < 2.5 ? "It moves gently all through." : "It moves all through, without sudden jumps.");
    }

    // ---- sheets ---------------------------------------------------------------------------------------------

    private static byte[] ShotSheet(SKBitmap? start, SKBitmap? end, string? startLabel, string? endLabel, List<(TimeSpan Time, SKBitmap Frame)> frames, int width, int height)
    {
        // Eight of the clip's frames, the first and last among them.
        var picks = Enumerable.Range(0, Math.Min(8, frames.Count)).Select(i => frames[(int)Math.Round(i * (frames.Count - 1) / (double)Math.Max(1, Math.Min(8, frames.Count) - 1))]).ToList();
        var cells = picks.Select(p => ($"{p.Time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s", p.Frame)).ToList();
        var story = new List<(string, SKBitmap)>();
        if (start is not null) story.Add(($"storyboard: start ({startLabel})", start));
        if (end is not null) story.Add(($"storyboard: end ({endLabel})", end));
        using var top = story.Count == 0 ? null : GridBitmap(story, width, height, columns: 2);
        using var bottom = GridBitmap(cells, width, height, columns: 4);
        var gap = top is null ? 0 : 12;
        using var sheet = new SKBitmap(SheetWidth, (top?.Height ?? 0) + gap + bottom.Height);
        using (var canvas = new SKCanvas(sheet))
        {
            canvas.Clear(new SKColor(20, 20, 20));
            if (top is not null)
            {
                canvas.DrawBitmap(top, 0, 0);
                using var line = new SKPaint { Color = new SKColor(224, 166, 75) };
                canvas.DrawRect(0, top.Height + 4, SheetWidth, 4, line);
            }
            canvas.DrawBitmap(bottom, 0, (top?.Height ?? 0) + gap);
        }
        return Jpeg(sheet);
    }

    private static byte[] Grid(List<(string Label, SKBitmap Frame)> cells, int width, int height)
    {
        using var grid = GridBitmap(cells, width, height, columns: cells.Count <= 4 ? Math.Max(1, cells.Count) : cells.Count <= 9 ? 3 : 4);
        return Jpeg(grid);
    }

    /// <summary>Pictures in a grid, each with its label in a corner, <see cref="SheetWidth"/> wide.</summary>
    private static SKBitmap GridBitmap(IReadOnlyList<(string Label, SKBitmap Frame)> cells, int width, int height, int columns)
    {
        columns = Math.Clamp(columns, 1, Math.Max(1, cells.Count));
        var rows = (cells.Count + columns - 1) / columns;
        var cellW = (SheetWidth - (columns - 1) * 4) / columns;
        var cellH = Math.Max(16, (int)Math.Round(cellW * (double)height / Math.Max(1, width)));
        var bitmap = new SKBitmap(SheetWidth, rows * cellH + (rows - 1) * 4);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(20, 20, 20));
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, Math.Clamp(cellW / 16f, 13, 22));
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = font.Size, Typeface = font.Typeface };
        using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 180) };
        for (var i = 0; i < cells.Count; i++)
        {
            var (x, y) = (i % columns * (cellW + 4), i / columns * (cellH + 4));
            var frame = cells[i].Frame;
            // Covering the cell, cut in the middle, as the film shows it.
            var scale = Math.Max(cellW / (double)frame.Width, cellH / (double)frame.Height);
            var (w, h) = ((float)(frame.Width * scale), (float)(frame.Height * scale));
            canvas.Save();
            canvas.ClipRect(new SKRect(x, y, x + cellW, y + cellH));
            canvas.DrawBitmap(frame, new SKRect(x + (cellW - w) / 2, y + (cellH - h) / 2, x + (cellW + w) / 2, y + (cellH + h) / 2), paint);
            canvas.Restore();
            var label = cells[i].Label;
            var labelW = text.MeasureText(label) + 10;
            canvas.DrawRect(x, y + cellH - font.Size - 8, labelW, font.Size + 8, shade);
            canvas.DrawText(label, x + 5, y + cellH - 6, font, text);
        }
        return bitmap;
    }

    private static byte[] Jpeg(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>The film's title (what it is about and its look are in each frame's and shot's prompt).</summary>
    private static void About(Storyboard board, StringBuilder sb)
    {
        if (board.Title.Trim().Length > 0)
            sb.AppendLine($"The film is called \"{board.Title.Trim()}\".");
    }
}
