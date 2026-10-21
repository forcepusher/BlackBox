using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Media;
using SkiaSharp;

namespace BlackBox.Core.Film;

/// <summary>What a film job made: the file in the film's folder, and the workflow that made it.</summary>
/// <param name="Notes">What the job couldn't put where the storyboard said, or other things worth knowing.</param>
public sealed record FilmMade(string File, string? Workflow, string? Template, TimeSpan Took, IReadOnlyList<string> Notes)
{
    /// <summary>How the song sounds to the judge, for a song it heard.</summary>
    public SongScore? Score { get; init; }
}

/// <summary>A job still running in ComfyUI when the wait for it ended (the user wrote meanwhile): asking for the same thing again waits for it.</summary>
public sealed class FilmStillRunningException(string message) : Exception(message);

/// <summary>
/// Makes a film's pieces with ComfyUI: each frame, each shot's clip and the song, every one with a
/// workflow of its own kept beside what it made, its seed fixed, so running it again makes the same
/// thing. Shared by the Filmmaking window and the film tool.
/// </summary>
public sealed partial class FilmMaker(ComfyHub hub, IMediaDecoder? media)
{
    /// <summary>How long a job is waited for at most; it goes on in ComfyUI after that.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromHours(3);

    /// <summary>Whether this maker queued any job: ComfyUI then lets go of the models when it is released.</summary>
    public bool RanJobs { get; private set; }

    private int _using;

    /// <summary>Marks the server as in use until <see cref="ReleaseAsync"/>, so a ComfyUI BlackBox started stays up between jobs.</summary>
    public void Use()
    {
        if (Interlocked.Exchange(ref _using, 1) == 0)
            hub.Use();
    }

    /// <summary>Done with the server: one BlackBox started is closed once its queue is empty (the user's own keeps running).</summary>
    public Task ReleaseAsync()
    {
        if (Interlocked.Exchange(ref _using, 0) == 0)
            return Task.CompletedTask;
        var ran = RanJobs;
        RanJobs = false;
        return hub.ReleaseAsync(ran);
    }

    // ---- frames ----------------------------------------------------------------------------------

    /// <summary>The prompt a frame is made with: its own (and an old storyboard's film-wide look, until it is upgraded).</summary>
    public static string FramePrompt(Storyboard board, FilmFrame frame) => Join(frame.Prompt, board.Style ?? "");

    /// <summary>The prompt a shot is made with: what happens and what the camera does (and an old storyboard's look).</summary>
    public static string ShotPrompt(Storyboard board, FilmShot shot) =>
        Join(Join(shot.Prompt, shot.Camera.Length > 0 ? "Camera: " + shot.Camera.TrimEnd('.') : ""), board.Style is { Length: > 0 } style ? "Style: " + style.TrimEnd('.') : "");

    private static string Join(string a, string b) => Storyboard.JoinText(a, b);

    /// <summary>
    /// A shot's prompt as the video model is given it: "No music." at the end unless it speaks of
    /// music. LTX makes a shot's sounds well and its music badly (the user's finding), and the film's
    /// music is its song. Not part of the shot's stamp, so shots made before stay up to date.
    /// </summary>
    internal static string WithoutMusic(string prompt) => MusicWords().IsMatch(prompt) ? prompt : Join(prompt, "No music.");

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(music|musical|song|sings?|singing|melody|band|piano|guitar|drums?|orchestra)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MusicWords();

    /// <summary>The kind of job a frame is: made from words, or from other frames.</summary>
    public static FilmJob JobOf(FilmFrame frame) => frame.From.Count > 0 ? FilmJob.Edit : FilmJob.Frame;

    /// <summary>The kind of job a shot is, by its frames and lip sync.</summary>
    public static FilmJob JobOf(FilmShot shot) =>
        shot.LipSync ? FilmJob.LipSync : shot.First is null ? FilmJob.ShotFromText : shot.Last is null ? FilmJob.ShotFromFirst : FilmJob.Shot;

    /// <summary>What a frame is made with: its own words, or an old storyboard's template for the job.</summary>
    public static string? ModelOf(Storyboard board, FilmFrame frame) => frame.Model ?? (board.Workflows is { } w ? FilmTemplates.Chosen(w, JobOf(frame)) : null);

    public static string? ModelOf(Storyboard board, FilmShot shot) => shot.Model ?? (board.Workflows is { } w ? FilmTemplates.Chosen(w, JobOf(shot)) : null);

    public static string? SongModelOf(Storyboard board) => board.Sound.Model ?? board.Workflows?.Song;

    /// <summary>Makes a frame's picture: taken from the user's file, else made from words, or from the frames it is based on.</summary>
    public async Task<FilmMade> MakeFrameAsync(Storyboard board, FilmFrame frame, Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        var target = board.FramePicture(frame);
        if (frame.Of is { } of && board.Shot(of) is { } before)
        {
            // Where the shot before's clip ends, as the clip has it.
            var started = DateTime.UtcNow;
            var clip = board.ShotVideo(before);
            if (!File.Exists(clip))
                throw new FilmException($"{frame.Label} is where shot {before.Id} ends, and shot {before.Id} isn't made yet: make it first.");
            if (media is null)
                throw new FilmException($"Nothing on this PC can read video to take {frame.Label} from shot {before.Id} (ffmpeg in BlackBox's Data\\tools would).");
            await Task.Run(() =>
            {
                using var reader = media.OpenVideo(clip, Math.Max(board.Width, board.Height)) ?? throw new FilmException($"{clip} has no picture this PC can read.");
                using var last = FilmCheck.LastFrame(reader) ?? throw new FilmException($"No frame of {clip} could be read.");
                var taken = Path.Combine(Path.GetTempPath(), $"blackbox-{Guid.NewGuid():N}.png");
                try
                {
                    using (var stream = File.Create(taken))
                        last.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
                    SaveFrame(taken, target, board.Width, board.Height);
                }
                finally
                {
                    File.Delete(taken);
                }
            }, ct).ConfigureAwait(false);
            WriteStamp(board.FrameWorkflow(frame), FrameStamp(board, frame), null, null);
            return new FilmMade(target, null, null, DateTime.UtcNow - started, []);
        }
        if (frame.File is { Length: > 0 } file)
        {
            var started = DateTime.UtcNow;
            if (!File.Exists(file))
                throw new FilmException($"{frame.Label}'s picture {file} isn't there.");
            await Task.Run(() => SaveFrame(file, target, board.Width, board.Height), ct).ConfigureAwait(false);
            WriteStamp(board.FrameWorkflow(frame), FrameStamp(board, frame), null, null);
            return new FilmMade(target, null, null, DateTime.UtcNow - started, []);
        }
        if (frame.Prompt.Trim().Length == 0)
            throw new FilmException($"{frame.Label} has no prompt: say what the picture shows, or give a picture of your own for it.");
        var pictures = new List<string>();
        foreach (var id in frame.From)
        {
            var from = board.Frame(id) ?? throw new FilmException($"{frame.Label} is to be made from \"{id}\", which isn't a frame of this film.");
            if (from.Id == frame.Id)
                continue;
            var picture = board.FramePicture(from);
            if (!File.Exists(picture))
                throw new FilmException($"{from.Label} isn't made yet, and {frame.Label} is made from it: make {from.Label} first.");
            pictures.Add(picture);
        }
        var inputs = new FilmInputs
        {
            Prompt = FramePrompt(board, frame),
            Seed = frame.Seed,
            Width = board.Width,
            Height = board.Height,
            Pictures = pictures,
            Prefix = Prefix(board, frame.Id),
        };
        var job = pictures.Count > 0 ? FilmJob.Edit : FilmJob.Frame;
        var run = await RunAsync(board, job, ModelOf(board, frame), inputs, frame.Set, board.FrameWorkflow(frame), FrameStamp(board, frame), "image", progress, userMessage, ct).ConfigureAwait(false);
        await Task.Run(() => SaveFrame(run.Output, target, board.Width, board.Height), ct).ConfigureAwait(false);
        FilmTakes.Remember(board.FrameWorkflow(frame), FilmPiece.Of(frame).Settings(board));
        return new FilmMade(target, board.FrameWorkflow(frame), run.Template, run.Took, run.Notes);
    }

    // ---- shots -------------------------------------------------------------------------------------

    /// <summary>
    /// Makes a shot's clip from its frames: first and last, first only, or words only; a lip-synced
    /// one from its first frame and the soundtrack's part under it.
    /// </summary>
    /// <param name="take">Saved as this file instead of the shot's own (one take of several).</param>
    public async Task<FilmMade> MakeShotAsync(Storyboard board, FilmShot shot, Action<string>? progress, CancellationToken userMessage, CancellationToken ct, string? take = null)
    {
        var first = board.Frame(shot.First);
        var last = board.Frame(shot.Last);
        string? Picture(FilmFrame? frame)
        {
            if (frame is null)
                return null;
            var path = board.FramePicture(frame);
            return File.Exists(path) ? path : throw new FilmException($"{frame.Label} isn't made yet, and shot {shot.Id} {(frame == first ? "starts" : "ends")} on it: make it first.");
        }
        var (firstPicture, lastPicture) = (Picture(first), Picture(last));
        var notes = new List<string>();
        string? audio = null;
        FilmJob job;
        if (shot.LipSync)
        {
            if (firstPicture is null)
                throw new FilmException($"Shot {shot.Id} is lip-synced, which needs its first frame to show who sings or speaks: give it one.");
            audio = board.ShotAudio(shot);
            if (Storyboard.Speaks(shot))
            {
                // Its own line, said a moment after the shot starts (a head turns to the camera first).
                if (!LineIsCurrent(board, shot))
                    notes.AddRange((await MakeLineAsync(board, shot, progress, userMessage, ct).ConfigureAwait(false)).Notes);
                var said = await Task.Run(() => PlaceSound(board.ShotLine(shot), LineLead, shot.Seconds, audio), ct).ConfigureAwait(false);
                if (said + LineLead > shot.Seconds + 0.05)
                    notes.Add($"Its line takes {said:0.0} s, and with the {LineLead:0.0} s before it the shot is {shot.Seconds:0.#} s: the end of the line is cut. Make the shot longer or the line shorter.");
            }
            else
            {
                var soundtrack = board.SoundtrackFile ?? throw new FilmException($"Shot {shot.Id} is lip-synced to the soundtrack, and there is none yet: " +
                                                                                 (board.Sound.Source == FilmSound.Song ? "make the song first." : "give it a line, or give the film a soundtrack first."));
                var (starts, _) = board.Timing();
                var start = starts[board.Shots.IndexOf(shot)];
                await Task.Run(() => CutSound(soundtrack, start, shot.Seconds, audio), ct).ConfigureAwait(false);
            }
            job = FilmJob.LipSync;
            if (lastPicture is not null)
                notes.Add($"A lip-synced shot is made from its first frame only, so it doesn't end on {last!.Label}.");
            lastPicture = null;
        }
        else
        {
            // The video model animates a picture well and draws one badly: a picture model makes it first.
            if (firstPicture is null)
                throw new FilmException(first is null
                    ? $"Shot {shot.Id} has no first frame: a picture model makes the picture a shot starts on, and the video model animates it. Give it a first frame."
                    : $"Shot {shot.Id} starts on {first.Label}, which isn't made yet: make it first.");
            job = lastPicture is null ? FilmJob.ShotFromFirst : FilmJob.Shot;
        }
        var inputs = new FilmInputs
        {
            Prompt = shot.LipSync ? ShotPrompt(board, shot) : WithoutMusic(ShotPrompt(board, shot)),
            Seed = shot.Seed,
            Width = board.Width,
            Height = board.Height,
            Seconds = shot.Seconds,
            Fps = board.Fps,
            FirstImage = firstPicture,
            LastImage = lastPicture,
            Audio = audio,
            TextToVideo = firstPicture is null,
            Prefix = Prefix(board, shot.Id),
        };
        var target = take ?? board.ShotVideo(shot);
        var workflow = take is null ? board.ShotWorkflow(shot) : Path.ChangeExtension(take, ".json");
        var run = await RunAsync(board, job, ModelOf(board, shot), inputs, shot.Set, workflow, ShotStamp(board, shot), "video", progress, userMessage, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(run.Output, target, overwrite: true);
        if (VideoSteady.AsksStill(shot.Camera + "\n" + shot.Prompt) && await HoldStillAsync(target, ct).ConfigureAwait(false) is { } held)
            notes.Add(held);
        FilmTakes.Remember(workflow, FilmPiece.Of(shot).Settings(board));
        return new FilmMade(target, workflow, run.Template, run.Took, [.. notes, .. run.Notes]);
    }

    /// <summary>
    /// A clip whose camera moved where its shot holds it still, held still after the fact
    /// (<see cref="VideoSteady"/>): LTX pushed in, panned or tilted on every locked-off shot of a
    /// night of films. What was done, for the notes; null when the camera held still or nothing could be read.
    /// </summary>
    private async Task<string?> HoldStillAsync(string clip, CancellationToken ct)
    {
        if (media is null || media.Ffmpeg is not { } ffmpeg)
            return null;
        var path = await Task.Run(() =>
        {
            using var reader = media.OpenVideo(clip, 512);
            return reader is null ? null : VideoSteady.Measure(VideoMotion.Read(reader));
        }, ct).ConfigureAwait(false);
        if (path is not { Moved: true })
            return null;
        var held = Path.Combine(Path.GetDirectoryName(clip)!, Path.GetFileNameWithoutExtension(clip) + ".held.mp4");
        try
        {
            if (!await VideoSteady.HoldAsync(clip, held, path, media, ffmpeg, ct).ConfigureAwait(false))
                return $"The camera {path.Describe()} where the shot holds it still, too far to hold still after the fact.";
            File.Move(held, clip, overwrite: true);
            return $"The camera {path.Describe()} where the shot holds it still: held it still, the picture enlarged {(path.Enlarged - 1) * 100:0}%.";
        }
        catch (IOException ex)
        {
            return $"The camera {path.Describe()} where the shot holds it still, and holding it still failed: {ex.Message}";
        }
        finally
        {
            if (File.Exists(held))
                File.Delete(held);
        }
    }

    // ---- lines -------------------------------------------------------------------------------------

    /// <summary>How long into a speaking shot its line starts: a moment to turn to the camera, or to breathe.</summary>
    public const double LineLead = 0.4;

    /// <summary>
    /// Says a shot's line in the film's voice with VoxCPM2, ComfyUI's text-to-speech model here. The
    /// first speaking shot's line is said from the voice's description; the others clone that
    /// line's voice, so every line sounds like the same person.
    /// </summary>
    public async Task<FilmMade> MakeLineAsync(Storyboard board, FilmShot shot, Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        if (!Storyboard.Speaks(shot))
            throw new FilmException($"Shot {shot.Id} has no line to say.");
        var firstSpeaker = board.Shots.First(Storyboard.Speaks);
        string? reference = null;
        if (firstSpeaker != shot)
        {
            if (!LineIsCurrent(board, firstSpeaker))
                await MakeLineAsync(board, firstSpeaker, progress, userMessage, ct).ConfigureAwait(false);
            reference = board.ShotLine(firstSpeaker);
        }
        Use();
        var notes = new List<string>();
        var api = await hub.ConnectAsync(null, notes, progress, ct).ConfigureAwait(false);
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        var workflow = SpeechWorkflow(library, shot.Line.Trim(), board.Sound.Voice.Trim(), board.Sound.Seed, reference, firstSpeaker.Line.Trim(), Prefix(board, shot.Id + "-line"))
                       ?? throw new FilmException("Saying a line needs VoxCPM2's nodes in ComfyUI (the ComfyUI-VoxCPM custom node pack), and they aren't installed: " +
                                                  "install the pack (it downloads its model on first use), or give the film a soundtrack of its own to lip-sync to.");
        workflow.FillAdvancedDefaults(library);
        var stamp = LineStamp(board, shot);
        var run = await RunWorkflowAsync(api, library, workflow, board.ShotLineWorkflow(shot), stamp, "VoxCPM2", "audio", progress, userMessage, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(board.ShotLine(shot))!);
        File.Copy(run.Output, board.ShotLine(shot), overwrite: true);
        return new FilmMade(board.ShotLine(shot), board.ShotLineWorkflow(shot), "VoxCPM2", run.Took, [.. notes, .. run.Notes]);
    }

    /// <summary>
    /// VoxCPM2 saying <paramref name="text"/>: from a description of the voice, or cloning the voice
    /// of <paramref name="reference"/> (which says <paramref name="referenceText"/>). Null when its nodes aren't installed.
    /// </summary>
    internal static ComfyWorkflow? SpeechWorkflow(ComfyNodeLibrary library, string text, string voice, long seed, string? reference, string referenceText, string prefix)
    {
        var node = reference is null ? "VoxCPM2_TTS" : "VoxCPM2_Clone";
        if (library.Find(node) is not { } def || library.Find("SaveAudio") is null)
            return null;
        var model = def.Input("model_name")?.Choices?.FirstOrDefault(c => c.Contains("VoxCPM2", StringComparison.OrdinalIgnoreCase)) ?? def.Input("model_name")?.Choices?.FirstOrDefault() ?? "VoxCPM2";
        var inputs = new JsonObject
        {
            ["model_name"] = model,
            ["lora_name"] = "None",
            ["voice_description"] = voice,
            ["text"] = text,
            ["seed"] = seed % (1L << 31),
        };
        var nodes = new JsonObject
        {
            ["1"] = new JsonObject { ["class_type"] = node, ["inputs"] = inputs, ["_meta"] = new JsonObject { ["title"] = "Say the line" } },
            ["2"] = new JsonObject { ["class_type"] = "SaveAudio", ["inputs"] = new JsonObject { ["audio"] = new JsonArray("1", 0), ["filename_prefix"] = prefix } },
        };
        if (reference is not null)
        {
            // The first line, and what it says, give the voice to clone.
            nodes["3"] = new JsonObject { ["class_type"] = "LoadAudio", ["inputs"] = new JsonObject { ["audio"] = reference }, ["_meta"] = new JsonObject { ["title"] = "The voice to keep" } };
            inputs["reference_audio"] = new JsonArray("3", 0);
            inputs["prompt_text"] = referenceText;
            // A description here steers the delivery, not the voice; the same one keeps the tone.
        }
        foreach (var input in def.Inputs.Where(i => i.Required && !inputs.ContainsKey(i.Name) && i.IsWidget && i.Extra["default"] is not null))
            inputs[input.Name] = input.Extra["default"]!.DeepClone();
        return ComfyWorkflow.FromApi(nodes);
    }

    public static bool LineIsCurrent(Storyboard board, FilmShot shot) =>
        File.Exists(board.ShotLine(shot)) && MadeStamp(board.ShotLineWorkflow(shot)) is { } made && made == LineStamp(board, shot);

    public static string LineStamp(Storyboard board, FilmShot shot)
    {
        var first = board.Shots.FirstOrDefault(Storyboard.Speaks);
        var reference = first is null || first == shot ? "" : $"{first.Line.Trim()}|{FileStamp(board.ShotLine(first))}";
        return Hash($"{shot.Line.Trim()}|{board.Sound.Voice.Trim()}|{board.Sound.Seed}|{reference}");
    }

    /// <summary>
    /// A sound placed <paramref name="at"/> seconds into a WAV <paramref name="seconds"/> long, silence
    /// around it (cut where it runs past the end); returns how long the sound is.
    /// </summary>
    public double PlaceSound(string sound, double at, double seconds, string target)
    {
        using var reader = media?.OpenAudio(sound) ?? throw new FilmException($"{sound} can't be read on this PC (ffmpeg in BlackBox's Data\\tools would).");
        var (rate, channels) = (reader.SampleRate, Math.Max(1, reader.Channels));
        var samples = new float[(long)Math.Round(seconds * rate) * channels];
        var offset = (long)Math.Round(at * rate) * channels;
        var buffer = new float[8192 * channels];
        long read = 0;
        int got;
        while ((got = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var i = 0; i < got && offset + read + i < samples.Length; i++)
                samples[offset + read + i] = buffer[i];
            read += got;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        WriteWav(target, samples, rate, channels);
        return read / (double)channels / rate;
    }

    // ---- the song -----------------------------------------------------------------------------------

    /// <summary>
    /// The shortest song made: ACE-Step 1.5 makes music that falls apart when asked for a short one.
    /// For the same style and lyrics, songs of 21 s scored 2.4 to 2.9 of 5 on SongEval (the user called
    /// them garbage), 30 s 3.2, 45 to 90 s 3.8 to 4.4. The film uses the song's start and fades it out.
    /// </summary>
    public const double MinSongSeconds = 60;

    /// <summary>How long the song is made: the film's length and a little more, a minute at least.</summary>
    public static double SongSeconds(Storyboard board) => Math.Max(MinSongSeconds, Math.Ceiling(board.Timing().Length) + 2);

    /// <summary>Hears how good a song is, to keep the best of several takes; null when it can't run here.</summary>
    public SongJudge? Judge { get; init; }

    /// <summary>
    /// Makes the soundtrack: a song in the storyboard's style, with its lyrics (an instrumental
    /// without), a minute long at least. With a judge, up to <paramref name="takes"/> songs with new
    /// seeds, each heard for the part the film uses, and the best kept (its seed in the storyboard,
    /// which the caller saves): the same settings scored 3.3 to 4.2 by the seed alone.
    /// </summary>
    public async Task<FilmMade> MakeSongAsync(Storyboard board, Action<string>? progress, CancellationToken userMessage, CancellationToken ct, int takes = 1)
    {
        var sound = board.Sound;
        if (sound.Tags.Trim().Length == 0)
            throw new FilmException("The song has no style: say what it should sound like (genre, instruments, voice, tempo).");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var python = Judge is null ? null : SongJudge.PythonOf(hub);
        var ffmpeg = media?.Ffmpeg;
        if (python is null || ffmpeg is null)
            takes = 1;
        var made = new List<(long Seed, string File, string Workflow, Run Run)>();
        for (var take = 1; take <= Math.Max(1, takes); take++)
        {
            if (take > 1)
                sound.Seed = Storyboard.NewSeed();
            var inputs = new FilmInputs
            {
                Tags = sound.Tags.Trim(),
                Lyrics = sound.Lyrics.Trim().Length > 0 ? sound.Lyrics.Trim() : "[Instrumental]",
                Bpm = Bpm(sound.Tags),
                Seed = sound.Seed,
                Seconds = SongSeconds(board),
                Prefix = Prefix(board, "song"),
            };
            var (file, workflow) = takes <= 1 ? (board.SongFile, board.SongWorkflow)
                : (Path.Combine(board.Folder, "sound", $"song.take{take}.mp3"), Path.Combine(board.Folder, "sound", $"song.take{take}.json"));
            var run = await RunAsync(board, FilmJob.Song, SongModelOf(board), inputs, sound.Set, workflow, SongStamp(board), "audio",
                takes <= 1 ? progress : s => progress?.Invoke($"Song {take} of {takes}: {s}"), userMessage, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Copy(run.Output, file, overwrite: true);
            made.Add((sound.Seed, file, workflow, run));
        }

        // Each heard for the part of it the film uses.
        Dictionary<string, SongScore>? scores = null;
        var notes = made.SelectMany(m => m.Run.Notes).Distinct().ToList();
        if (python is not null && ffmpeg is not null)
        {
            try
            {
                scores = await Judge!.ScoreAsync(made.Select(m => m.File).ToList(), python, ffmpeg, board.Timing().Length, progress, ct).ConfigureAwait(false);
            }
            catch (FilmException ex)
            {
                notes.Add(ex.Message);
            }
        }
        var best = made.MaxBy(m => scores?.GetValueOrDefault(m.File)?.Musicality ?? 0);
        sound.Seed = best.Seed;
        if (made.Count > 1)
        {
            File.Copy(best.File, board.SongFile, overwrite: true);
            File.Copy(best.Workflow, board.SongWorkflow, overwrite: true);
            notes.Insert(0, $"Kept song {made.IndexOf(best) + 1} of {made.Count} (seed {best.Seed}); musicality of each: " +
                            string.Join(", ", made.Select(m => scores?.GetValueOrDefault(m.File) is { } s ? s.Musicality.ToString("0.0", CultureInfo.InvariantCulture) : "?")) + ".");
        }
        FilmTakes.Remember(board.SongWorkflow, FilmPiece.Song.Settings(board));
        return new FilmMade(board.SongFile, board.SongWorkflow, best.Run.Template, clock.Elapsed, notes) { Score = scores?.GetValueOrDefault(best.File) };
    }

    /// <summary>The tempo a song's style names ("70 BPM"), for ACE-Step's own setting.</summary>
    internal static int? Bpm(string tags) =>
        System.Text.RegularExpressions.Regex.Match(tags, @"(\d{2,3})\s*bpm", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m &&
        int.TryParse(m.Groups[1].Value, out var bpm) && bpm is >= 30 and <= 300 ? bpm : null;

    // ---- running a job --------------------------------------------------------------------------------

    /// <summary>Where ComfyUI saves a film's files, under its output folder.</summary>
    private static string Prefix(Storyboard board, string id) => $"BlackBox films/{Storyboard.Safe(board.Title.Length > 0 ? board.Title : Path.GetFileName(board.Folder))}/{id}";

    private sealed record Run(string Output, string Template, TimeSpan Took, List<string> Notes);

    /// <summary>A job queued in ComfyUI and not yet picked up: by the workflow file it is for.</summary>
    private sealed record Pending(string PromptId, string ClientId, string Stamp, string Server, IReadOnlyDictionary<string, string> Titles, string Template, DateTime Queued);

    private static readonly ConcurrentDictionary<string, Pending> Running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the template for a job with the inputs set, and saves the workflow as it ran beside the
    /// result (with the files it loads by their full paths, so the comfyui tool can run it again).
    /// </summary>
    /// <param name="model">What it is to be made with, in words or by name (<see cref="FilmFrame.Model"/>); null: the best installed.</param>
    private async Task<Run> RunAsync(Storyboard board, FilmJob job, string? model, FilmInputs inputs, JsonObject? extra, string workflowFile, string stamp, string kind,
        Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        Use();
        var notes = new List<string>();
        var api = await hub.ConnectAsync(null, notes, progress, ct).ConfigureAwait(false);
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        progress?.Invoke(model is null ? "Getting the workflow ready…" : $"Finding \"{model}\" among what is installed…");
        var (template, note) = await FilmTemplates.PickAsync(hub, api, model, job, ct).ConfigureAwait(false);
        if (note is not null)
            notes.Add(note);
        if (template is null)
            throw new FilmException($"No ComfyUI template for {FilmTemplates.Describe(job)} is ready on this PC: " +
                                    "every one this needs a model or node that isn't installed. The comfyui tool finds templates (action=templates) and downloads what they need.");
        if (!template.Ready)
            throw new FilmException($"The template {template.Template.Name} needs {string.Join(", ", template.Missing.Take(6))}, which isn't installed: " +
                                    "download it (the comfyui tool's action=templates with its name gives the links), or pick another template for this.");
        var workflow = template.Workflow;
        var missed = new List<string>();
        var set = FilmRoles.Apply(workflow, library, inputs, missed);
        // What the job can't go without stops it; the rest is said.
        var vital = missed.Where(m => m.StartsWith("the prompt", StringComparison.Ordinal) || m.StartsWith("the first frame", StringComparison.Ordinal) ||
                                      m.StartsWith("the last frame", StringComparison.Ordinal) || m.StartsWith("the sound", StringComparison.Ordinal)).ToList();
        if (vital.Count > 0)
            throw new FilmException($"The template {template.Template.Name} has no place for {string.Join(", ", vital)}: pick another template for this job, " +
                                    "or say where it goes with set {\"node id.input\": value}.");
        notes.AddRange(missed.Select(m => $"The template {template.Template.Name} had no place for {m}."));
        if (extra is { Count: > 0 } && workflow.Set(extra, library) is { Count: > 0 } wrong)
            throw new FilmException($"These changes don't fit the template {template.Template.Name}:\n- " + string.Join("\n- ", wrong));
        workflow.FillAdvancedDefaults(library);
        var run = await RunWorkflowAsync(api, library, workflow, workflowFile, stamp, template.Template.Name, kind, progress, userMessage, ct).ConfigureAwait(false);
        return run with { Notes = [.. notes, .. run.Notes] };
    }

    /// <summary>
    /// Runs a workflow ready to go, its workflow file written first (with the files it loads by
    /// their full paths): waits for it, and finds what it made of the kind wanted.
    /// </summary>
    private async Task<Run> RunWorkflowAsync(ComfyApi api, ComfyNodeLibrary library, ComfyWorkflow workflow, string workflowFile, string stamp, string templateName, string kind,
        Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var notes = new List<string>();
        // The same job asked for again while it still runs (the wait for it ended when the user wrote): wait for it.
        if (Running.TryGetValue(workflowFile, out var pending))
        {
            if (pending.Stamp == stamp && pending.Server == api.BaseUrl.ToString())
                return await FinishAsync(api, pending, kind, workflowFile, notes, progress, userMessage, ct).ConfigureAwait(false);
            // Asked for with other settings since: the old one is of no use.
            Running.TryRemove(workflowFile, out _);
            try { await api.CancelAsync(pending.PromptId, ct).ConfigureAwait(false); }
            catch (ComfyApiException) { }
        }
        WriteStamp(workflowFile, stamp, templateName, workflow);

        // The files it loads go into ComfyUI's input folder.
        var uploaded = new HashSet<string>();
        foreach (var (id, input, path) in workflow.LocalFiles(library, p => p))
        {
            if (!File.Exists(path))
                throw new FilmException($"{path} isn't there: node {id} loads it.");
            progress?.Invoke($"Uploading {Path.GetFileName(path)}…");
            ((JsonObject)workflow.Nodes[id]!["inputs"]!)[input] = await api.UploadAsync(path, ct).ConfigureAwait(false);
            uploaded.Add(id + "." + input);
        }
        var problems = workflow.Validate(library, uploaded);
        if (problems.Count > 0)
            throw new FilmException($"The workflow from {templateName} can't run:\n- " + string.Join("\n- ", problems.Take(12)));
        var clientId = "blackbox-" + Guid.NewGuid().ToString("N")[..12];
        var (promptId, refused) = await ComfyRunner.QueueAsync(api, workflow, clientId, ct).ConfigureAwait(false);
        if (promptId is null)
            throw new FilmException($"ComfyUI refused the workflow from {templateName}:\n- " + string.Join("\n- ", refused));
        RanJobs = true;
        var titles = workflow.All().ToDictionary(n => n.Id, n => ComfyWorkflow.TitleOf(n.Node) ?? ComfyWorkflow.ClassOf(n.Node));
        hub.RememberJob(promptId, clientId, titles);
        var queued = new Pending(promptId, clientId, stamp, api.BaseUrl.ToString(), titles, templateName, DateTime.UtcNow);
        Running[workflowFile] = queued;
        var run = await FinishAsync(api, queued, kind, workflowFile, notes, progress, userMessage, ct).ConfigureAwait(false);
        return run with { Took = clock.Elapsed };
    }

    /// <summary>Waits for a queued job and finds what it made of the kind wanted.</summary>
    private async Task<Run> FinishAsync(ComfyApi api, Pending pending, string kind, string workflowFile, List<string> notes,
        Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        ComfyJob job;
        try
        {
            job = await ComfyRunner.WaitAsync(api, pending.PromptId, pending.ClientId, pending.Titles, MaxWait, progress, userMessage, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Running.TryRemove(workflowFile, out _); // the wait cancelled the job in ComfyUI
            throw;
        }
        if (job.Status is "running" or "queued")
            throw new FilmStillRunningException($"It is still being made in ComfyUI ({job.Progress ?? job.Status}, {ComfyRunner.Clock(DateTime.UtcNow - pending.Queued)} so far)" +
                                                ": asking for the same thing again waits for it.");
        Running.TryRemove(workflowFile, out _);
        if (job.Status != "success")
            throw new FilmException($"ComfyUI's job from {pending.Template} {(job.Status == "interrupted" ? "was stopped" : "failed")}: {job.Error}");
        var dirs = await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) ?? [];
        var files = await ComfyRunner.LocateAsync(api, job.Files, dirs, Path.Combine(Path.GetTempPath(), "BlackBox films"), ct).ConfigureAwait(false);
        var made = files.Where(f => f.LocalPath is not null && f.Kind == kind || kind == "image" && f.Kind == "animation" && f.LocalPath is not null).ToList();
        var saved = made.Where(f => f.Type == "output").ToList();
        var pick = (saved.Count > 0 ? saved : made).MaxBy(f => new FileInfo(f.LocalPath!).Length)
                   ?? throw new FilmException($"The job from {pending.Template} made no {kind} (it made {(files.Count == 0 ? "nothing" : string.Join(", ", files.Select(f => f.Filename)))}).");
        return new Run(pick.LocalPath!, pending.Template, job.Elapsed, notes);
    }

    // ---- what each piece was made from ----------------------------------------------------------------

    /// <summary>
    /// What a frame's picture comes from, as a short hash: its prompt, seed, size and what it is made
    /// with, its user's file, the pictures it is based on. A frame whose stamp differs from the one it
    /// was made with is out of date. Only the frame's own settings and the film's size go in: a
    /// change to one frame leaves the others made.
    /// </summary>
    public static string FrameStamp(Storyboard board, FilmFrame frame)
    {
        var sb = new StringBuilder();
        if (frame.Of is { } of && board.Shot(of) is { } before)
            sb.Append("of|").Append(of).Append('|').Append(FileStamp(board.ShotVideo(before)));
        else if (frame.File is { Length: > 0 } file)
            sb.Append("file|").Append(file).Append('|').Append(FileStamp(file));
        else
        {
            sb.Append(FramePrompt(board, frame)).Append('|').Append(frame.Seed).Append('|')
              .Append(ModelOf(board, frame)).Append('|').Append(frame.Set?.ToJsonString());
            foreach (var id in frame.From)
                sb.Append("|from ").Append(id).Append(' ').Append(board.Frame(id) is { } from ? FileStamp(board.FramePicture(from)) : "");
        }
        sb.Append('|').Append(board.Width).Append('x').Append(board.Height);
        return Hash(sb.ToString());
    }

    public static string ShotStamp(Storyboard board, FilmShot shot)
    {
        var sb = new StringBuilder();
        sb.Append(ShotPrompt(board, shot)).Append('|').Append(shot.Seed).Append('|').Append(shot.Seconds).Append('|').Append(board.Fps).Append('|')
          .Append(board.Width).Append('x').Append(board.Height).Append('|').Append(shot.LipSync).Append('|').Append(shot.Set?.ToJsonString());
        foreach (var id in new[] { shot.First, shot.LipSync ? null : shot.Last })
            sb.Append('|').Append(board.Frame(id) is { } frame ? FileStamp(board.FramePicture(frame)) : "none");
        sb.Append('|').Append(ModelOf(board, shot));
        if (Storyboard.Speaks(shot))
            sb.Append("|line ").Append(LineStamp(board, shot)).Append(' ').Append(LineLead);
        else if (shot.LipSync)
        {
            var (starts, _) = board.Timing();
            sb.Append("|sound ").Append(board.SoundtrackFile is { } s ? FileStamp(s) : "").Append(' ').Append(starts[Math.Max(0, board.Shots.IndexOf(shot))]);
        }
        return Hash(sb.ToString());
    }

    public static string SongStamp(Storyboard board) =>
        Hash($"{board.Sound.Tags}|{board.Sound.Lyrics}|{board.Sound.Seed}|{SongSeconds(board)}|{SongModelOf(board)}|{board.Sound.Set?.ToJsonString()}");

    /// <summary>
    /// Upgrades a storyboard from before frames and shots had all of their own look and model
    /// (<see cref="Storyboard.Upgrade"/>), and saves it: what was made and up to date before stays so,
    /// its stamp written again for its settings' new place.
    /// </summary>
    public static void Upgrade(Storyboard board)
    {
        var frames = board.Frames.Where(f => IsCurrent(board, f)).ToList();
        var shots = board.Shots.Where(s => IsCurrent(board, s)).ToList();
        var song = SongIsCurrent(board);
        board.Upgrade();
        foreach (var f in frames)
            Restamp(board.FrameWorkflow(f), FrameStamp(board, f));
        foreach (var s in shots)
            Restamp(board.ShotWorkflow(s), ShotStamp(board, s));
        if (song)
            Restamp(board.SongWorkflow, SongStamp(board));
        try
        {
            board.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Gives a made piece's workflow file a new stamp (when it differs).</summary>
    private static void Restamp(string workflowFile, string stamp)
    {
        try
        {
            if (MadeStamp(workflowFile) == stamp || JsonNode.Parse(File.ReadAllText(workflowFile)) is not JsonObject json || json["blackbox"] is not JsonObject head)
                return;
            head["stamp"] = stamp;
            Settings.AtomicFile.Write(workflowFile, json.ToJsonString(ComfyWorkflow.Indented));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { }
    }

    private static string FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Length}@{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>The stamp a piece was made with, kept in its workflow file; null when it wasn't made (or by hand).</summary>
    public static string? MadeStamp(string workflowFile)
    {
        try
        {
            return File.Exists(workflowFile) && JsonNode.Parse(File.ReadAllText(workflowFile))?["blackbox"]?["stamp"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the workflow a piece is made with beside it, with what it was made from: ComfyUI's
    /// API format under "prompt", which the comfyui tool runs as it is (workflow_file).
    /// </summary>
    private static void WriteStamp(string workflowFile, string stamp, string? template, ComfyWorkflow? workflow)
    {
        var json = new JsonObject
        {
            ["blackbox"] = new JsonObject { ["stamp"] = stamp, ["template"] = template, ["made"] = DateTime.UtcNow.ToString("O") },
        };
        if (workflow is not null)
            json["prompt"] = JsonNode.Parse(workflow.ToJson());
        Settings.AtomicFile.Write(workflowFile, json.ToJsonString(ComfyWorkflow.Indented));
    }

    /// <summary>Whether a frame's picture is there and was made from what the storyboard says now.</summary>
    public static bool IsCurrent(Storyboard board, FilmFrame frame) =>
        File.Exists(board.FramePicture(frame)) && MadeStamp(board.FrameWorkflow(frame)) is { } made && made == FrameStamp(board, frame);

    public static bool IsCurrent(Storyboard board, FilmShot shot) =>
        File.Exists(board.ShotVideo(shot)) && MadeStamp(board.ShotWorkflow(shot)) is { } made && made == ShotStamp(board, shot);

    public static bool SongIsCurrent(Storyboard board) =>
        File.Exists(board.SongFile) && MadeStamp(board.SongWorkflow) is { } made && made == SongStamp(board);

    // ---- pictures and sound --------------------------------------------------------------------------------

    /// <summary>
    /// A picture scaled to cover the film's frame and cut to it in the middle, saved as PNG: every
    /// frame of a film has its size, whatever made it or wherever it came from.
    /// </summary>
    public static void SaveFrame(string source, string target, int width, int height)
    {
        using var bitmap = SKBitmap.Decode(source) ?? throw new FilmException($"{source} isn't a picture this can read.");
        var inside = Bars(bitmap);
        using var frame = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(frame))
        {
            canvas.Clear(SKColors.Black);
            var scale = Math.Max(width / (double)inside.Width, height / (double)inside.Height);
            var (w, h) = ((float)(inside.Width * scale), (float)(inside.Height * scale));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(bitmap, inside, new SKRect((width - w) / 2, (height - h) / 2, (width + w) / 2, (height + h) / 2), paint);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        using (var data = frame.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(temp))
            data.SaveTo(stream);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// The picture inside black bars along its edges, if it has some. Asked for a close-up "in a
    /// cinematic 35mm look", Qwen-Image-Edit drew the picture letterboxed, and LTX kept the bars
    /// through the whole clip; a film's frame is the picture all over.
    /// </summary>
    internal static SKRectI Bars(SKBitmap bitmap)
    {
        var (w, h) = (bitmap.Width, bitmap.Height);
        bool Dark(int x0, int y0, int dx, int dy, int count)
        {
            var (samples, dark) = (0, 0);
            var step = Math.Max(1, count / 64);
            for (var i = 0; i < count; i += step)
            {
                var c = bitmap.GetPixel(x0 + dx * i, y0 + dy * i);
                samples++;
                dark += c.Red + c.Green + c.Blue < 3 * 18 ? 1 : 0;
            }
            return dark >= samples * 0.97;
        }
        bool DarkRow(int y) => Dark(0, y, 1, 0, w);
        bool DarkColumn(int x) => Dark(x, 0, 0, 1, h);
        int top = 0, bottom = 0, left = 0, right = 0;
        while (top < h * 0.3 && DarkRow(top)) top++;
        while (bottom < h * 0.3 && DarkRow(h - 1 - bottom)) bottom++;
        while (left < w * 0.3 && DarkColumn(left)) left++;
        while (right < w * 0.3 && DarkColumn(w - 1 - right)) right++;
        // A band of a few rows is a dark picture's edge, not a bar; one past 30% is a dark picture.
        if (top + bottom < h * 0.03 || top >= h * 0.3 || bottom >= h * 0.3) top = bottom = 0;
        if (left + right < w * 0.03 || left >= w * 0.3 || right >= w * 0.3) left = right = 0;
        return new SKRectI(left, top, w - right, h - bottom);
    }

    /// <summary>
    /// The part of the soundtrack from <paramref name="start"/> for <paramref name="seconds"/>, as a
    /// WAV file (silence where the soundtrack is shorter): what a lip-synced shot is made with.
    /// </summary>
    public void CutSound(string soundtrack, double start, double seconds, string target)
    {
        using var reader = media?.OpenAudio(soundtrack) ?? throw new FilmException($"The soundtrack {soundtrack} can't be read on this PC (ffmpeg in BlackBox's Data\\tools would).");
        var (rate, channels) = (reader.SampleRate, Math.Max(1, reader.Channels));
        if (start > 0)
            reader.Seek(TimeSpan.FromSeconds(start));
        var wanted = (long)Math.Round(seconds * rate) * channels;
        var samples = new float[wanted];
        long got = 0;
        var buffer = new float[8192 * channels];
        while (got < wanted)
        {
            var read = reader.Read(buffer, 0, (int)Math.Min(buffer.Length, wanted - got));
            if (read <= 0)
                break;
            Array.Copy(buffer, 0, samples, got, read);
            got += read;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        WriteWav(target, samples, rate, channels);
    }

    /// <summary>Samples (interleaved, -1 to 1) as a 16-bit PCM WAV file.</summary>
    internal static void WriteWav(string path, float[] samples, int rate, int channels)
    {
        using var file = new BinaryWriter(File.Create(path));
        var bytes = samples.Length * 2;
        file.Write("RIFF"u8);
        file.Write(36 + bytes);
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)channels);
        file.Write(rate);
        file.Write(rate * channels * 2);
        file.Write((short)(channels * 2));
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(bytes);
        foreach (var s in samples)
            file.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * 32767));
    }
}
