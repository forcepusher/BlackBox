using System.Diagnostics;
using System.Globalization;
using System.Text;
using Sloperator.Core.Media;

namespace Sloperator.Core.Video;

/// <summary>
/// Joins a video's shots into one video with ffmpeg: each clip cut to its length, scaled to the
/// video's frame and frame rate, one after another with the storyboard's cuts, dissolves and fades,
/// the sounds of its sound lines laid over the shots' own sound where they start, the end faded out.
/// </summary>
public static class VideoMontage
{
    /// <summary>How the video begins and ends: a short fade in from black, a longer one out.</summary>
    internal const double FadeIn = 0.4, FadeOut = 1.0;

    /// <summary>The ffmpeg command line that joins the video, and how long the video is.</summary>
    /// <param name="hasSound">Whether a file has a sound track (shots made without one get silence).</param>
    public static (List<string> Arguments, double Length, List<string> Notes) Plan(Storyboard board, Func<string, bool> hasSound, string output)
    {
        if (board.Shots.Count == 0)
            throw new VideoException("The video has no shots to join.");
        var missing = board.Shots.Where(s => !File.Exists(board.ShotVideo(s))).Select(s => s.Id).ToList();
        if (missing.Count > 0)
            throw new VideoException($"These shots have no clip yet: {string.Join(", ", missing)}. Make them first.");
        var notes = new List<string>();
        var (width, height, fps) = (board.Width, board.Height, board.Fps);
        var sounds = new List<VideoSound>();
        foreach (var sound in board.Sounds)
            if (File.Exists(board.SoundFile(sound)))
                sounds.Add(sound);
            else
                notes.Add(sound.Source == VideoSound.FromFile ? $"The sound file {board.SoundFile(sound)} isn't there, so the video is without it." : $"The {(sound.Source == VideoSound.Foley ? "foley" : "song")} {sound.Label} isn't made yet, so the video is without it.");
        // Under a song the shots' own sound is kept at half: LTX makes a shot's sounds well and
        // its music badly (the user's finding), so a shot is asked for its sounds and no music
        // (VideoMaker.WithoutMusic) and the music is the song. Asked for nothing, LTX's sound was a
        // muddy wash that took the Paper Boat song from 3.1 to 2.7 on SongEval at 30%.
        var shotVolume = sounds.Any(s => s.Source == VideoSound.Song) ? 0.5 : 1.0;
        var args = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
        foreach (var shot in board.Shots)
            args.AddRange(["-i", board.ShotVideo(shot)]);
        foreach (var sound in sounds)
            args.AddRange(["-i", board.SoundFile(sound)]);
        // A shot's line is heard as the voice model said it, clean, not as the video model gave it back.
        var lines = new Dictionary<int, int>();
        var next = board.Shots.Count + sounds.Count;
        for (var i = 0; i < board.Shots.Count; i++)
            if (Storyboard.Speaks(board.Shots[i]) && File.Exists(board.ShotAudio(board.Shots[i])))
            {
                args.AddRange(["-i", board.ShotAudio(board.Shots[i])]);
                lines[i] = next++;
            }

        var f = new StringBuilder();
        string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
        for (var i = 0; i < board.Shots.Count; i++)
        {
            var shot = board.Shots[i];
            var s = shot.Seconds;
            // A clip shorter than its shot holds its last frame; a longer one is cut.
            f.Append($"[{i}:v]tpad=stop_mode=clone:stop_duration={N(s)},trim=duration={N(s)},setpts=PTS-STARTPTS,")
             .Append($"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,fps={fps},format=yuv420p[v{i}];");
            if (lines.TryGetValue(i, out var line))
            {
                f.Append($"[{line}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad=whole_dur={N(s)},atrim=duration={N(s)},asetpts=PTS-STARTPTS[a{i}];");
                continue;
            }
            if (hasSound(board.ShotVideo(shot)))
                f.Append($"[{i}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,apad=whole_dur={N(s)},atrim=duration={N(s)},asetpts=PTS-STARTPTS,volume={N(shotVolume)}[a{i}];");
            else
                f.Append($"anullsrc=r=48000:cl=stereo,atrim=duration={N(s)},aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];");
        }
        // One after another: a cut joins them, a dissolve or a fade lays the next over the end of the one before.
        var (video, audio, length) = ("v0", "a0", board.Shots[0].Seconds);
        for (var i = 1; i < board.Shots.Count; i++)
        {
            var shot = board.Shots[i];
            var overlap = Storyboard.Overlap(shot);
            if (overlap <= 0)
            {
                f.Append($"[{video}][v{i}]concat=n=2:v=1:a=0[vj{i}];[{audio}][a{i}]concat=n=2:v=0:a=1[aj{i}];");
                length += shot.Seconds;
            }
            else
            {
                var kind = shot.Transition == VideoShot.Fade ? "fadeblack" : "fade";
                f.Append($"[{video}][v{i}]xfade=transition={kind}:duration={N(overlap)}:offset={N(length - overlap)}[vj{i}];")
                 .Append($"[{audio}][a{i}]acrossfade=d={N(overlap)}[aj{i}];");
                length += shot.Seconds - overlap;
            }
            (video, audio) = ($"vj{i}", $"aj{i}");
        }
        var fadeOut = Math.Min(FadeOut, length / 3);
        f.Append($"[{video}]fade=t=in:d={N(Math.Min(FadeIn, length / 4))},fade=t=out:st={N(length - fadeOut)}:d={N(fadeOut)}[vout];");
        if (sounds.Count > 0)
        {
            // Each sound as its clip on the sound line is: its start cut off (trim), as long as it plays,
            // from where it starts in the video, at its volume; quieter while someone speaks, so the
            // line is heard over it.
            var (starts, _) = board.Timing();
            var speaking = lines.Keys.Select(i => $"between(t,{N(starts[i])},{N(starts[i] + board.Shots[i].Seconds)})").ToList();
            var duck = speaking.Count == 0 ? "" : $",volume='if({string.Join("+", speaking)},0.35,1)':eval=frame";
            var mix = new StringBuilder($"[{audio}]");
            for (var k = 0; k < sounds.Count; k++)
            {
                var sound = sounds[k];
                var cut = sound.Seconds > 0 ? $",atrim=duration={N(sound.Seconds)}" : "";
                var delay = (long)Math.Round(sound.Start * 1000);
                f.Append($"[{board.Shots.Count + k}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=start={N(sound.Trim)},asetpts=PTS-STARTPTS{cut},volume={N(sound.Volume * board.LineVolume(sound.SoundLine))},")
                 // Stamped again by their samples after the delay: a sound trimmed at its start (atrim
                 // start > 0) lost its delay in apad and atrim (ffmpeg 8.1), and played from 0 s.
                 .Append($"adelay={delay}|{delay},asetpts=N/SR/TB,apad=whole_dur={N(length)},atrim=duration={N(length)},asetpts=PTS-STARTPTS{duck}[s{k}];");
                mix.Append($"[s{k}]");
            }
            f.Append(mix).Append($"amix=inputs={sounds.Count + 1}:duration=first:normalize=0[mixed];");
            audio = "mixed";
        }
        // A sound cut where the video ends fades out over longer than the picture does.
        var runsOn = sounds.Any(s => s.Seconds <= 0 || s.Start + s.Seconds > length + 0.05);
        var soundFade = runsOn ? Math.Min(2.5, length / 3) : fadeOut;
        f.Append($"[{audio}]afade=t=out:st={N(length - soundFade)}:d={N(soundFade)},alimiter=limit=0.95[aout]");

        args.AddRange(["-filter_complex", f.ToString(), "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-t", N(length), output]);
        return (args, length, notes);
    }

    /// <summary>Foley is made from what the video shows at 25 frames a second (MMAudio's own rate), small.</summary>
    internal const int FoleyFps = 25, FoleyHeight = 384;

    /// <summary>
    /// The ffmpeg command line that cuts a stretch of the video out of its clips, as the joined video
    /// would show it (cuts, dissolves and fades), without sound, at <see cref="FoleyFps"/>: what foley
    /// is made from. Only the clips under the stretch are needed, and they must be made.
    /// </summary>
    public static List<string> StretchPlan(Storyboard board, double start, double seconds, string output)
    {
        var (starts, length) = board.Timing();
        var end = Math.Min(length, start + seconds);
        if (end - start < 0.5)
            throw new VideoException(start >= length ? $"No clip is under {N(start)} s: the video is {N(length)} s long." : "The stretch is too short to make a sound from.");
        var under = Enumerable.Range(0, board.Shots.Count).Where(i => starts[i] < end - 1e-6 && starts[i] + board.Shots[i].Seconds > start + 1e-6).ToList();
        var missing = under.Where(i => !File.Exists(board.ShotVideo(board.Shots[i]))).Select(i => board.Shots[i].Id).ToList();
        if (missing.Count > 0)
            throw new VideoException($"The clips under it aren't made yet: {string.Join(", ", missing)}. Make them first: the sound is made from what they show.");
        var (width, height) = (board.Width, board.Height);
        var args = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
        foreach (var i in under)
            args.AddRange(["-i", board.ShotVideo(board.Shots[i])]);
        var f = new StringBuilder();
        for (var k = 0; k < under.Count; k++)
        {
            var s = board.Shots[under[k]].Seconds;
            f.Append($"[{k}:v]tpad=stop_mode=clone:stop_duration={N(s)},trim=duration={N(s)},setpts=PTS-STARTPTS,")
             .Append($"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height},setsar=1,fps={FoleyFps},format=yuv420p[v{k}];");
        }
        var (video, joined) = ("v0", board.Shots[under[0]].Seconds);
        for (var k = 1; k < under.Count; k++)
        {
            var shot = board.Shots[under[k]];
            var overlap = Storyboard.Overlap(shot);
            if (overlap <= 0)
                f.Append($"[{video}][v{k}]concat=n=2:v=1:a=0[vj{k}];");
            else
                f.Append($"[{video}][v{k}]xfade=transition={(shot.Transition == VideoShot.Fade ? "fadeblack" : "fade")}:duration={N(overlap)}:offset={N(joined - overlap)}[vj{k}];");
            joined += shot.Seconds - overlap;
            video = $"vj{k}";
        }
        f.Append($"[{video}]trim=start={N(start - starts[under[0]])}:duration={N(end - start)},setpts=PTS-STARTPTS,scale=-2:{FoleyHeight}[vout]");
        args.AddRange(["-filter_complex", f.ToString(), "-map", "[vout]", "-an", "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-r", FoleyFps.ToString(CultureInfo.InvariantCulture), "-t", N(end - start), output]);
        return args;
    }

    private static string N(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Runs ffmpeg to make <paramref name="output"/> (written to a temporary file next to it first), saying how far it has got.</summary>
    internal static async Task RunAsync(string ffmpeg, List<string> args, string output, double length, string doing, string task, Action<string>? progress, CancellationToken ct)
    {
        var temp = args[^1];
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new VideoException($"ffmpeg ({ffmpeg}) didn't start.");
        var errors = new StringBuilder();
        var said = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                lock (errors)
                {
                    errors.AppendLine(line);
                    if (errors.Length > 20_000)
                        errors.Remove(0, errors.Length - 10_000);
                }
        }, CancellationToken.None);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && long.TryParse(line[12..], out var us) && us > 0)
                    progress?.Invoke($"{doing}… {us / 1e6:0.0} of {length:0.0} s");
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
        try { await said.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { }
        if (process.ExitCode != 0 || !File.Exists(temp))
        {
            string tail;
            lock (errors)
                tail = string.Join("\n", errors.ToString().Split('\n').Where(l => l.Trim().Length > 0).TakeLast(12));
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw new VideoException($"ffmpeg couldn't {task} (exit code {process.ExitCode}):\n{tail}");
        }
        if (!string.Equals(temp, output, StringComparison.OrdinalIgnoreCase))
        {
            MediaRelease.Release(output); // the one made before may be playing in the chat
            File.Move(temp, output, overwrite: true);
        }
    }

    /// <summary>Joins the video into its file next to the storyboard (named after it); returns where it is and what to know.</summary>
    public static async Task<VideoMade> JoinAsync(Storyboard board, string? ffmpeg, IMediaDecoder? decoder, Action<string>? progress, CancellationToken ct)
    {
        if (ffmpeg is null)
            throw new VideoException("Joining the shots needs ffmpeg, and there is none on this PC: download a portable build (ffmpeg's own site links them, such as gyan.dev's " +
                                    "ffmpeg-release-essentials.zip) and unpack it into Sloperator's Data\\tools, where it is found, then join again.");
        var output = board.VideoFile;
        var temp = Path.Combine(board.Folder, "." + Path.GetFileNameWithoutExtension(output) + ".joining.mp4");
        var (args, length, notes) = Plan(board, file => decoder?.Probe(file)?.HasAudio ?? true, temp);
        var clock = Stopwatch.StartNew();
        await RunAsync(ffmpeg, args, output, length, "Joining the shots", "join the shots", progress, ct).ConfigureAwait(false);
        return new VideoMade(output, null, null, clock.Elapsed, notes);
    }
}
