using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Sloperator.Core.Platform;

namespace Sloperator.Core.Media;

/// <summary>
/// Video and sound through ffmpeg, for what the OS can't read itself (WebM, Opus, MKV…): the one on
/// the PATH, one the agent put in Data/tools, or the one that comes with ComfyUI. Frames and samples
/// come through a pipe, so nothing is written to disk.
/// </summary>
public sealed partial class FfmpegDecoder(Func<IEnumerable<string>> searchFolders) : IMediaDecoder
{
    private string? _found;
    private DateTime _lookedAt;

    /// <summary>ffmpeg's program, or null when there is none (looked for again after a minute: the agent may have just fetched it).</summary>
    public string? Program
    {
        get
        {
            if (_found is not null && File.Exists(_found))
                return _found;
            if (DateTime.UtcNow - _lookedAt < TimeSpan.FromMinutes(1))
                return null;
            _lookedAt = DateTime.UtcNow;
            return _found = Find(searchFolders());
        }
    }

    public string? Ffmpeg => Program;

    private static string? Find(IEnumerable<string> folders)
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        foreach (var folder in folders.Where(Directory.Exists))
        {
            try
            {
                var match = Directory.EnumerateFiles(folder, "ffmpeg*" + Path.GetExtension(exe), new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
                    .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).StartsWith("ffmpeg", StringComparison.OrdinalIgnoreCase) && !f.Contains("ffprobe", StringComparison.OrdinalIgnoreCase));
                if (match is not null) return match;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public MediaInfo? Probe(string path)
    {
        if (Program is not { } ffmpeg)
            return null;
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-hide_banner", "-i", path })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardOutput.ReadToEndAsync();
        var said = p.StandardError.ReadToEndAsync();
        // Ten seconds at most: the wait came after reading all it said, so ffmpeg stuck on a file
        // on a share that is off held up read_file, which Stop couldn't end, for as long as it took.
        if (!said.Wait(TimeSpan.FromSeconds(10)))
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return null;
        }
        p.WaitForExit(2_000);
        return ParseProbe(said.Result);
    }

    internal static MediaInfo? ParseProbe(string text)
    {
        var duration = Duration().Match(text) is { Success: true } d
            ? new TimeSpan(0, int.Parse(d.Groups[1].Value), int.Parse(d.Groups[2].Value), int.Parse(d.Groups[3].Value)) + TimeSpan.FromSeconds(double.Parse("0." + d.Groups[4].Value, CultureInfo.InvariantCulture))
            : TimeSpan.Zero;
        var video = VideoStream().Match(text);
        var audio = AudioStream().Match(text);
        if (!video.Success && !audio.Success)
            return null;
        double fps = 0;
        if (video.Success)
        {
            // The frame rate comes later on the stream's line than its size.
            var end = text.IndexOf('\n', video.Index);
            var line = text[video.Index..(end < 0 ? text.Length : end)];
            if (Fps().Match(line) is { Success: true } f)
                fps = double.Parse(f.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        var channels = 0;
        if (audio.Success)
            channels = audio.Groups[2].Value switch { "mono" => 1, "stereo" => 2, var c when c.StartsWith("5.1") => 6, var c when c.StartsWith("7.1") => 8, _ => 2 };
        return new MediaInfo
        {
            Duration = duration,
            HasVideo = video.Success,
            Width = video.Success ? int.Parse(video.Groups[1].Value) : 0,
            Height = video.Success ? int.Parse(video.Groups[2].Value) : 0,
            FrameRate = fps,
            HasAudio = audio.Success,
            SampleRate = audio.Success ? int.Parse(audio.Groups[1].Value) : 0,
            Channels = channels,
        };
    }

    public IVideoReader? OpenVideo(string path, int maxSide)
    {
        if (Program is not { } ffmpeg || Probe(path) is not { HasVideo: true } info || info.Width <= 0 || info.Height <= 0)
            return null;
        var scale = maxSide > 0 ? Math.Min(1.0, (double)maxSide / Math.Max(info.Width, info.Height)) : 1.0;
        var (w, h) = (Math.Max(2, (int)Math.Round(info.Width * scale / 2) * 2), Math.Max(2, (int)Math.Round(info.Height * scale / 2) * 2));
        return new VideoPipe(ffmpeg, path, info, w, h);
    }

    public IAudioReader? OpenAudio(string path)
    {
        if (Program is not { } ffmpeg || Probe(path) is not { HasAudio: true } info)
            return null;
        return new AudioPipe(ffmpeg, path, info);
    }

    private static Process Start(string ffmpeg, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync(); // drained, so ffmpeg never blocks on it
        return p;
    }

    private static void Stop(Process? p)
    {
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(); }
        catch (InvalidOperationException) { }
        p.Dispose();
    }

    private static string Seconds(TimeSpan t) => t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed class VideoPipe(string ffmpeg, string path, MediaInfo info, int width, int height) : IVideoReader
    {
        private Process? _process;
        private TimeSpan _start;
        private long _frame;

        public int Width => width;
        public int Height => height;
        public TimeSpan Duration => info.Duration;
        public double FrameRate => info.FrameRate > 0 ? info.FrameRate : 25;

        public (TimeSpan Time, RawImage Frame)? Next()
        {
            _process ??= Start(ffmpeg, ["-v", "error", "-ss", Seconds(_start), "-i", path, "-an", "-sn",
                "-vf", $"scale={width}:{height}:flags=bilinear", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1"]);
            var bytes = new byte[width * height * 4];
            var stream = _process.StandardOutput.BaseStream;
            var read = 0;
            while (read < bytes.Length)
            {
                var n = stream.Read(bytes, read, bytes.Length - read);
                if (n <= 0) return null;
                read += n;
            }
            var time = _start + TimeSpan.FromSeconds(_frame++ / FrameRate);
            return (time, new RawImage(width, height, bytes));
        }

        public void Seek(TimeSpan time)
        {
            Stop(_process);
            _process = null;
            _start = time < TimeSpan.Zero ? TimeSpan.Zero : time;
            _frame = 0;
        }

        public void Dispose() => Stop(_process);
    }

    private sealed class AudioPipe(string ffmpeg, string path, MediaInfo info) : IAudioReader
    {
        private Process? _process;
        private TimeSpan _start;
        private readonly byte[] _bytes = new byte[64 * 1024];

        public int SampleRate => info.SampleRate > 0 ? info.SampleRate : 44100;
        public int Channels => Math.Clamp(info.Channels, 1, 2);
        public TimeSpan Duration => info.Duration;

        public int Read(float[] buffer, int offset, int count)
        {
            _process ??= Start(ffmpeg, ["-v", "error", "-ss", Seconds(_start), "-i", path, "-vn", "-sn",
                "-f", "f32le", "-ac", Channels.ToString(CultureInfo.InvariantCulture), "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "pipe:1"]);
            var want = Math.Min(count * 4, _bytes.Length);
            var stream = _process.StandardOutput.BaseStream;
            var read = 0;
            while (read < want)
            {
                var n = stream.Read(_bytes, read, want - read);
                if (n <= 0) break;
                read += n;
            }
            var samples = read / 4;
            Buffer.BlockCopy(_bytes, 0, buffer, offset * 4, samples * 4);
            return samples;
        }

        public void Seek(TimeSpan time)
        {
            Stop(_process);
            _process = null;
            _start = time < TimeSpan.Zero ? TimeSpan.Zero : time;
        }

        public void Dispose() => Stop(_process);
    }

    [GeneratedRegex(@"Duration: (\d+):(\d+):(\d+)\.(\d+)")]
    private static partial Regex Duration();

    [GeneratedRegex(@"Stream #\S+.*?: Video: .*?(\d{2,5})x(\d{2,5})")]
    private static partial Regex VideoStream();

    [GeneratedRegex(@"([\d.]+) (?:fps|tbr)")]
    private static partial Regex Fps();

    [GeneratedRegex(@"Stream #\S+.*?: Audio: .*?(\d+) Hz, ([^,]+)")]
    private static partial Regex AudioStream();
}
