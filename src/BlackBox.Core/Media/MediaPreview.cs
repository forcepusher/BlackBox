using System.Globalization;
using SkiaSharp;

namespace BlackBox.Core.Media;

/// <summary>What a set of files is, in words and in one picture for the model.</summary>
/// <param name="Image">A JPEG with a look at each file (null when there is nothing to see, or the model can't see).</param>
/// <param name="Lines">One line per file: its path and what it is ("video 640x640, 81 frames at 16 fps (5.1 s), no sound, 1.4 MB").</param>
/// <param name="Caption">What the picture shows, to say where it reaches the model.</param>
public sealed record MediaLook(byte[]? Image, IReadOnlyList<string> Lines, string Caption);

/// <summary>
/// A look at pictures, animations, videos, sounds and 3D models for a model that sees only still
/// pictures: a video as a sheet of frames with their times, a sound as its waveform and
/// spectrogram with its loudness and silences in words, a 3D model from four sides. All of them in
/// one picture, since a step carries one image to the model.
/// </summary>
public static class MediaPreview
{
    /// <summary>The widest a look at one file is, and the most pixels the whole picture has (about 3,000 image tokens).</summary>
    private const int TileWidth = 1024, MaxPixels = 2048 * 1536;

    public static MediaLook ForFiles(IReadOnlyList<string> paths, IMediaDecoder? decoder, bool seesImages)
    {
        var lines = new List<string>();
        var tiles = new List<(string Label, SKBitmap Bitmap)>();
        var described = new List<string>();
        try
        {
            foreach (var path in paths.Take(12))
            {
                var (line, tile, shows) = Look(path, decoder, makeTile: seesImages && tiles.Count < 6);
                lines.Add($"{path}: {line}");
                if (tile is not null)
                {
                    tiles.Add(($"{tiles.Count + 1}. {Path.GetFileName(path)}", tile));
                    described.Add($"{tiles.Count}) {shows}");
                }
            }
            if (paths.Count > 12)
                lines.Add($"… and {paths.Count - 12} more files");
            if (tiles.Count == 0)
                return new MediaLook(null, lines, "");
            var image = Compose(tiles);
            var caption = tiles.Count == 1 ? $"The picture shows {described[0][3..]}." : $"The picture shows, from the top: {string.Join("; ", described)}.";
            return new MediaLook(image, lines, caption);
        }
        finally
        {
            foreach (var (_, bitmap) in tiles)
                bitmap.Dispose();
        }
    }

    /// <summary>What one file is, and a look at it.</summary>
    /// <returns>A line about it, a picture of it (null when none is made) and what that picture shows.</returns>
    public static (string Line, SKBitmap? Tile, string Shows) Look(string path, IMediaDecoder? decoder, bool makeTile)
    {
        if (!File.Exists(path))
            return ("not found", null, "");
        var size = Size(new FileInfo(path).Length);
        try
        {
            switch (KindOf(path))
            {
                case "image":
                case "animation":
                    return LookAtPicture(path, size, makeTile);
                case "video":
                    return LookAtVideo(path, size, decoder, makeTile);
                case "audio":
                    return LookAtSound(path, size, decoder, makeTile);
                case "3d":
                    return LookAtModel(path, size, makeTile);
                default:
                    return ($"a file, {size}", null, "");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or ArgumentException or InvalidOperationException
                                       or NotSupportedException or IndexOutOfRangeException or OverflowException or System.Text.Json.JsonException)
        {
            return ($"{size}, but it couldn't be read: {ex.Message}", null, "");
        }
    }

    public static string KindOf(string path) => Comfy.ComfyOutput.KindOf(path);

    internal static string Size(long bytes) => bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0} KB" : $"{bytes} bytes";

    // ---- pictures and animations --------------------------------------------------------------

    private static (string, SKBitmap?, string) LookAtPicture(string path, string size, bool makeTile)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidDataException("not a picture this can read");
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        var frames = codec.FrameCount;
        if (frames <= 1)
        {
            if (!makeTile)
                return ($"picture {w}x{h}, {size}", null, "");
            using var bitmap = SKBitmap.Decode(path) ?? throw new InvalidDataException("not a picture this can read");
            return ($"picture {w}x{h}, {size}", Fit(bitmap, TileWidth, TileWidth), "the picture");
        }
        var info = codec.FrameInfo;
        var duration = TimeSpan.FromMilliseconds(info.Sum(f => Math.Max(f.Duration, 10)));
        var line = $"animation {w}x{h}, {frames} frames ({duration.TotalSeconds:0.0} s), {size}";
        if (!makeTile)
            return (line, null, "");
        // Up to eight frames, evenly spread, each decoded on top of the ones it builds on.
        var picks = Enumerable.Range(0, Math.Min(8, frames)).Select(i => (int)Math.Round(i * (frames - 1) / (double)Math.Max(1, Math.Min(8, frames) - 1))).Distinct().ToList();
        var shots = new List<(TimeSpan, SKBitmap)>();
        using var canvasBitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var at = TimeSpan.Zero;
        for (var i = 0; i < frames && shots.Count < picks.Count; i++)
        {
            var options = new SKCodecOptions(i, info[i].RequiredFrame);
            codec.GetPixels(canvasBitmap.Info, canvasBitmap.GetPixels(), options);
            if (picks.Contains(i))
                shots.Add((at, canvasBitmap.Copy()));
            at += TimeSpan.FromMilliseconds(Math.Max(info[i].Duration, 10));
        }
        try
        {
            return (line, Sheet(shots, w, h), $"{shots.Count} frames of the animation with their times");
        }
        finally
        {
            foreach (var (_, b) in shots) b.Dispose();
        }
    }

    // ---- video ------------------------------------------------------------------------------

    private static (string, SKBitmap?, string) LookAtVideo(string path, string size, IMediaDecoder? decoder, bool makeTile)
    {
        if (decoder?.Probe(path) is not { } info)
            return ($"video, {size} (nothing on this PC can decode it to look at it; ffmpeg in Data/tools would)", null, "");
        var line = info.Describe() + ", " + size;
        if (!makeTile || !info.HasVideo)
            return (line, null, "");
        using var reader = decoder.OpenVideo(path, 512);
        if (reader is null)
            return (line, null, "");
        var count = info.Duration.TotalSeconds < 1.5 ? 4 : 8;
        var shots = new List<(TimeSpan, SKBitmap)>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                // From just after the start to just before the end.
                var t = TimeSpan.FromSeconds(Math.Max(0, info.Duration.TotalSeconds * (0.02 + 0.96 * i / (count - 1))));
                if (i > 0) reader.Seek(t);
                if (reader.Next() is not { } frame)
                    break;
                shots.Add((frame.Time, ToBitmap(frame.Frame)));
            }
            if (shots.Count == 0)
                return (line, null, "");
            return (line, Sheet(shots, reader.Width, reader.Height), $"{shots.Count} frames of the video with their times");
        }
        finally
        {
            foreach (var (_, b) in shots) b.Dispose();
        }
    }

    public static SKBitmap ToBitmap(Platform.RawImage frame)
    {
        var bitmap = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(frame.Bgra, 0, bitmap.GetPixels(), Math.Min(frame.Bgra.Length, bitmap.ByteCount));
        return bitmap;
    }

    /// <summary>Frames in a grid, four to a row, each with its time in a corner.</summary>
    private static SKBitmap Sheet(IReadOnlyList<(TimeSpan Time, SKBitmap Frame)> shots, int width, int height)
    {
        var columns = shots.Count <= 4 ? Math.Min(shots.Count, 2) : 4;
        if (height > width * 1.3 && shots.Count > 4) columns = Math.Min(shots.Count, 4);
        var rows = (shots.Count + columns - 1) / columns;
        var cellW = (TileWidth - (columns - 1) * 4) / columns;
        var cellH = Math.Max(16, (int)Math.Round(cellW * (double)height / width));
        var sheet = new SKBitmap(TileWidth, rows * cellH + (rows - 1) * 4);
        using var canvas = new SKCanvas(sheet);
        canvas.Clear(new SKColor(32, 32, 32));
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, Math.Max(14, cellW / 14f));
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = font.Size, Typeface = font.Typeface };
        using var shade = new SKPaint { Color = new SKColor(0, 0, 0, 170) };
        for (var i = 0; i < shots.Count; i++)
        {
            var (x, y) = (i % columns * (cellW + 4), i / columns * (cellH + 4));
            canvas.DrawBitmap(shots[i].Frame, new SKRect(x, y, x + cellW, y + cellH), paint);
            var label = shots[i].Time.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            var labelW = text.MeasureText(label) + 10;
            canvas.DrawRect(x, y + cellH - font.Size - 8, labelW, font.Size + 8, shade);
            canvas.DrawText(label, x + 5, y + cellH - 6, font, text);
        }
        return sheet;
    }

    // ---- sound ------------------------------------------------------------------------------

    private static (string, SKBitmap?, string) LookAtSound(string path, string size, IMediaDecoder? decoder, bool makeTile)
    {
        using var reader = decoder?.OpenAudio(path);
        if (reader is null)
            return ($"sound, {size} (nothing on this PC can decode it to listen to it; ffmpeg in Data/tools would)", null, "");
        var (mono, rate) = ReadMono(reader, TimeSpan.FromMinutes(10));
        var seconds = mono.Length / (double)rate;
        var stats = SoundStats.Of(mono, rate);
        var channels = reader.Channels == 1 ? "mono" : reader.Channels == 2 ? "stereo" : $"{reader.Channels} channels";
        var line = $"sound {Math.Max(seconds, reader.Duration.TotalSeconds):0.0} s, {rate / 1000.0:0.#} kHz {channels}, {stats.Describe()}, {size}";
        if (!makeTile || mono.Length == 0)
            return (line, null, "");
        return (line, SoundPicture(mono, rate), "the sound's waveform (top) and spectrogram (bottom, low notes at the bottom, louder is brighter), with seconds marked");
    }

    /// <summary>The sound mixed to one channel, up to <paramref name="max"/> long.</summary>
    public static (float[] Samples, int Rate) ReadMono(IAudioReader reader, TimeSpan max)
    {
        var channels = Math.Max(1, reader.Channels);
        var limit = (long)(max.TotalSeconds * reader.SampleRate);
        var mono = new List<float>((int)Math.Min(limit, reader.SampleRate * 60L));
        var buffer = new float[8192 * channels];
        int read;
        while (mono.Count < limit && (read = reader.Read(buffer, 0, buffer.Length)) > 0)
            for (var i = 0; i + channels <= read; i += channels)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++) sum += buffer[i + c];
                mono.Add(sum / channels);
            }
        return (mono.ToArray(), reader.SampleRate);
    }

    /// <summary>Waveform over spectrogram, with a mark each second (or each 5, 10, 30 for long sounds).</summary>
    private static SKBitmap SoundPicture(float[] mono, int rate)
    {
        const int waveH = 160, specH = 256, axisH = 22;
        var bitmap = new SKBitmap(TileWidth, waveH + specH + axisH);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(24, 24, 28));
        var columns = TileWidth;
        var perColumn = Math.Max(1, mono.Length / (double)columns);
        // Drawn as loud as the picture allows (up to ten times): a quiet sound is still seen; the
        // words beside the picture say how loud it really is.
        var peak = 0f;
        foreach (var s in mono) peak = Math.Max(peak, Math.Abs(s));
        var gain = peak > 0 ? Math.Min(10f, 0.95f / peak) : 1f;

        using (var wave = new SKPaint { Color = new SKColor(90, 200, 255), StrokeWidth = 1 })
        using (var mid = new SKPaint { Color = new SKColor(70, 70, 80), StrokeWidth = 1 })
        {
            canvas.DrawLine(0, waveH / 2f, TileWidth, waveH / 2f, mid);
            for (var x = 0; x < columns; x++)
            {
                var from = (int)(x * perColumn);
                var to = Math.Min(mono.Length, (int)((x + 1) * perColumn));
                if (from >= to) continue;
                float lo = 0, hi = 0;
                for (var i = from; i < to; i++) { lo = Math.Min(lo, mono[i]); hi = Math.Max(hi, mono[i]); }
                canvas.DrawLine(x, waveH / 2f - hi * gain * (waveH / 2f - 4), x, waveH / 2f - lo * gain * (waveH / 2f - 4) + 1, wave);
            }
        }

        // Spectrogram: 2048-sample windows, frequencies on a log scale from 40 Hz to half the rate.
        const int n = 2048;
        var re = new double[n];
        var im = new double[n];
        var window = Enumerable.Range(0, n).Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1))).ToArray();
        var (fMin, fMax) = (40.0, rate / 2.0);
        var pixels = new SKColor[TileWidth * specH];
        for (var x = 0; x < columns; x++)
        {
            var center = (int)((x + 0.5) * mono.Length / columns);
            for (var i = 0; i < n; i++)
            {
                var at = center - n / 2 + i;
                re[i] = at >= 0 && at < mono.Length ? mono[at] * window[i] : 0;
                im[i] = 0;
            }
            Fft(re, im);
            for (var y = 0; y < specH; y++)
            {
                var f = fMin * Math.Pow(fMax / fMin, 1 - (y + 0.5) / specH);
                var bin = Math.Clamp((int)Math.Round(f / rate * n), 1, n / 2 - 1);
                var magnitude = Math.Sqrt(re[bin] * re[bin] + im[bin] * im[bin]) / (n / 4.0);
                var db = 20 * Math.Log10(magnitude + 1e-9);
                var v = Math.Clamp((db + 90) / 80, 0, 1); // -90 dB black … -10 dB bright
                pixels[y * TileWidth + x] = Heat(v);
            }
        }
        using (var spec = new SKBitmap(TileWidth, specH))
        {
            spec.Pixels = pixels;
            canvas.DrawBitmap(spec, 0, waveH);
        }

        // Seconds along the bottom.
        var seconds = mono.Length / (double)rate;
        var step = seconds <= 12 ? 1 : seconds <= 60 ? 5 : seconds <= 180 ? 10 : 30;
        using var tick = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = 1 };
        using var font = new SKFont(SKTypeface.Default, 14);
        using var label = new SKPaint { Color = SKColors.White, IsAntialias = true };
        for (var s = 0; s <= seconds + 1e-6; s += step)
        {
            var x = (float)(s / seconds * (TileWidth - 1));
            canvas.DrawLine(x, 0, x, waveH + specH, tick);
            canvas.DrawText($"{s}s", Math.Min(x + 2, TileWidth - 30), waveH + specH + 16, font, label);
        }
        return bitmap;
    }

    private static SKColor Heat(double v)
    {
        // black → blue → magenta → orange → yellow-white
        var r = Math.Clamp(v * 3 - 0.9, 0, 1);
        var g = Math.Clamp(v * 3 - 1.8, 0, 1);
        var b = Math.Clamp(v < 0.5 ? v * 2.2 : 1.1 - (v - 0.5) * 1.6, 0, 1);
        return new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    /// <summary>In-place radix-2 FFT; the length is a power of two.</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var (wr, wi) = (Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    var (ar, ai) = (re[i + k], im[i + k]);
                    var (br, bi) = (re[i + k + len / 2] * cr - im[i + k + len / 2] * ci, re[i + k + len / 2] * ci + im[i + k + len / 2] * cr);
                    (re[i + k], im[i + k]) = (ar + br, ai + bi);
                    (re[i + k + len / 2], im[i + k + len / 2]) = (ar - br, ai - bi);
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
    }

    // ---- 3D models --------------------------------------------------------------------------

    private static (string, SKBitmap?, string) LookAtModel(string path, string size, bool makeTile)
    {
        var mesh = Mesh.Load(path);
        var line = mesh.Describe() + ", " + size;
        if (!makeTile || mesh.TriangleCount == 0)
            return (line, null, "");
        const int cell = 510;
        var tile = new SKBitmap(TileWidth, cell * 2 + 4);
        using var canvas = new SKCanvas(tile);
        canvas.Clear(new SKColor(32, 32, 32));
        using var font = new SKFont(SKTypeface.Default, 16);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var views = new[] { ("front", 0.0, 10.0), ("right side", 90.0, 10.0), ("back", 180.0, 10.0), ("from above, front left", -40.0, 40.0) };
        for (var i = 0; i < views.Length; i++)
        {
            var (name, yaw, pitch) = views[i];
            using var view = MeshRenderer.Render(mesh, cell, cell, yaw, pitch);
            var (x, y) = (i % 2 * (cell + 4), i / 2 * (cell + 4));
            canvas.DrawBitmap(view, x, y);
            canvas.DrawText(name, x + 8, y + 22, font, text);
        }
        return (line, tile, "the 3D model from four sides (front, right side, back, and from above), lit from the front");
    }

    // ---- putting it together -----------------------------------------------------------------

    private static SKBitmap Fit(SKBitmap bitmap, int maxW, int maxH)
    {
        var scale = Math.Min(1.0, Math.Min((double)maxW / bitmap.Width, (double)maxH / bitmap.Height));
        var (w, h) = (Math.Max(1, (int)Math.Round(bitmap.Width * scale)), Math.Max(1, (int)Math.Round(bitmap.Height * scale)));
        var fitted = new SKBitmap(w, h);
        using var canvas = new SKCanvas(fitted);
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        canvas.DrawBitmap(bitmap, new SKRect(0, 0, w, h), paint);
        return fitted;
    }

    /// <summary>The looks one under the other with their numbers and names, scaled to a few megapixels, as a JPEG whose sides are multiples of 32.</summary>
    private static byte[] Compose(IReadOnlyList<(string Label, SKBitmap Bitmap)> tiles)
    {
        const int labelH = 30;
        var width = tiles.Max(t => t.Bitmap.Width);
        var height = tiles.Sum(t => labelH + (int)Math.Round(t.Bitmap.Height * (double)width / t.Bitmap.Width));
        var scale = Math.Min(1.0, Math.Min(Math.Sqrt((double)MaxPixels / ((long)width * height)), 2048.0 / Math.Max(width, height)));
        var (outW, outH) = (Snap(width * scale), Snap(height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(outW, outH, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(16, 16, 16));
        canvas.Scale((float)(outW / (double)width), (float)(outH / (double)height));
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 20);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float y = 0;
        foreach (var (label, bitmap) in tiles)
        {
            canvas.DrawText(label, 8, y + 22, font, text);
            y += labelH;
            var h = (float)(bitmap.Height * (double)width / bitmap.Width);
            canvas.DrawBitmap(bitmap, new SKRect(0, y, width, y + h), paint);
            y += h;
        }
        using var image = surface.Snapshot();
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        return jpeg.ToArray();
    }

    private static int Snap(double size) => Math.Max(32, (int)Math.Round(size / 32) * 32);
}

/// <summary>How loud a sound is and where it is silent, in words the model can check against what was asked.</summary>
internal sealed record SoundStats(double PeakDb, double RmsDb, double LeadingSilence, double TrailingSilence, IReadOnlyList<(double From, double To)> Gaps, double Clipped)
{
    private const double SilentDb = -50;

    public static SoundStats Of(float[] mono, int rate)
    {
        if (mono.Length == 0)
            return new(-120, -120, 0, 0, [], 0);
        double peak = 0, sum = 0;
        long clipped = 0;
        foreach (var s in mono)
        {
            var a = Math.Abs(s);
            peak = Math.Max(peak, a);
            sum += s * s;
            if (a >= 0.999) clipped++;
        }
        // Loudness in 50 ms windows, to find silences.
        var window = Math.Max(1, rate / 20);
        var loud = new List<bool>();
        for (var i = 0; i < mono.Length; i += window)
        {
            double w = 0;
            var end = Math.Min(mono.Length, i + window);
            for (var j = i; j < end; j++) w += mono[j] * mono[j];
            loud.Add(Db(Math.Sqrt(w / (end - i))) > SilentDb);
        }
        var first = loud.IndexOf(true);
        var last = loud.LastIndexOf(true);
        var gaps = new List<(double, double)>();
        if (first >= 0)
            for (var i = first; i <= last; i++)
            {
                if (loud[i]) continue;
                var start = i;
                while (i <= last && !loud[i]) i++;
                if ((i - start) * 0.05 >= 0.5)
                    gaps.Add((start * 0.05, i * 0.05));
            }
        var seconds = mono.Length / (double)rate;
        return new(Db(peak), Db(Math.Sqrt(sum / mono.Length)), first < 0 ? seconds : first * 0.05, first < 0 ? 0 : seconds - (last + 1) * 0.05,
            gaps, (double)clipped / mono.Length);
    }

    private static double Db(double amplitude) => 20 * Math.Log10(Math.Max(amplitude, 1e-6));

    public string Describe()
    {
        if (PeakDb <= SilentDb)
            return "silent throughout";
        var parts = new List<string> { $"peak {PeakDb:0.0} dBFS, average {RmsDb:0} dBFS" };
        if (Clipped > 0.001)
            parts.Add($"clipping in {Clipped * 100:0.0}% of samples");
        if (LeadingSilence >= 0.3)
            parts.Add($"silent for the first {LeadingSilence:0.0} s");
        if (TrailingSilence >= 0.3)
            parts.Add($"silent for the last {TrailingSilence:0.0} s");
        if (Gaps.Count > 0)
            parts.Add("silent gaps at " + string.Join(", ", Gaps.Take(6).Select(g => $"{g.From:0.0}–{g.To:0.0} s")) + (Gaps.Count > 6 ? $" and {Gaps.Count - 6} more" : ""));
        return string.Join(", ", parts);
    }
}
