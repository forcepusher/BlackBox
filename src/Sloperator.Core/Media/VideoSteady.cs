using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SkiaSharp;

namespace Sloperator.Core.Media;

/// <summary>
/// Holds a camera still after the fact. LTX moves the camera though a prompt says it is locked off
/// (three shots of three in a night of videos pushed in, panned or tilted), and ffmpeg's tripod
/// stabilizer (vid.stab) neither undoes a push in nor found the background of a snowfield. Here the
/// camera's path is measured the way <see cref="VideoMotion"/> measures it (what most of the picture
/// does, a push in from the blocks that move with the camera), and each frame is moved and scaled
/// back to where the first one was, the picture enlarged just enough to hide the edges that brings in.
/// </summary>
public static partial class VideoSteady
{
    /// <summary>The most a picture is enlarged to hold the camera: past this, it is a different shot, not a steadier one.</summary>
    public const double MostEnlarged = 1.25;

    /// <summary>
    /// Where the camera went over a clip: for each frame (by its time), how far the picture has
    /// moved since the first frame, as a share of the width and height, and how much it has grown.
    /// </summary>
    public sealed record CameraPath(IReadOnlyList<(double Time, double X, double Y, double Grow)> Frames)
    {
        /// <summary>The farthest the picture moved, as a share of its width or height.</summary>
        public double MostMoved => Frames.Count == 0 ? 0 : Frames.Max(f => Math.Max(Math.Abs(f.X), Math.Abs(f.Y)));

        /// <summary>The most it grew or shrank, as a share (0.06: 6%).</summary>
        public double MostGrown => Frames.Count == 0 ? 0 : Frames.Max(f => Math.Abs(f.Grow - 1));

        /// <summary>Whether the camera moved enough for a viewer to see it: 1.5% across, or 2% in or out.</summary>
        public bool Moved => MostMoved >= 0.015 || MostGrown >= 0.02;

        /// <summary>How much the picture has to be enlarged so that held still it still fills the frame; above <see cref="MostEnlarged"/>, too much.</summary>
        public double Enlarged => Frames.Count == 0 ? 1 : Math.Max(1, Frames.Max(f =>
            Math.Max(1 - 2 * Math.Abs(f.X) <= 0 ? double.MaxValue : f.Grow / (1 - 2 * Math.Abs(f.X)),
                     1 - 2 * Math.Abs(f.Y) <= 0 ? double.MaxValue : f.Grow / (1 - 2 * Math.Abs(f.Y)))));

        /// <summary>The path at a time, between the frames measured.</summary>
        public (double X, double Y, double Grow) At(double time)
        {
            if (Frames.Count == 0)
                return (0, 0, 1);
            if (time <= Frames[0].Time)
                return (Frames[0].X, Frames[0].Y, Frames[0].Grow);
            for (var i = 1; i < Frames.Count; i++)
                if (time <= Frames[i].Time)
                {
                    var (a, b) = (Frames[i - 1], Frames[i]);
                    var t = (time - a.Time) / Math.Max(1e-6, b.Time - a.Time);
                    return (a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y), a.Grow + t * (b.Grow - a.Grow));
                }
            return (Frames[^1].X, Frames[^1].Y, Frames[^1].Grow);
        }

        /// <summary>"pans 4% of the width, pushes in 6%", for a note.</summary>
        public string Describe()
        {
            var last = Frames.Count == 0 ? (0.0, 0.0, 0.0, 1.0) : Frames[^1];
            var parts = new List<string>();
            if (MostMoved >= 0.015)
                parts.Add($"moves {N(MostMoved * 100)}% across");
            if (MostGrown >= 0.02)
                parts.Add($"{(last.Item4 >= 1 ? "pushes in" : "pulls back")} {N(MostGrown * 100)}%");
            return parts.Count == 0 ? "holds still" : string.Join(" and ", parts);
        }
    }

    /// <summary>
    /// The camera's path from a clip's small frames (<see cref="VideoMotion.Read"/>): steps of an
    /// eighth of a second measured and chained, a point at p in the first frame being at
    /// c + grow·(p − c) + shift in a later one.
    /// </summary>
    public static CameraPath Measure(IReadOnlyList<VideoMotion.Frame> frames)
    {
        var path = new List<(double, double, double, double)>();
        if (frames.Count == 0)
            return new CameraPath(path);
        var span = frames.Count < 2 ? 0.04 : Math.Max(0.01, (frames[^1].Time - frames[0].Time) / (frames.Count - 1));
        var k = Math.Max(1, (int)Math.Round(0.125 / span));
        var (x, y, grow) = (0.0, 0.0, 1.0);
        path.Add((frames[0].Time, 0, 0, 1));
        for (var i = k; i < frames.Count + k - 1; i += k)
        {
            var (a, b) = (frames[i - k], frames[Math.Min(i, frames.Count - 1)]);
            if (b == a)
                break;
            var step = VideoMotion.Between(a, b);
            // A step on top of the path so far: c + s·(c + g·(p − c) + d − c) + δ.
            (x, y, grow) = (step.Grow * x + step.ShiftX, step.Grow * y + step.ShiftY, step.Grow * grow);
            path.Add((b.Time, x / VideoMotion.Width, y / VideoMotion.Height, grow));
        }
        return new CameraPath(path);
    }

    /// <summary>Whether words for a shot ask for a camera that holds still.</summary>
    public static bool AsksStill(string words) => StillWords().IsMatch(words);

    [GeneratedRegex(@"\b(static|locked[- ]off|locked camera|still camera|tripod|camera (is )?(still|fixed|locked)|camera does not move|camera holds|holds still|^still\.?$)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex StillWords();

    /// <summary>
    /// The clip with the camera held where it starts, saved as <paramref name="output"/> (its sound
    /// kept). False when it can't be read, or the camera moved too far to hold without enlarging the
    /// picture past <see cref="MostEnlarged"/>.
    /// </summary>
    public static async Task<bool> HoldAsync(string clip, string output, CameraPath path, IMediaDecoder decoder, string ffmpeg, CancellationToken ct)
    {
        var enlarged = path.Enlarged;
        if (enlarged > MostEnlarged)
            return false;
        using var reader = decoder.OpenVideo(clip, 0);
        if (reader is null)
            return false;
        var (width, height) = (reader.Width, reader.Height);
        var fps = reader.FrameRate > 0 ? reader.FrameRate : 24;
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-y", "-f", "rawvideo", "-pix_fmt", "bgra", "-s", $"{width}x{height}", "-framerate", N(fps), "-i", "pipe:0",
                     "-i", clip, "-map", "0:v:0", "-map", "1:a?", "-c:v", "libx264", "-preset", "medium", "-crf", "16", "-pix_fmt", "yuv420p", "-c:a", "copy", "-shortest", output })
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new IOException($"ffmpeg ({ffmpeg}) didn't start.");
        var errors = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await Task.Run(() =>
            {
                using var held = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
                using var canvas = new SKCanvas(held);
                using var paint = new SKPaint { IsAntialias = true };
                var stdin = process.StandardInput.BaseStream;
                var (cx, cy) = (width / 2f, height / 2f);
                reader.Seek(TimeSpan.Zero);
                while (reader.Next() is { } next)
                {
                    ct.ThrowIfCancellationRequested();
                    using var frame = MediaPreview.ToBitmap(next.Frame);
                    var (x, y, grow) = path.At(next.Time.TotalSeconds);
                    // What was at p in the first frame is at c + grow·(p − c) + shift here: drawn back to p, enlarged.
                    canvas.Clear(SKColors.Black);
                    canvas.Save();
                    canvas.Translate(cx, cy);
                    canvas.Scale((float)(enlarged / grow));
                    canvas.Translate((float)(-cx - x * width), (float)(-cy - y * height));
                    canvas.DrawBitmap(frame, 0, 0, Sampling.High(grow, enlarged), paint);
                    canvas.Restore();
                    canvas.Flush();
                    stdin.Write(held.GetPixelSpan());
                }
                stdin.Flush();
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        await errors.ConfigureAwait(false);
        return process.ExitCode == 0 && File.Exists(output);
    }

    private static string N(double d) => d.ToString(d < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
}
