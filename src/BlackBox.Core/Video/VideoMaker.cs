using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Media;
using SkiaSharp;

namespace BlackBox.Core.Video;

/// <summary>What a video job made: the file in the video's folder, and the workflow that made it.</summary>
/// <param name="Notes">What the job couldn't put where the storyboard said, or other things worth knowing.</param>
public sealed record VideoMade(string File, string? Meta, string? Template, TimeSpan Took, IReadOnlyList<string> Notes)
{
    /// <summary>How the song sounds to the judge, for a song it heard.</summary>
    public SongScore? Score { get; init; }
}

/// <summary>A job still running in ComfyUI when the wait for it ended (the user wrote meanwhile): asking for the same thing again waits for it.</summary>
public sealed class VideoStillRunningException(string message) : Exception(message);

/// <summary>
/// Makes a video's pieces with ComfyUI: each frame, each shot's clip and the songs, every one with a
/// workflow of its own kept beside what it made, its seed fixed, so running it again makes the same
/// thing. Shared by the Video generator panel and the video tool.
/// </summary>
public sealed partial class VideoMaker(ComfyHub hub, IMediaDecoder? media)
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

    /// <summary>The prompt a frame is made with: its own.</summary>
    public static string FramePrompt(VideoFrame frame) => frame.Prompt.Trim();

    /// <summary>The prompt a shot is made with: what happens and what the camera does.</summary>
    public static string ShotPrompt(VideoShot shot) =>
        Join(shot.Prompt, shot.Camera.Length > 0 ? "Camera: " + shot.Camera.TrimEnd('.') : "");

    private static string Join(string a, string b) => Storyboard.JoinText(a, b);

    /// <summary>
    /// A shot's prompt as the video model is given it: "No music." at the end unless it speaks of
    /// music. LTX makes a shot's sounds well and its music badly (the user's finding), and the video's
    /// music is a song on its sound lines. Not part of the shot's stamp, so shots made before stay up to date.
    /// </summary>
    internal static string WithoutMusic(string prompt) => MusicWords().IsMatch(prompt) ? prompt : Join(prompt, "No music.");

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(music|musical|song|sings?|singing|melody|band|piano|guitar|drums?|orchestra)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex MusicWords();

    /// <summary>The kind of job a frame is: made from words, or from other frames.</summary>
    public static VideoJob JobOf(VideoFrame frame) => frame.From.Count > 0 ? VideoJob.Edit : VideoJob.Frame;

    /// <summary>The kind of job a shot is, by its frames and lip sync.</summary>
    public static VideoJob JobOf(VideoShot shot) =>
        shot.LipSync ? VideoJob.LipSync : shot.Motion is not null ? VideoJob.Motion : shot.First is null ? VideoJob.ShotFromText : shot.Last is null ? VideoJob.ShotFromFirst : VideoJob.Shot;


    /// <summary>Makes a frame's picture: taken from the user's file, else made from words, or from the frames it is based on.</summary>
    public async Task<VideoMade> MakeFrameAsync(Storyboard board, VideoFrame frame, Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        using var making = MakingIn(board);
        var target = board.FramePicture(frame);
        if (frame.Of is { } of && (board.Shot(of) is not null || board.ReferenceVideo(of) is not null || Storyboard.IsVideoFile(of)))
        {
            // Where the shot before's clip ends, as the clip has it; or where a video of the user's ends.
            var started = DateTime.UtcNow;
            var before = board.Shot(of);
            var clip = before is not null ? board.ShotVideo(before) : board.ReferenceVideo(of)
                       ?? throw new VideoException($"{frame.Label} is where the video {of} ends, and there is no such video in References ({board.ReferencesFolder}).");
            if (!File.Exists(clip))
                throw new VideoException($"{frame.Label} is where shot {of} ends, and shot {of} isn't made yet: make it first.");
            if (media is null)
                throw new VideoException($"Nothing on this PC can read video to take {frame.Label} from {of} (ffmpeg in BlackBox's Data\\tools would).");
            await Task.Run(() =>
            {
                using var reader = media.OpenVideo(clip, Math.Max(board.Width, board.Height)) ?? throw new VideoException($"{clip} has no picture this PC can read.");
                using var last = VideoCheck.LastFrame(reader) ?? throw new VideoException($"No frame of {clip} could be read.");
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
            WriteStamp(board.FrameMeta(frame), FrameStamp(board, frame), null, null);
            return new VideoMade(target, null, null, DateTime.UtcNow - started, []);
        }
        // A frame of a shot is the video's size, cut to it; a reference keeps its own shape, as the
        // pictures made from it are made the video's size (see VideoRoles.SizeFromBlank): cut to the
        // video's frame, a square picture of a character lost its head, and so did every frame made from it.
        Action<string, string> save = frame.Reference ? SaveReference : (source, to) => SaveFrame(source, to, board.Width, board.Height);
        if (frame.File is { Length: > 0 })
        {
            var started = DateTime.UtcNow;
            await Task.Run(() => TakeFile(board, frame), ct).ConfigureAwait(false);
            return new VideoMade(target, null, null, DateTime.UtcNow - started, []);
        }
        if (frame.Prompt.Trim().Length == 0)
            throw new VideoException($"{frame.Label} has no prompt: say what the picture shows, or give a picture of your own for it.");
        var pictures = new List<string>();
        foreach (var id in frame.From)
        {
            if (board.Frame(id) is not { } from)
            {
                pictures.Add(board.ReferenceFile(id) ?? board.ReferenceVideo(id) ?? throw new VideoException(
                    $"{frame.Label} is to be made from \"{id}\", which is neither a frame of this video nor a picture in its References folder ({board.ReferencesFolder})" +
                    (board.ReferenceFiles() is { Count: > 0 } there ? $", which has {string.Join(", ", there.Take(20).Select(board.InReferences))}." : ", which is empty.")));
                continue;
            }
            if (from.Id == frame.Id)
                continue;
            var picture = board.FramePicture(from);
            if (!File.Exists(picture))
                throw new VideoException($"{from.Label} isn't made yet, and {frame.Label} is made from it: make {from.Label} first.");
            pictures.Add(picture);
        }
        // A video named in from: a picture from its middle (a look, a character, a place), as the edit model takes pictures.
        var stills = new List<string>();
        for (var i = 0; i < pictures.Count; i++)
            if (Storyboard.IsVideoFile(pictures[i]))
            {
                var video = pictures[i];
                stills.Add(pictures[i] = await Task.Run(() => Still(video), ct).ConfigureAwait(false));
            }
        try
        {
            var inputs = new VideoInputs
            {
                Prompt = FramePrompt(frame),
                Seed = frame.Seed,
                Width = board.Width,
                Height = board.Height,
                Pictures = pictures,
                Prefix = Prefix(board, frame.Id),
            };
            var job = pictures.Count > 0 ? VideoJob.Edit : VideoJob.Frame;
            var stamp = FrameStamp(board, frame);
            var run = await RunAsync(job, frame.Model, inputs, frame.Set, board.FrameMeta(frame), stamp, "image", progress, userMessage, ct).ConfigureAwait(false);
            await Task.Run(() => save(run.Output, target), ct).ConfigureAwait(false);
            Restamp(board.FrameMeta(frame), stamp);
            VideoVersions.Remember(board.FrameMeta(frame), VideoPiece.Of(frame).Settings(board));
            return new VideoMade(target, board.FrameMeta(frame), run.Template, run.Took, run.Notes);
        }
        finally
        {
            foreach (var still in stills)
                try { File.Delete(still); }
                catch (IOException) { }
        }
    }

    /// <summary>A picture from the middle of a video, as a PNG in the temp folder.</summary>
    private string Still(string video)
    {
        if (media is null)
            throw new VideoException($"Nothing on this PC can read video to take a picture from {Path.GetFileName(video)} (ffmpeg in BlackBox's Data\\tools would).");
        using var reader = media.OpenVideo(video, 2048) ?? throw new VideoException($"{video} has no picture this PC can read.");
        using var frame = VideoCheck.MiddleFrame(reader) ?? throw new VideoException($"No frame of {video} could be read.");
        var still = Path.Combine(Path.GetTempPath(), $"blackbox-{Guid.NewGuid():N}.png");
        using (var stream = File.Create(still))
            frame.Encode(stream, SkiaSharp.SKEncodedImageFormat.Png, 100);
        return still;
    }

    // ---- shots -------------------------------------------------------------------------------------

    /// <summary>
    /// Makes a shot's clip from its frames: first and last, first only, or words only; a speaking
    /// one from its first frame and its line.
    /// </summary>
    /// <param name="tryFile">Saved as this file instead of the shot's own (one try of several).</param>
    public async Task<VideoMade> MakeShotAsync(Storyboard board, VideoShot shot, Action<string>? progress, CancellationToken userMessage, CancellationToken ct, string? tryFile = null)
    {
        using var making = MakingIn(board);
        var first = board.Frame(shot.First);
        var last = board.Frame(shot.Last);
        string? Picture(VideoFrame? frame)
        {
            if (frame is null)
                return null;
            var path = board.FramePicture(frame);
            return File.Exists(path) ? path : throw new VideoException($"{frame.Label} isn't made yet, and shot {shot.Id} {(frame == first ? "starts" : "ends")} on it: make it first.");
        }
        var (firstPicture, lastPicture) = (Picture(first), Picture(last));
        var notes = new List<string>();
        string? audio = null;
        VideoJob job;
        if (shot.LipSync)
        {
            if (firstPicture is null)
                throw new VideoException($"Shot {shot.Id} is lip-synced, which needs its first frame to show who sings or speaks: give it one.");
            audio = board.ShotAudio(shot);
            // Its own line, said a moment after the shot starts (a head turns to the camera first).
            if (!LineIsCurrent(board, shot))
                notes.AddRange((await MakeLineAsync(board, shot, progress, userMessage, ct).ConfigureAwait(false)).Notes);
            var said = await Task.Run(() => PlaceSound(board.ShotLine(shot), LineLead, shot.Seconds, audio), ct).ConfigureAwait(false);
            if (said + LineLead > shot.Seconds + 0.05)
                notes.Add($"Its line takes {said:0.0} s, and with the {LineLead:0.0} s before it the shot is {shot.Seconds:0.#} s: the end of the line is cut. Make the shot longer or the line shorter.");
            job = VideoJob.LipSync;
            if (lastPicture is not null)
                notes.Add($"A lip-synced shot is made from its first frame only, so it doesn't end on {last!.Label}.");
            lastPicture = null;
        }
        else
        {
            // The video model animates a picture well and draws one badly: a picture model makes it first.
            if (firstPicture is null)
                throw new VideoException(first is null
                    ? $"Shot {shot.Id} has no first frame: a picture model makes the picture a shot starts on, and the video model animates it. Give it a first frame."
                    : $"Shot {shot.Id} starts on {first.Label}, which isn't made yet: make it first.");
            job = lastPicture is null ? VideoJob.ShotFromFirst : VideoJob.Shot;
        }
        // A shot that moves as a video of the user's does: made with a template that follows a video.
        string? motion = null;
        if (shot.Motion is { } named && !shot.LipSync)
        {
            motion = board.ReferenceVideo(named) ?? throw new VideoException(
                $"Shot {shot.Id} moves as \"{named}\", which is no video in its References folder ({board.ReferencesFolder})" +
                (board.ReferenceVideos() is { Count: > 0 } there ? $", which has {string.Join(", ", there.Take(20).Select(board.InReferences))}." : ", which has no videos."));
            job = VideoJob.Motion;
        }
        var inputs = new VideoInputs
        {
            Prompt = shot.LipSync ? ShotPrompt(shot) : WithoutMusic(ShotPrompt(shot)),
            Seed = shot.Seed,
            Width = board.Width,
            Height = board.Height,
            Seconds = shot.Seconds,
            Fps = board.Fps,
            FirstImage = firstPicture,
            LastImage = lastPicture,
            Audio = audio,
            Video = motion,
            TextToVideo = firstPicture is null,
            Prefix = Prefix(board, shot.Id),
        };
        var target = tryFile ?? board.ShotVideo(shot);
        var workflow = tryFile is null ? board.ShotMeta(shot) : Storyboard.MetaOf(tryFile);
        var stamp = ShotStamp(board, shot);
        var run = await RunAsync(job, shot.Model, inputs, shot.Set, workflow, stamp, "video", progress, userMessage, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        MediaRelease.Release(target); // its version before may be playing in the chat
        File.Copy(run.Output, target, overwrite: true);
        if (VideoSteady.AsksStill(shot.Camera + "\n" + shot.Prompt) && await HoldStillAsync(target, ct).ConfigureAwait(false) is { } held)
            notes.Add(held);
        Restamp(workflow, stamp);
        VideoVersions.Remember(workflow, VideoPiece.Of(shot).Settings(board));
        return new VideoMade(target, workflow, run.Template, run.Took, [.. notes, .. run.Notes]);
    }

    /// <summary>
    /// A clip whose camera moved where its shot holds it still, held still after the fact
    /// (<see cref="VideoSteady"/>): LTX pushed in, panned or tilted on every locked-off shot of a
    /// night of videos. What was done, for the notes; null when the camera held still or nothing could be read.
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
            MediaRelease.Release(clip);
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
    /// Says a shot's line in the video's voice with VoxCPM2, ComfyUI's text-to-speech model here. The
    /// first speaking shot's line is said from the voice's description; the others clone that
    /// line's voice, so every line sounds like the same person.
    /// </summary>
    public async Task<VideoMade> MakeLineAsync(Storyboard board, VideoShot shot, Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        using var making = MakingIn(board);
        if (!Storyboard.Speaks(shot))
            throw new VideoException($"Shot {shot.Id} has no line to say.");
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
        var workflow = SpeechWorkflow(library, shot.Line.Trim(), board.Voice.Trim(), board.VoiceSeed, reference, firstSpeaker.Line.Trim(), Prefix(board, shot.Id + "-line"))
                       ?? throw new VideoException("Saying a line needs VoxCPM2's nodes in ComfyUI (the ComfyUI-VoxCPM custom node pack), and they aren't installed: " +
                                                  "install the pack (it downloads its model on first use).");
        workflow.FillAdvancedDefaults(library);
        var stamp = LineStamp(board, shot);
        var run = await RunWorkflowAsync(api, library, workflow, board.ShotLineMeta(shot), stamp, "VoxCPM2", "audio", progress, userMessage, ct).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(board.ShotLine(shot))!);
        MediaRelease.Release(board.ShotLine(shot));
        File.Copy(run.Output, board.ShotLine(shot), overwrite: true);
        Restamp(board.ShotLineMeta(shot), stamp);
        return new VideoMade(board.ShotLine(shot), board.ShotLineMeta(shot), "VoxCPM2", run.Took, [.. notes, .. run.Notes]);
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

    public static bool LineIsCurrent(Storyboard board, VideoShot shot) =>
        File.Exists(board.ShotLine(shot)) && MadeStamp(board.ShotLineMeta(shot)) is { } made && made == LineStamp(board, shot);

    public static string LineStamp(Storyboard board, VideoShot shot)
    {
        var first = board.Shots.FirstOrDefault(Storyboard.Speaks);
        var reference = first is null || first == shot ? "" : $"{first.Line.Trim()}|{FileStamp(board.ShotLine(first))}";
        return Hash($"{shot.Line.Trim()}|{board.Voice.Trim()}|{board.VoiceSeed}|{reference}");
    }

    /// <summary>
    /// A sound placed <paramref name="at"/> seconds into a WAV <paramref name="seconds"/> long, silence
    /// around it (cut where it runs past the end); returns how long the sound is.
    /// </summary>
    public double PlaceSound(string sound, double at, double seconds, string target)
    {
        using var reader = media?.OpenAudio(sound) ?? throw new VideoException($"{sound} can't be read on this PC (ffmpeg in BlackBox's Data\\tools would).");
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
    /// them garbage), 30 s 3.2, 45 to 90 s 3.8 to 4.4. The video uses the song's start and fades it out.
    /// </summary>
    public const double MinSongSeconds = 60;

    /// <summary>How long a song is made: from where it starts to the video's end and a little more, a minute at least.</summary>
    public static double SongSeconds(Storyboard board, VideoSound sound) => Math.Max(MinSongSeconds, Math.Ceiling(board.Timing().Length - sound.Start) + 2);

    /// <summary>Hears how good a song is, to keep the best of several tries; null when it can't run here.</summary>
    public SongJudge? Judge { get; init; }

    /// <summary>
    /// Makes a song for a sound line: in its style, with its lyrics (an instrumental without), a
    /// minute long at least. With a judge, up to <paramref name="tries"/> songs with new
    /// seeds, each heard for the part the video uses, and the best kept (its seed in the storyboard,
    /// which the caller saves): the same settings scored 3.3 to 4.2 by the seed alone.
    /// </summary>
    public async Task<VideoMade> MakeSongAsync(Storyboard board, VideoSound sound, Action<string>? progress, CancellationToken userMessage, CancellationToken ct, int tries = 1)
    {
        using var making = MakingIn(board);
        if (sound.Source != VideoSound.Song)
            throw new VideoException(sound.Source == VideoSound.Foley ? $"Sound {sound.Id} is foley, not a song." : $"Sound {sound.Id} is a sound file of the user's: there is nothing to make.");
        if (sound.Tags.Trim().Length == 0)
            throw new VideoException("The song has no style: say what it should sound like (genre, instruments, voice, tempo).");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var python = Judge is null ? null : SongJudge.PythonOf(hub);
        var ffmpeg = media?.Ffmpeg;
        if (python is null || ffmpeg is null)
            tries = 1;
        var made = new List<(long Seed, string File, string Workflow, Run Run)>();
        var seedBefore = sound.Seed;
        var song = board.SoundFile(sound);
        var meta = board.SoundMeta(sound);
        var piece = VideoPiece.Of(sound);
        try
        {
            for (var attempt = 1; attempt <= Math.Max(1, tries); attempt++)
            {
                if (attempt > 1)
                    sound.Seed = Storyboard.NewSeed();
                var inputs = new VideoInputs
                {
                    Tags = sound.Tags.Trim(),
                    Lyrics = sound.Lyrics.Trim().Length > 0 ? sound.Lyrics.Trim() : "[Instrumental]",
                    Bpm = Bpm(sound.Tags),
                    Seed = sound.Seed,
                    Seconds = SongSeconds(board, sound),
                    Prefix = Prefix(board, sound.Id),
                };
                var file = tries <= 1 ? song : Path.Combine(Path.GetDirectoryName(song)!, $"{Path.GetFileNameWithoutExtension(song)}.try{attempt}.mp3");
                var workflow = Storyboard.MetaOf(file);
                var stamp = SongStamp(board, sound);
                var run = await RunAsync(VideoJob.Song, sound.Model, inputs, sound.Set, workflow, stamp, "audio",
                    tries <= 1 ? progress : s => progress?.Invoke($"Song {attempt} of {tries}: {s}"), userMessage, ct).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                MediaRelease.Release(file);
                File.Copy(run.Output, file, overwrite: true);
                Restamp(workflow, stamp);
                made.Add((sound.Seed, file, workflow, run));
            }
        }
        catch
        {
            // A try that failed (or was stopped) leaves the board as it was: the song made before
            // still matches it, rather than being out of date for a seed nothing was made with.
            sound.Seed = seedBefore;
            // The tries made before it are versions of the song, as a shot's are, not stray files.
            if (tries > 1)
                foreach (var m in made)
                {
                    try { VideoVersions.Add(board, piece, m.File); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            throw;
        }

        // Each heard for the part of it the video uses.
        Dictionary<string, SongScore>? scores = null;
        var notes = made.SelectMany(m => m.Run.Notes).Distinct().ToList();
        if (python is not null && ffmpeg is not null)
        {
            try
            {
                scores = await Judge!.ScoreAsync(made.Select(m => m.File).ToList(), python, ffmpeg, Math.Max(1, board.Timing().Length - sound.Start), progress, ct).ConfigureAwait(false);
            }
            catch (VideoException ex)
            {
                notes.Add(ex.Message);
            }
        }
        var best = made.MaxBy(m => scores?.GetValueOrDefault(m.File)?.Musicality ?? 0);
        sound.Seed = best.Seed;
        // How each sounds, kept with it: the Video generator panel shows it with the song and its versions.
        foreach (var m in made)
            if (scores?.GetValueOrDefault(m.File) is { } heard)
                VideoVersions.RememberLook(m.Workflow, heard.Musicality < SongScore.Good ? "redo" : "good", heard.Describe());
        if (made.Count > 1)
        {
            notes.Insert(0, $"Kept song {made.IndexOf(best) + 1} of {made.Count} (seed {best.Seed}); musicality of each: " +
                            string.Join(", ", made.Select(m => scores?.GetValueOrDefault(m.File) is { } s ? s.Musicality.ToString("0.0", CultureInfo.InvariantCulture) : "?")) + ".");
            VideoVersions.Promote(best.File, song);
            VideoVersions.Remember(meta, piece.Settings(board));
            // The others are versions of the song, to put back in the panel.
            foreach (var other in made.Where(m => m != best))
            {
                VideoVersions.Remember(other.Workflow, piece.Settings(board) is JsonObject settings ? With(settings, other.Seed) : null);
                VideoVersions.Add(board, piece, other.File);
            }
        }
        else
            VideoVersions.Remember(meta, piece.Settings(board));
        return new VideoMade(song, meta, best.Run.Template, clock.Elapsed, notes) { Score = scores?.GetValueOrDefault(best.File) };

        // The sound's settings with the seed that try was made with.
        static JsonObject With(JsonObject settings, long seed)
        {
            settings["seed"] = seed;
            return settings;
        }
    }

    /// <summary>The tempo a song's style names ("70 BPM"), for ACE-Step's own setting.</summary>
    internal static int? Bpm(string tags) =>
        System.Text.RegularExpressions.Regex.Match(tags, @"(\d{2,3})\s*bpm", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m &&
        int.TryParse(m.Groups[1].Value, out var bpm) && bpm is >= 30 and <= 300 ? bpm : null;

    // ---- foley ----------------------------------------------------------------------------------------

    /// <summary>Foley is made in even pieces of at most this long: MMAudio learnt on 8 s clips, and longer comes out worse.</summary>
    public const double FoleyPiece = 8;

    /// <summary>
    /// The stretch of the video foley is made for, in seconds: from where its file starts playing
    /// (its start, less what is trimmed off) to where it stops, or the video's end.
    /// </summary>
    public static (double From, double To) FoleyStretch(Storyboard board, VideoSound sound)
    {
        var length = board.Timing().Length;
        var to = sound.Seconds > 0 ? Math.Min(length, sound.Start + sound.Seconds) : length;
        return (Math.Round(Math.Max(0, sound.Start - sound.Trim), 2), Math.Round(to, 2));
    }

    /// <summary>
    /// Makes foley for a sound line: what the clips under it show, heard (footsteps, a door, rain),
    /// with a video-to-audio model (MMAudio). The stretch is cut out of the clips as the joined video
    /// shows it, in pieces of up to <see cref="FoleyPiece"/> seconds, each made with the sound's seed
    /// and the next, and joined.
    /// </summary>
    public async Task<VideoMade> MakeFoleyAsync(Storyboard board, VideoSound sound, Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        using var making = MakingIn(board);
        if (sound.Source != VideoSound.Foley)
            throw new VideoException($"Sound {sound.Id} isn't foley.");
        var ffmpeg = media?.Ffmpeg ?? throw new VideoException("Foley is made from the clips under it, cut out with ffmpeg, and there is none on this PC: download a portable build " +
                                                              "(gyan.dev's ffmpeg-release-essentials.zip) and unpack it into BlackBox's Data\\tools, where it is found.");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var (from, to) = FoleyStretch(board, sound);
        var count = Math.Max(1, (int)Math.Ceiling((to - from) / FoleyPiece - 1e-6));
        var each = (to - from) / count;
        var file = board.SoundFile(sound);
        var meta = board.SoundMeta(sound);
        var stamp = FoleyStamp(board, sound);
        var work = Path.Combine(Path.GetTempPath(), "BlackBox videos", "foley-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var pieces = new List<string>();
            var notes = new List<string>();
            string? template = null;
            for (var k = 0; k < count; k++)
            {
                var at = from + k * each;
                var clip = Path.Combine(work, $"{sound.Id}-{k + 1}.mp4");
                var tell = count == 1 ? progress : s => progress?.Invoke($"Piece {k + 1} of {count}: {s}");
                await VideoMontage.RunAsync(ffmpeg, VideoMontage.StretchPlan(board, at, each, clip), clip, each, "Cutting out the clips under it", "cut out the clips under it", tell, ct).ConfigureAwait(false);
                var inputs = new VideoInputs
                {
                    Video = clip,
                    Prompt = sound.Tags.Trim(),
                    Seed = sound.Seed + k,
                    Seconds = Math.Round(each, 2),
                    Prefix = Prefix(board, sound.Id),
                };
                // The first piece's workflow is kept with the sound; the others are only run.
                var workflow = k == 0 ? meta : Path.Combine(work, $"{sound.Id}-{k + 1}.json");
                var run = await RunAsync(VideoJob.Foley, sound.Model, inputs, sound.Set, workflow, stamp, "audio", tell, userMessage, ct).ConfigureAwait(false);
                template = run.Template;
                notes.AddRange(run.Notes);
                pieces.Add(run.Output);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            if (pieces.Count == 1)
            {
                MediaRelease.Release(file);
                File.Copy(pieces[0], file, overwrite: true);
            }
            else
            {
                var args = new List<string> { "-hide_banner", "-y", "-nostats", "-progress", "pipe:1" };
                foreach (var p in pieces)
                    args.AddRange(["-i", p]);
                var join = string.Concat(Enumerable.Range(0, pieces.Count).Select(i => $"[{i}:a]")) + $"concat=n={pieces.Count}:v=0:a=1[a]";
                var temp = Path.Combine(work, "foley.mp3");
                args.AddRange(["-filter_complex", join, "-map", "[a]", "-c:a", "libmp3lame", "-q:a", "2", temp]);
                await VideoMontage.RunAsync(ffmpeg, args, file, to - from, "Joining its pieces", "join the foley's pieces", progress, ct).ConfigureAwait(false);
            }
            Restamp(meta, stamp);
            VideoVersions.Remember(meta, VideoPiece.Of(sound).Settings(board));
            if (count > 1)
                notes.Insert(0, $"Made in {count} pieces of {each:0.#} s, joined.");
            return new VideoMade(file, meta, template, clock.Elapsed, notes.Distinct().ToList());
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>What foley is made from: what is heard, its seed and model, the stretch, and the clips under it as they are now.</summary>
    public static string FoleyStamp(Storyboard board, VideoSound sound)
    {
        var (from, to) = FoleyStretch(board, sound);
        var (starts, _) = board.Timing();
        var sb = new StringBuilder($"foley|{sound.Tags.Trim()}|{sound.Seed}|{sound.Model}|{sound.Set?.ToJsonString()}|{from.ToString(CultureInfo.InvariantCulture)}|{to.ToString(CultureInfo.InvariantCulture)}");
        for (var i = 0; i < board.Shots.Count; i++)
            if (starts[i] < to && starts[i] + board.Shots[i].Seconds > from)
                sb.Append('|').Append(board.Shots[i].Id).Append(' ').Append(FileStamp(board.ShotVideo(board.Shots[i])));
        return Hash(sb.ToString());
    }

    // ---- running a job --------------------------------------------------------------------------------

    /// <summary>Where ComfyUI saves a video's files, under its output folder.</summary>
    private static string Prefix(Storyboard board, string id) => $"BlackBox videos/{Storyboard.Safe(board.Title.Length > 0 ? board.Title : Path.GetFileName(board.Folder))}/{id}";

    private sealed record Run(string Output, string Template, TimeSpan Took, List<string> Notes);

    /// <summary>A job queued in ComfyUI and not yet picked up: by the workflow file it is for.</summary>
    private sealed record Pending(string PromptId, string ClientId, string Stamp, string Server, IReadOnlyDictionary<string, string> Titles, string Template, DateTime Queued);

    private static readonly ConcurrentDictionary<string, Pending> Running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the template for a job with the inputs set, and saves the workflow as it ran beside the
    /// result (with the files it loads by their full paths, so the comfyui tool can run it again).
    /// </summary>
    /// <param name="model">What it is to be made with, in words or by name (<see cref="VideoFrame.Model"/>); null: the best installed.</param>
    private async Task<Run> RunAsync(VideoJob job, string? model, VideoInputs inputs, JsonObject? extra, string workflowFile, string stamp, string kind,
        Action<string>? progress, CancellationToken userMessage, CancellationToken ct)
    {
        Use();
        var notes = new List<string>();
        var api = await hub.ConnectAsync(null, notes, progress, ct).ConfigureAwait(false);
        var library = await hub.LibraryAsync(api, ct).ConfigureAwait(false);
        progress?.Invoke(model is null ? "Getting the workflow ready…" : $"Finding \"{model}\" among what is installed…");
        var (template, note) = await VideoTemplates.PickAsync(hub, api, model, job, ct).ConfigureAwait(false);
        if (note is not null)
            notes.Add(note);
        if (template is null)
            throw new VideoException($"No ComfyUI template for {VideoTemplates.Describe(job)} is ready on this PC: " +
                                    "every one this needs a model or node that isn't installed. The comfyui tool finds templates (action=templates) and downloads what they need.");
        if (!template.Ready)
            throw new VideoException($"The template {template.Template.Name} needs {string.Join(", ", template.Missing.Take(6))}, which isn't installed: " +
                                    "download it (the comfyui tool's action=templates with its name gives the links), or pick another template for this.");
        var workflow = template.Workflow;
        var missed = new List<string>();
        var set = VideoRoles.Apply(workflow, library, inputs, missed);
        // What the job can't go without stops it; the rest is said.
        var vital = missed.Where(m => m.StartsWith("the prompt", StringComparison.Ordinal) || m.StartsWith("the first frame", StringComparison.Ordinal) ||
                                      m.StartsWith("the last frame", StringComparison.Ordinal) || m.StartsWith("the sound", StringComparison.Ordinal) ||
                                      m.StartsWith("the video", StringComparison.Ordinal)).ToList();
        if (vital.Count > 0)
            throw new VideoException($"The template {template.Template.Name} has no place for {string.Join(", ", vital)}: pick another template for this job, " +
                                    "or say where it goes with set {\"node id.input\": value}.");
        notes.AddRange(missed.Select(m => $"The template {template.Template.Name} had no place for {m}."));
        if (extra is { Count: > 0 } && workflow.Set(extra, library) is { Count: > 0 } wrong)
            throw new VideoException($"These changes don't fit the template {template.Template.Name}:\n- " + string.Join("\n- ", wrong));
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
        // No stamp until what it makes is in place (the caller writes it then): a job that failed or
        // was stopped left the piece made before with the new settings' stamp, as if made from them.
        WriteStamp(workflowFile, "", templateName, workflow);

        // The files it loads go into ComfyUI's input folder.
        var uploaded = new HashSet<string>();
        foreach (var (id, input, path) in workflow.LocalFiles(library, p => p))
        {
            if (!File.Exists(path))
                throw new VideoException($"{path} isn't there: node {id} loads it.");
            progress?.Invoke($"Uploading {Path.GetFileName(path)}…");
            ((JsonObject)workflow.Nodes[id]!["inputs"]!)[input] = await api.UploadAsync(path, ct).ConfigureAwait(false);
            uploaded.Add(id + "." + input);
        }
        var problems = workflow.Validate(library, uploaded);
        if (problems.Count > 0)
            throw new VideoException($"The workflow from {templateName} can't run:\n- " + string.Join("\n- ", problems.Take(12)));
        var clientId = "blackbox-" + Guid.NewGuid().ToString("N")[..12];
        var (promptId, refused) = await ComfyRunner.QueueAsync(api, workflow, clientId, ct).ConfigureAwait(false);
        if (promptId is null)
            throw new VideoException($"ComfyUI refused the workflow from {templateName}:\n- " + string.Join("\n- ", refused));
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
            throw new VideoStillRunningException($"It is still being made in ComfyUI ({job.Progress ?? job.Status}, {Chats.ChatTimes.Stopwatch(DateTime.UtcNow - pending.Queued)} so far)" +
                                                ": asking for the same thing again waits for it.");
        Running.TryRemove(workflowFile, out _);
        if (job.Status != "success")
            throw new VideoException($"ComfyUI's job from {pending.Template} {(job.Status == "interrupted" ? "was stopped" : "failed")}: {job.Error}");
        var dirs = await ComfyHub.FoldersAsync(api, ct).ConfigureAwait(false) ?? [];
        var files = await ComfyRunner.LocateAsync(api, job.Files, dirs, Path.Combine(Path.GetTempPath(), "BlackBox videos"), ct).ConfigureAwait(false);
        var made = files.Where(f => f.LocalPath is not null && f.Kind == kind || kind == "image" && f.Kind == "animation" && f.LocalPath is not null).ToList();
        var saved = made.Where(f => f.Type == "output").ToList();
        var pick = (saved.Count > 0 ? saved : made).MaxBy(f => new FileInfo(f.LocalPath!).Length)
                   ?? throw new VideoException($"The job from {pending.Template} made no {kind} (it made {(files.Count == 0 ? "nothing" : string.Join(", ", files.Select(f => f.Filename)))}).");
        return new Run(pick.LocalPath!, pending.Template, job.Elapsed, notes);
    }

    // ---- what each piece was made from ----------------------------------------------------------------

    /// <summary>
    /// What a frame's picture comes from, as a short hash: its prompt, seed, size and what it is made
    /// with, its user's file, the pictures it is based on. A frame whose stamp differs from the one it
    /// was made with is out of date. Only the frame's own settings and the video's size go in: a
    /// change to one frame leaves the others made.
    /// </summary>
    public static string FrameStamp(Storyboard board, VideoFrame frame)
    {
        var sb = new StringBuilder();
        if (frame.Of is { } of && board.Shot(of) is { } before)
            sb.Append("of|").Append(of).Append('|').Append(FileStamp(board.ShotVideo(before)));
        else if (frame.Of is { } video && (board.ReferenceVideo(video) is not null || Storyboard.IsVideoFile(video)))
            sb.Append("of|").Append(video).Append('|').Append(board.ReferenceVideo(video) is { } file ? FileStamp(file) : "none");
        else if (frame.File is { Length: > 0 } file)
            sb.Append("file|").Append(file).Append('|').Append(FileStamp(file));
        else
        {
            sb.Append(FramePrompt(frame)).Append('|').Append(frame.Seed).Append('|')
              .Append(frame.Model).Append('|').Append(frame.Set?.ToJsonString());
            // Not itself, which making it skips: its own picture changes with every make, and it was never up to date.
            foreach (var id in frame.From.Where(id => id != frame.Id))
                sb.Append("|from ").Append(id).Append(' ').Append(board.Frame(id) is { } from ? FileStamp(board.FramePicture(from)) : (board.ReferenceFile(id) ?? board.ReferenceVideo(id)) is { } picture ? FileStamp(picture) : "");
        }
        sb.Append('|').Append(board.Width).Append('x').Append(board.Height);
        return Hash(sb.ToString());
    }

    public static string ShotStamp(Storyboard board, VideoShot shot)
    {
        var sb = new StringBuilder();
        // Numbers as the invariant culture writes them: a video moved to a PC that writes 5,5 for 5.5 stays made.
        sb.Append(ShotPrompt(shot)).Append('|').Append(shot.Seed).Append('|').Append(shot.Seconds.ToString(CultureInfo.InvariantCulture)).Append('|').Append(board.Fps).Append('|')
          .Append(board.Width).Append('x').Append(board.Height).Append('|').Append(shot.LipSync).Append('|').Append(shot.Set?.ToJsonString());
        foreach (var id in new[] { shot.First, shot.LipSync ? null : shot.Last })
            sb.Append('|').Append(board.Frame(id) is { } frame ? FileStamp(board.FramePicture(frame)) : "none");
        sb.Append('|').Append(shot.Model);
        // Only a shot that follows a video has it in its stamp: those made before stay up to date.
        if (shot.Motion is { } motion && !shot.LipSync)
            sb.Append("|motion ").Append(motion).Append(' ').Append(board.ReferenceVideo(motion) is { } file ? FileStamp(file) : "none");
        if (Storyboard.Speaks(shot))
            sb.Append("|line ").Append(LineStamp(board, shot)).Append(' ').Append(LineLead.ToString(CultureInfo.InvariantCulture));
        return Hash(sb.ToString());
    }

    /// <summary>
    /// What a song is made from, and after a colon how long it was made: a song stays up to date
    /// while it covers the video from where it starts. With the length in the hash, any change of a
    /// video over a minute (a shot a second shorter) put the song out of date, and remaking it changed the music.
    /// </summary>
    public static string SongStamp(Storyboard board, VideoSound sound) =>
        SongHash(sound) + ":" + SongSeconds(board, sound).ToString(CultureInfo.InvariantCulture);

    private static string SongHash(VideoSound sound) =>
        Hash($"{sound.Tags}|{sound.Lyrics}|{sound.Seed}|{sound.Model}|{sound.Set?.ToJsonString()}");

    /// <summary>Gives a made piece's meta a new stamp (when it differs).</summary>
    private static void Restamp(string meta, string stamp)
    {
        if (MadeStamp(meta) != stamp)
            VideoVersions.ChangeMade(meta, made => made["stamp"] = stamp);
    }

    private static string FileStamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? $"{info.Length}@{info.LastWriteTimeUtc.Ticks}" : "missing";
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>The stamp a piece was made with, kept in its meta; null when it wasn't made (or by hand).</summary>
    public static string? MadeStamp(string meta)
    {
        try
        {
            return File.Exists(meta) && JsonNode.Parse(File.ReadAllText(meta))?["made"]?["stamp"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The template a piece was made with, as its meta says; null when it says none.</summary>
    public static string? MadeWith(string meta)
    {
        try
        {
            return File.Exists(meta) && JsonNode.Parse(File.ReadAllText(meta))?["made"]?["template"] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes in a piece's meta how its file is being made (its stamp, the template, when), what the
    /// meta says of the piece itself kept (a line's meta is made here), and the workflow beside it:
    /// ComfyUI's API format, which ComfyUI opens and the comfyui tool runs as it is (workflow_file).
    /// </summary>
    private static void WriteStamp(string meta, string stamp, string? template, ComfyWorkflow? workflow)
    {
        JsonObject json;
        try { json = File.Exists(meta) ? JsonNode.Parse(File.ReadAllText(meta)) as JsonObject ?? [] : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { json = []; }
        // A line's meta is made here; a frame's, shot's or the song's by the storyboard, with the piece in it.
        if (json["kind"] is null && (meta.EndsWith(".line.json", StringComparison.OrdinalIgnoreCase) ? "line"
                : Path.GetFileName(meta).Contains(".try", StringComparison.OrdinalIgnoreCase) ? "try" : null) is { } kind)
            json["kind"] = kind;
        json["made"] = new JsonObject { ["stamp"] = stamp, ["template"] = template, ["at"] = DateTime.UtcNow.ToString("O") };
        Directory.CreateDirectory(Path.GetDirectoryName(meta)!);
        Settings.AtomicFile.Write(meta, json.ToJsonString(ComfyWorkflow.Indented));
        var file = Storyboard.WorkflowOf(meta);
        if (workflow is not null)
            Settings.AtomicFile.Write(file, JsonNode.Parse(workflow.ToJson())!.ToJsonString(ComfyWorkflow.Indented));
        else if (File.Exists(file))
            File.Delete(file); // taken from a file: no workflow made it
    }

    /// <summary>The videos a piece is being made of now, by their folders (with how many at once): their folders aren't renamed meanwhile.</summary>
    private static readonly ConcurrentDictionary<string, int> Making = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a piece of the video in <paramref name="folder"/> is being made now (in this BlackBox).</summary>
    public static bool IsMaking(string folder) => Making.TryGetValue(Path.GetFullPath(folder), out var n) && n > 0;

    /// <summary>Marks the video as being made until the result is disposed.</summary>
    private static IDisposable MakingIn(Storyboard board)
    {
        var folder = Path.GetFullPath(board.Folder);
        Making.AddOrUpdate(folder, 1, (_, n) => n + 1);
        return new Done(() => Making.AddOrUpdate(folder, 0, (_, n) => Math.Max(0, n - 1)));
    }

    private sealed class Done(Action done) : IDisposable
    {
        private int _once;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _once, 1) == 0)
                done();
        }
    }

    /// <summary>Whether a frame's picture is there and was made from what the storyboard says now.</summary>
    public static bool IsCurrent(Storyboard board, VideoFrame frame) =>
        File.Exists(board.FramePicture(frame)) && MadeStamp(board.FrameMeta(frame)) is { } made && made == FrameStamp(board, frame);

    public static bool IsCurrent(Storyboard board, VideoShot shot) =>
        File.Exists(board.ShotVideo(shot)) && MadeStamp(board.ShotMeta(shot)) is { } made && made == ShotStamp(board, shot);

    /// <summary>
    /// A sound is there and, a song, was made from what the storyboard says now and covers the video
    /// from where it starts; foley, from what it says and the clips under it as they are now.
    /// </summary>
    public static bool IsCurrent(Storyboard board, VideoSound sound) =>
        File.Exists(board.SoundFile(sound)) &&
        sound.Source switch
        {
            VideoSound.FromFile => true,
            VideoSound.Foley => MadeStamp(board.SoundMeta(sound)) == FoleyStamp(board, sound),
            _ => MadeStamp(board.SoundMeta(sound)) is { } made && made.Split(':') is [var hash, var length] && hash == SongHash(sound)
                 && double.TryParse(length, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds >= SongSeconds(board, sound),
        };

    // ---- pictures and sound --------------------------------------------------------------------------------

    /// <summary>
    /// A frame made from the user's picture (<see cref="VideoFrame.File"/>), at once: a frame of a
    /// shot cut to the video's size, a reference at its own shape, and marked made from it.
    /// </summary>
    public static void TakeFile(Storyboard board, VideoFrame frame)
    {
        if (frame.File is not { Length: > 0 } file || !File.Exists(file))
            throw new VideoException($"{frame.Label}'s picture {frame.File} isn't there.");
        var target = board.FramePicture(frame);
        if (frame.Reference)
            SaveReference(file, target);
        else
            SaveFrame(file, target, board.Width, board.Height);
        WriteStamp(board.FrameMeta(frame), FrameStamp(board, frame), null, null);
    }

    /// <summary>
    /// A picture scaled to cover the video's frame and cut to it in the middle, saved as PNG: every
    /// frame of a video has its size, whatever made it or wherever it came from.
    /// </summary>
    public static void SaveFrame(string source, string target, int width, int height)
    {
        using var bitmap = PictureFile.Decode(source) ?? throw new VideoException($"{source} isn't a picture this can read.");
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

    /// <summary>The longest side a reference is kept at: the edit model works from about a megapixel.</summary>
    public const int ReferenceSide = 2048;

    /// <summary>
    /// A reference's picture saved as PNG at its own shape, black bars along its edges cut off and
    /// no bigger than <see cref="ReferenceSide"/> across: what it shows is kept whole, as the frames
    /// made from it are made the video's size from all of it.
    /// </summary>
    public static void SaveReference(string source, string target)
    {
        using var bitmap = PictureFile.Decode(source) ?? throw new VideoException($"{source} isn't a picture this can read.");
        var inside = Bars(bitmap);
        var scale = Math.Min(1, ReferenceSide / (double)Math.Max(inside.Width, inside.Height));
        var (width, height) = (Math.Max(1, (int)Math.Round(inside.Width * scale)), Math.Max(1, (int)Math.Round(inside.Height * scale)));
        using var picture = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(picture))
        {
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(bitmap, inside, new SKRect(0, 0, width, height), paint);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".tmp";
        using (var data = picture.Encode(SKEncodedImageFormat.Png, 100))
        using (var stream = File.Create(temp))
            data.SaveTo(stream);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// The picture inside black bars along its edges, if it has some. Asked for a close-up "in a
    /// cinematic 35mm look", Qwen-Image-Edit drew the picture letterboxed, and LTX kept the bars
    /// through the whole clip; a video's frame is the picture all over.
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
