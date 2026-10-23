using System.Diagnostics;
using System.Globalization;
using System.Text;
using BlackBox.Core.Media;

namespace BlackBox.Core.Film;

/// <summary>
/// Joins a film's shots into one video with ffmpeg: each clip cut to its length, scaled to the
/// film's frame and frame rate, one after another with the storyboard's cuts, dissolves and fades,
/// the sounds of its sound tracks laid over the shots' own sound where they start, the end faded out.
/// </summary>
public static class FilmMontage
{
    /// <summary>How the film begins and ends: a short fade in from black, a longer one out.</summary>
    internal const double FadeIn = 0.4, FadeOut = 1.0;

    /// <summary>The ffmpeg command line that joins the film, and how long the film is.</summary>
    /// <param name="hasSound">Whether a file has a sound track (shots made without one get silence).</param>
    public static (List<string> Arguments, double Length, List<string> Notes) Plan(Storyboard board, Func<string, bool> hasSound, string output)
    {
        if (board.Shots.Count == 0)
            throw new FilmException("The film has no shots to join.");
        var missing = board.Shots.Where(s => !File.Exists(board.ShotVideo(s))).Select(s => s.Id).ToList();
        if (missing.Count > 0)
            throw new FilmException($"These shots have no clip yet: {string.Join(", ", missing)}. Make them first.");
        var notes = new List<string>();
        var (width, height, fps) = (board.Width, board.Height, board.Fps);
        var sounds = new List<FilmSound>();
        foreach (var sound in board.Sounds)
            if (File.Exists(board.SoundFile(sound)))
                sounds.Add(sound);
            else
                notes.Add(sound.Source == FilmSound.Song ? $"The song {sound.Label} isn't made yet, so the film is without it." : $"The sound file {board.SoundFile(sound)} isn't there, so the film is without it.");
        // Under a song the shots' own sound is kept at half: LTX makes a shot's sounds well and
        // its music badly (the user's finding), so a shot is asked for its sounds and no music
        // (FilmMaker.WithoutMusic) and the music is the song. Asked for nothing, LTX's sound was a
        // muddy wash that took the Paper Boat song from 3.1 to 2.7 on SongEval at 30%.
        var shotVolume = sounds.Any(s => s.Source == FilmSound.Song) ? 0.5 : 1.0;
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
                var kind = shot.Transition == FilmShot.Fade ? "fadeblack" : "fade";
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
            // Each sound as its clip on the track is: its start cut off (trim), as long as it plays,
            // from where it starts in the film, at its volume; quieter while someone speaks, so the
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
                f.Append($"[{board.Shots.Count + k}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo,atrim=start={N(sound.Trim)},asetpts=PTS-STARTPTS{cut},volume={N(sound.Volume)},")
                 .Append($"adelay={delay}|{delay},apad=whole_dur={N(length)},atrim=duration={N(length)},asetpts=PTS-STARTPTS{duck}[s{k}];");
                mix.Append($"[s{k}]");
            }
            f.Append(mix).Append($"amix=inputs={sounds.Count + 1}:duration=first:normalize=0[mixed];");
            audio = "mixed";
        }
        // A sound cut where the film ends fades out over longer than the picture does.
        var runsOn = sounds.Any(s => s.Seconds <= 0 || s.Start + s.Seconds > length + 0.05);
        var soundFade = runsOn ? Math.Min(2.5, length / 3) : fadeOut;
        f.Append($"[{audio}]afade=t=out:st={N(length - soundFade)}:d={N(soundFade)},alimiter=limit=0.95[aout]");

        args.AddRange(["-filter_complex", f.ToString(), "-map", "[vout]", "-map", "[aout]",
            "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-r", fps.ToString(CultureInfo.InvariantCulture),
            "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", "-t", N(length), output]);
        return (args, length, notes);
    }

    /// <summary>Joins the film into its file next to the storyboard (named after it); returns where it is and what to know.</summary>
    public static async Task<FilmMade> JoinAsync(Storyboard board, string? ffmpeg, IMediaDecoder? decoder, Action<string>? progress, CancellationToken ct)
    {
        if (ffmpeg is null)
            throw new FilmException("Joining the shots needs ffmpeg, and there is none on this PC: download a portable build (ffmpeg's own site links them, such as gyan.dev's " +
                                    "ffmpeg-release-essentials.zip) and unpack it into BlackBox's Data\\tools, where it is found, then join again.");
        var output = board.FilmFile;
        var temp = Path.Combine(board.Folder, "." + Path.GetFileNameWithoutExtension(output) + ".joining.mp4");
        var (args, length, notes) = Plan(board, file => decoder?.Probe(file)?.HasAudio ?? true, temp);
        var clock = Stopwatch.StartNew();
        var psi = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var process = Process.Start(psi) ?? throw new FilmException($"ffmpeg ({ffmpeg}) didn't start.");
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
                    progress?.Invoke($"Joining the shots… {us / 1e6:0.0} of {length:0.0} s");
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
            throw new FilmException($"ffmpeg couldn't join the shots (exit code {process.ExitCode}):\n{tail}");
        }
        MediaRelease.Release(output); // the film joined before may be playing in the chat
        File.Move(temp, output, overwrite: true);
        return new FilmMade(output, null, null, clock.Elapsed, notes);
    }
}
