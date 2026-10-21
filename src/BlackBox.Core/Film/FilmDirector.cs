using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Media;

namespace BlackBox.Core.Film;

/// <summary>Asks the chat model something in a request of its own, with its own instructions and pictures or none, thinking first or not; null when it couldn't be asked.</summary>
public delegate Task<string?> FilmAsk(string system, string question, IReadOnlyList<byte[]> pictures, bool think, CancellationToken ct);

/// <summary>What BlackBox made of what the user said: what it says back, the storyboard as it is now, and what to make.</summary>
/// <param name="Make">The pieces to make now, in the order they are to be made.</param>
/// <param name="Notes">What it wanted to change and wasn't let (another piece than the one picked).</param>
public sealed record FilmDirection(string Reply, Storyboard Board, IReadOnlyList<FilmPiece> Make, IReadOnlyList<string> Notes);

/// <summary>
/// The user says in plain words what they want of a piece of the film (a frame, a shot, the song) or
/// of the whole film; the chat model turns it into what the image, video and music models are given:
/// prompts, camera, lengths, lines, which installed model makes each piece. The user never writes a
/// prompt. A piece's request changes only that piece (a shot's, its frames too); the whole film's
/// can lay it out or change anything, and makes nothing unless asked.
/// </summary>
public static class FilmDirector
{
    private const string Brief = """
        You direct films made with AI models in BlackBox's Filmmaking window, for a person who says in plain words what they want. You turn their words into what the models are given: each picture's prompt, each shot's prompt and camera, its length, what is said in it, which installed model makes it, and the song. They never write prompts themselves, so write them well:

        - A frame is one still picture: who is in it (repeat what makes each person look like themselves: age, hair, face, build, clothes), where, doing what, how it is framed (wide, medium, close-up; the angle), the light, and the film's look. Write the film's look into every prompt, in the same words each time, so the pictures match. No words or letters in a picture unless asked.
        - A reference is a picture other frames are made from, so a person or place looks the same in every shot: a character sheet (the same person from the front and three-quarters on a plain background) or the place on its own. It is in no shot, and has a short "name" the person knows it by ("Marco", "the fox", "the forest"). A frame made from references lists them in "from", and its prompt says the person is the same as in the reference.
        - A shot is a few seconds of video from its first frame to its last one, if it has one. Every shot starts on a frame (its own, or the one the shot before ends on): the video model animates a picture well and draws one badly, so a picture model makes it. Its prompt says what happens, in order, in the present tense: one clear action, how people move and what changes, with the look, and last what is heard (footsteps, wind, a door; no music: the video model makes the shot's sound, and the music is the song). Write action a camera could film for real, because a video model takes the words literally: people move as people do, and things come from somewhere and fall ("twirls a big forkful, slurps it, sauce flicks onto his chin", not "noodles flying"; "leans over the plate", not "head down in the plate"). "camera" says what the camera does (still, slow push in, pans left, follows her). 3 to 8 seconds. A shot that starts on the frame the one before ends on goes on with the same camera; one with a first frame of its own is a cut. Its first frame is how it begins and its last frame how it ends: what changes during the shot goes in the shot's prompt, and a frame changes only when the way the shot begins or ends changes. A shot with both moves from one to the other, so whatever is in a different place in the two (the moon, a tree, the horizon) moves during the shot: write the two frames to agree on everything that stays put (the same words for where the moon and the trees are), differing only in what the shot changes, and make the last frame from the first (its "from" holds the first frame's id, with the references), so the picture model keeps everything else exactly where it is. That picture model changes a pose, a face, a turn or what someone holds well, and moves someone across the frame badly (asked for a girl "now beside the mailbox", it left her where she stood, and the video walked her there and back): when someone walks or moves far, give the shot no last frame and say in its prompt where they end up. A last frame also pins the end, which the video reaches early and holds (a wave ending on a raised hand froze there for the last 1.5 s): an action that keeps going to the end (waving, walking, dancing, talking) gets no last frame either. A shot that goes on from the very moment the one before ends starts on a frame taken from that shot's clip: {"id": "f5", "of": "s1"} with no prompt is where s1's clip ends, so nothing jumps; use it after a shot without a last frame.
        - A line is what someone says in a shot, lip-synced from its first frame, in the film's one voice ("voice" in sound: age, voice, manner). It starts 0.4 s into the shot at about 2.5 words a second, so it has to fit. A speaking shot's first frame shows the face toward the camera with the mouth free: no food or hand over it. A lip-synced shot is made from its first frame only.
        - The song: "tags" (genre, instruments, mood, tempo such as "100 BPM") and "lyrics" ([verse] and [chorus] lines; none for an instrumental). It is made a minute long and the film uses its start.
        - "model" is what a piece is made with. Set it only when the person names or describes a model or workflow, to the name of an installed one (listed with the request). Leave it out otherwise: the best installed one is used.
        - Change only what they asked for, and keep the rest as it is: a made piece whose settings change has to be made again.

        Reply with only a JSON object, nothing before or after it:
        {"reply": "what you did, or the answer to their question: one or two plain sentences to them, no jargon",
         "change": {"title": "...", "frames": [{"id": "f2", "prompt": "..."}], "shots": [{"id": "s1", "seconds": 5}], "sound": {"tags": "..."}},
         "make": ["f2", "s1"]}
        In "change" give only what changes: a frame or shot by its id with the fields that change; a new one with a new id and all its fields; {"id": "s4", "delete": true} takes one out. Frame fields: name, reference (true for a reference), prompt, from (ids of the frames it is made from), of (a shot's id: the frame is where that shot's clip ends, taken from it), model, seed. Shot fields: first, last (frame ids; null for none), seconds, prompt, camera, line, lipSync, transition (cut, dissolve or fade), model, seed. Sound fields: source (none, song or file), tags, lyrics, voice, model, seed. "seed": "new" makes another take of the same settings. "make" lists the pieces to make now, by id ("song" for the song), in the order they are to be made: frames before the shots that start or end on them, references before the frames made from them.
        """;

    /// <summary>
    /// The brief with how the video model reads a shot's prompt: the user found most takes ruined by
    /// motion (the wrong way, bodies bent wrong, props moving by themselves), which is what a prompt
    /// leaves open.
    /// </summary>
    private static readonly string Instructions = Brief + "\n\nHow the video model (LTX) reads a shot's prompt, which is how to write one:\n\n" +
                                            ComfyGuides.Without(ComfyGuides.Video, "Pictures first", "Sound and music", "Exact motion: tracks and controls", "Settings");

    /// <summary>
    /// Turns what the user said about <paramref name="piece"/> (null: the whole film) into changes to
    /// the storyboard and what to make. The storyboard given isn't changed; the one returned is,
    /// and isn't saved yet.
    /// </summary>
    /// <param name="installed">What is installed for the jobs the piece can be, to name in "model".</param>
    /// <param name="picture">The piece as it is (a frame, a sheet of a clip), for the model to see.</param>
    /// <param name="think">Whether the model thinks first; by default a whole film is thought out and a piece's request answered at once.</param>
    public static async Task<FilmDirection> DirectAsync(Storyboard board, FilmPiece? piece, string request, FilmTalk talk,
        IReadOnlyList<FilmOption> installed, byte[]? picture, FilmAsk ask, CancellationToken ct, bool? think = null)
    {
        var question = Question(board, piece, request, talk, installed, picture is not null);
        string? answer = null;
        JsonObject? json = null;
        for (var attempt = 0; attempt < 2 && json is null; attempt++)
        {
            // A whole film is laid out with thought (half a minute without, three with); a piece is changed at once.
            answer = await ask(Instructions, attempt == 0 ? question : question + "\n\nYour last answer wasn't a JSON object as asked. Reply with only the JSON object.",
                picture is null ? [] : [picture], think ?? piece is null, ct).ConfigureAwait(false);
            if (answer is null)
                throw new FilmException("BlackBox's model couldn't be asked: is the chat server running?");
            json = JsonIn(answer);
        }
        if (json is null)
            return new FilmDirection(answer!.Trim(), board, [], []);
        var reply = json["reply"] is JsonValue r && r.TryGetValue<string>(out var said) ? said.Trim() : "";
        var notes = new List<string>();
        var change = json["change"] as JsonObject ?? [];
        Fix(change);
        if (piece is not null)
            Keep(board, piece, change, notes);
        var changed = change.Count > 0 ? Storyboard.Merge(board, change) : board;
        // Every shot starts on a picture a picture model makes; one written without gets its own.
        var added = changed != board ? changed.AddFirstFrames() : [];
        if (added.Count > 0)
            reply = Storyboard.JoinText(reply, $"{(added.Count == 1 ? "A shot" : "Some shots")} had no first frame, so I gave {(added.Count == 1 ? "it one" : "each one")} " +
                                               $"({string.Join(", ", added.Select(f => f.Id))}): a picture model makes what a shot starts on.");
        var make = new List<FilmPiece>();
        // The whole film is made piece by piece, as the user asks for each: a change to it makes nothing.
        var asked = piece is null ? [] : json["make"] as JsonArray ?? [];
        foreach (var id in asked.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null).OfType<string>())
        {
            var made = id.Equals(FilmTalk.Song, StringComparison.OrdinalIgnoreCase) ? FilmPiece.Song
                : changed.Frame(id) is { } f ? FilmPiece.Of(f)
                : changed.Shot(id) is { } s ? FilmPiece.Of(s) : null;
            if (made is null || make.Contains(made))
                continue;
            if (piece is not null && !Mine(changed, piece, made))
                continue;
            make.Add(made);
        }
        foreach (var frame in added)
            if (make.Any(p => p.Kind == FilmPieceKind.Shot && changed.Shot(p.Id)?.First == frame.Id))
                make.Add(FilmPiece.Of(frame));
        return new FilmDirection(reply.Length > 0 ? reply : "Done.", changed, Order(changed, make), notes);
    }

    /// <summary>The question for the model: the film as it is, the piece picked, what was said about it, and what is installed.</summary>
    private static string Question(Storyboard board, FilmPiece? piece, string request, FilmTalk talk, IReadOnlyList<FilmOption> installed, bool picture)
    {
        var sb = new StringBuilder();
        var (starts, length) = board.Timing();
        sb.AppendLine($"The film \"{board.Title}\": {board.Width}x{board.Height}, {N(length)} s in {board.Shots.Count} shot(s).");
        sb.AppendLine("Frames and references:");
        if (board.Frames.Count == 0) sb.AppendLine("  (none)");
        foreach (var f in board.Frames)
        {
            var used = board.Shots.Where(s => s.First == f.Id || s.Last == f.Id).Select(s => s.Id).ToList();
            sb.AppendLine($"  {f.Id}{(f.Name is { Length: > 0 } n ? $" \"{n}\"" : "")}{(f.Reference ? " (reference)" : used.Count > 0 ? $" (in {string.Join(", ", used)})" : "")}: " +
                          (f.Of is { } of ? $"where {of}'s clip ends, taken from it" : f.File is { Length: > 0 } ? "the person's own picture" : $"prompt \"{f.Prompt.Trim().ReplaceLineEndings(" ")}\"") +
                          (f.From.Count > 0 ? $"; from {string.Join(", ", f.From)}" : "") + (f.Model is { } m ? $"; model \"{m}\"" : "") +
                          $"; {State(board, FilmPiece.Of(f))}.");
        }
        sb.AppendLine("Shots, in order:");
        if (board.Shots.Count == 0) sb.AppendLine("  (none)");
        for (var i = 0; i < board.Shots.Count; i++)
        {
            var s = board.Shots[i];
            sb.AppendLine($"  {s.Id} ({N(starts[i])}-{N(starts[i] + s.Seconds)} s, {N(s.Seconds)} s, {(i == 0 ? "first" : s.First is not null && s.First == board.Shots[i - 1].Last ? "goes on from " + board.Shots[i - 1].Id : s.Transition)}): " +
                          $"first {s.First ?? "none"}, last {(s.LipSync ? "none (lip-synced)" : s.Last ?? "none")}; prompt \"{s.Prompt.Trim().ReplaceLineEndings(" ")}\"" +
                          (s.Camera.Length > 0 ? $"; camera \"{s.Camera}\"" : "") + (Storyboard.Speaks(s) ? $"; line \"{s.Line.Trim()}\"" : s.LipSync ? "; sings the song's part under it" : "") +
                          (s.Model is { } m ? $"; model \"{m}\"" : "") + $"; {State(board, FilmPiece.Of(s))}.");
        }
        var sound = board.Sound;
        sb.AppendLine("Sound: " + sound.Source switch
        {
            FilmSound.Song => $"a song, tags \"{sound.Tags.Trim()}\", " + (sound.Lyrics.Trim().Length > 0 ? $"lyrics \"{sound.Lyrics.Trim().ReplaceLineEndings(" / ")}\"" : "instrumental") +
                              (sound.Model is { } m ? $", model \"{m}\"" : "") + $"; {State(board, FilmPiece.Song)}",
            FilmSound.FromFile => $"the person's own sound file {sound.File}",
            _ => "none (each shot keeps its own sound)",
        } + (sound.Voice.Trim().Length > 0 ? $". The lines' voice: \"{sound.Voice.Trim()}\"." : "."));
        sb.AppendLine();

        var key = piece?.Id ?? FilmTalk.Film;
        if (piece is null)
        {
            sb.AppendLine("Nothing is picked: what they said is about the whole film. You may change anything: add, change or take out references, frames, shots, lines and the song. " +
                          "Nothing is made now (\"make\": []): they make each piece and look at it, so say in your reply what you changed and what is to be made.");
            if (board.Shots.Count == 0 || board.Shots.All(s => s.Prompt.Trim().Length == 0))
                sb.AppendLine("The film is new: give it a title, a reference for each person or place that comes back, frames made from those, shots of 3 to 8 seconds from different angles " +
                              "(each with its first frame, and a last one when where it ends matters), the lines when people speak, and a song as long as the film when they want music. " +
                              "Take out the empty starting shot and frames (s1, f1, f2) if you don't use them.");
        }
        else
        {
            sb.AppendLine($"They picked {Describe(board, piece)}.");
            sb.AppendLine($"You may change {Scope(board, piece)}; anything else stays. Make it again (\"make\") unless they only asked a question; " +
                          "when a frame it starts or ends on changes, make that frame first.");
        }
        var said = talk.About(key).TakeLast(8).ToList();
        if (said.Count > 0)
        {
            sb.AppendLine("What was said about it before:");
            foreach (var line in said)
                sb.AppendLine($"  {(line.Who == FilmTalk.You ? "They" : "You")}: {line.Text.ReplaceLineEndings(" ")}");
        }
        if (installed.Count > 0)
        {
            sb.AppendLine("Installed, to name in \"model\" by the name before the colon:");
            foreach (var o in installed.Take(40))
                sb.AppendLine($"  {o.Name}: {o.Label}");
        }
        if (picture)
            sb.AppendLine("The picture shows it as it is now.");
        sb.AppendLine();
        sb.Append($"They said: \"{request.Trim()}\"");
        return sb.ToString();
    }

    private static string State(Storyboard board, FilmPiece piece) =>
        piece.File(board) is not { } file || !File.Exists(file) ? "not made" : piece.IsCurrent(board) ? "made" : "made, but changed since";

    /// <summary>The piece, as the question names it.</summary>
    private static string Describe(Storyboard board, FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame when board.Frame(piece.Id) is { Reference: true } f => $"the reference {f.Id}" + (f.Name is { Length: > 0 } n ? $" \"{n}\"" : ""),
        FilmPieceKind.Frame => $"the frame {piece.Id}",
        FilmPieceKind.Shot => $"the shot {piece.Id}",
        _ => "the song",
    };

    private static string Scope(Storyboard board, FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Shot when board.Shot(piece.Id) is { } s => $"the shot {s.Id}" +
            (Own(s).ToList() is { Count: > 0 } frames ? $" and the frame{(frames.Count > 1 ? "s it starts and ends" : " it starts")} on ({string.Join(", ", frames)})" : "") +
            " and the lines' voice",
        FilmPieceKind.Song => "the sound (the song, the lines' voice)",
        _ => $"the frame {piece.Id}",
    };

    /// <summary>The frames a shot is made from: its first, and its last unless it is lip-synced (made from its first alone).</summary>
    private static IEnumerable<string> Own(FilmShot shot) => new[] { shot.First, shot.LipSync ? null : shot.Last }.OfType<string>().Distinct();

    /// <summary>Whether another piece is the picked one's own: a shot's frames, or the song.</summary>
    private static bool Mine(Storyboard board, FilmPiece picked, FilmPiece other) =>
        other == picked || picked.Kind == FilmPieceKind.Shot && other.Kind == FilmPieceKind.Frame && board.Shot(picked.Id) is { } s && Own(s).Contains(other.Id);

    /// <summary>A piece's change keeps to the piece: what it would change elsewhere is left, and said.</summary>
    private static void Keep(Storyboard board, FilmPiece piece, JsonObject change, List<string> notes)
    {
        var shot = piece.Kind == FilmPieceKind.Shot ? board.Shot(piece.Id) : null;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { piece.Id };
        if (shot is not null)
            foreach (var id in Own(shot))
                allowed.Add(id);
        foreach (var key in change.Select(p => p.Key).ToList())
        {
            var name = key.ToLowerInvariant();
            if (name is "frames" or "shots" && change[key] is JsonArray items)
            {
                foreach (var item in items.OfType<JsonObject>().ToList())
                {
                    var id = item["id"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
                    var ok = id is not null && allowed.Contains(id) && (name == "frames" ? piece.Kind != FilmPieceKind.Song : piece.Kind == FilmPieceKind.Shot);
                    // A shot may get a new frame of its own to start or end on.
                    if (!ok && id is not null && name == "frames" && shot is not null && board.Frame(id) is null && item["delete"] is null)
                    {
                        ok = true;
                        allowed.Add(id);
                    }
                    if (!ok)
                    {
                        items.Remove(item);
                        if (id is not null)
                            notes.Add($"It would also have changed {id}, which wasn't picked: pick it to change it.");
                    }
                }
            }
            else if (name == "sound" && change[key] is JsonObject sound)
            {
                // A shot's request may change the voice its line is said in; the rest of the sound is the song's.
                if (piece.Kind != FilmPieceKind.Song)
                    foreach (var k in sound.Select(p => p.Key).Where(k => !k.Equals("voice", StringComparison.OrdinalIgnoreCase)).ToList())
                        sound.Remove(k);
            }
            else if (name != "title" || piece.Kind != FilmPieceKind.Song)
                change.Remove(key);
        }
    }

    /// <summary>What a model writes that the storyboard reads otherwise: "seed": "new" for a new seed.</summary>
    private static void Fix(JsonObject change)
    {
        foreach (var item in new[] { change["frames"], change["shots"] }.OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>()).Append(change["sound"] as JsonObject).OfType<JsonObject>())
            if (item["seed"] is JsonValue seed && !seed.TryGetValue<long>(out _) && !(seed.TryGetValue<string>(out var s) && long.TryParse(s, out _)))
                item["seed"] = Storyboard.NewSeed();
    }

    /// <summary>Makes in an order that works: references, then frames made from others after those, then the song, then shots.</summary>
    private static List<FilmPiece> Order(Storyboard board, List<FilmPiece> make)
    {
        int Rank(FilmPiece p) => p.Kind switch
        {
            FilmPieceKind.Frame => board.Frame(p.Id) is { } f ? f.Reference ? 0 : f.From.Count > 0 ? 2 : 1 : 3,
            FilmPieceKind.Song => 4,
            _ => 5,
        };
        var ordered = make.Select((p, i) => (p, i)).OrderBy(x => Rank(x.p)).ThenBy(x => x.i).Select(x => x.p).ToList();
        // A frame taken from where a shot's clip ends comes right after that shot.
        foreach (var taken in ordered.Where(p => p.Kind == FilmPieceKind.Frame && board.Frame(p.Id)?.Of is not null).ToList())
        {
            int After() => ordered.FindIndex(p => p.Kind == FilmPieceKind.Shot && p.Id == board.Frame(taken.Id)!.Of);
            if (After() < 0)
                continue;
            ordered.Remove(taken);
            ordered.Insert(After() + 1, taken);
        }
        return ordered;
    }

    /// <summary>The JSON object in a model's answer, wherever it put it (in a code block, after a sentence); null when there is none.</summary>
    internal static JsonObject? JsonIn(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;
        try
        {
            return JsonNode.Parse(answer[start..(end + 1)], documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- looking at what was made ------------------------------------------------------------------------

    /// <summary>
    /// A fresh look at a piece just made: does a frame show what its prompt says, does a shot do what
    /// it should (with the numbers of how it moves and starts and ends). Null when it can't be looked at.
    /// </summary>
    /// <returns>The verdict (good or redo), a line to tell the user, and what is wrong (for a redo).</returns>
    public static async Task<(string Verdict, string Said, string? Reason)?> CheckAsync(Storyboard board, FilmPiece piece, IMediaDecoder? media, FilmAsk ask, CancellationToken ct)
    {
        const string Eyes = "You look at pictures carefully and say plainly what you see, faults included. You don't praise what isn't good.";
        string? answer;
        var faults = new List<string>();
        switch (piece.Kind)
        {
            case FilmPieceKind.Frame when board.Frame(piece.Id) is { Of: null } frame && File.Exists(board.FramePicture(frame)):
                if (await Task.Run(() => FilmCheck.Picture(board.FramePicture(frame), 1024), ct).ConfigureAwait(false) is not { } picture)
                    return null;
                var (before, beside) = Before(board, frame);
                var other = before is null ? null : await Task.Run(() => FilmCheck.Picture(board.FramePicture(before), 768), ct).ConfigureAwait(false);
                var question = $"This picture is {(frame.Reference ? "a reference sheet" : "a frame")} of a film, made from the prompt: \"{frame.Prompt.Trim()}\"" +
                               (frame.From.Count > 0 ? $" It was made from other pictures ({string.Join(", ", frame.From.Select(id => board.Frame(id)?.Label ?? id))}) so the same people and places look the same." : "") +
                               (other is null ? "" : "\n" + beside) +
                               "\nSay briefly and concretely: does it show what the prompt says? Faults a viewer would notice: warped faces or hands, extra fingers or limbs, " +
                               "things that couldn't be (food coming out of a head, a body merged with a chair), garbled text, black bars, a person who doesn't match the prompt, " +
                               "a camera much closer or farther than the prompt says (a wide view where it says close-up)" + (other is null ? "" : ", and what doesn't agree with the second picture") + ". " +
                               "Mark each SEVERE (a viewer would notice it and it spoils the picture) or MINOR.\nThen end with exactly one line: VERDICT: good, or VERDICT: redo: and the main reason. Redo for any SEVERE problem.";
                answer = await ask(Eyes, question, other is null ? [picture] : [picture, other], true, ct).ConfigureAwait(false);
                break;
            case FilmPieceKind.Shot when media is not null && board.Shot(piece.Id) is { } shot && File.Exists(board.ShotVideo(shot)):
                var look = await Task.Run(() => FilmCheck.Shot(board, shot, board.ShotVideo(shot), media), ct).ConfigureAwait(false);
                faults.AddRange(look.Faults);
                // Two looks: physics and bodies with a closer look at its busiest moment, then behaviour over time.
                answer = await FilmCheck.JudgeShotAsync(board, shot, look, null, (pictures, q, c) => ask(Eyes, q, pictures, true, c), ct).ConfigureAwait(false);
                break;
            default:
                return null;
        }
        if (answer is null)
            return null;
        var verdict = FilmCheck.Verdict(answer) ?? "good";
        var reason = verdict == "redo" ? FilmCheck.Reason(answer) : null;
        var said = reason is not null
            ? $"Checked: {reason}."
            : faults.Count > 0 ? $"Checked: it looks right, though {string.Join("; ", faults)}." : "Checked: it looks right.";
        return (verdict, said, reason);
    }

    /// <summary>
    /// The frame a viewer sees just before this one, and what to compare: the frame its shot starts on
    /// (the shot moves from one to the other, so what differs slides: the fox film's moon in "the upper
    /// left" and then "directly above" wandered across the sky), or the last frame of the shot before
    /// (the film cuts from one to the other: the fox film's second shot was made an open clearing with
    /// the moon on the other side, a jump only the look at the whole film saw, two shots later).
    /// </summary>
    private static (FilmFrame? Frame, string Text) Before(Storyboard board, FilmFrame frame)
    {
        if (frame.Reference)
            return (null, "");
        for (var i = 0; i < board.Shots.Count; i++)
        {
            var shot = board.Shots[i];
            if (!shot.LipSync && shot.Last == frame.Id && board.Frame(shot.First) is { } first && first.Id != frame.Id && File.Exists(board.FramePicture(first)))
                return (first, $"The second picture is {first.Label}, the frame its shot starts on: the shot moves from that picture to this one" +
                               (shot.Camera.Trim().Length > 0 ? $" (the camera: {shot.Camera.Trim().TrimEnd('.')})" : "") + $", and shows: \"{shot.Prompt.Trim().ReplaceLineEndings(" ")}\" " +
                               "So who or what the shot moves should be where the shot takes them by its end (a girl who walks to a mailbox stands beside it), " +
                               "and everything else stays put: the moon or sun, the horizon, trees, buildings, furniture and props in the same place and the same size in both, " +
                               "the camera at the same angle and distance unless the shot moves it. Something that should stay put but sits elsewhere or is another size is SEVERE: " +
                               "the video would slide it across the screen. So is someone not where the shot takes them: the video would walk them back.");
            if (i > 0 && shot.First == frame.Id && board.Shots[i - 1] is { LipSync: false, Last: { } end } && end != frame.Id && board.Frame(end) is { } last && File.Exists(board.FramePicture(last)))
                return (last, $"The second picture is {last.Label}, the last frame of the shot before: the film cuts from it to this one. Unless this prompt moves the story to another place " +
                              "or time, a viewer must take this as the same place a moment later: the same kind of place (not a forest turned into a clearing, not another room), the moon or sun " +
                              "on the same side and about the same size unless the camera turned around, the same light and weather, the same people looking the same. A jump like that is SEVERE.");
        }
        return (null, "");
    }

    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
}
