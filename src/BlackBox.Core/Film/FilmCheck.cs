using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using BlackBox.Core.Media;
using SkiaSharp;

namespace BlackBox.Core.Film;

/// <summary>What a look at a shot or a film found: in words, with the sheet of frames it is based on.</summary>
/// <param name="Sheet">A JPEG: the storyboard's frames above the clip's (or each shot of the film), with their times.</param>
/// <param name="Faults">What is surely wrong (a clip that doesn't start on its frame, a black or frozen clip), to fix before anything else.</param>
/// <param name="Closer">A JPEG: twelve frames close together from the stretch of a shot where the most changes, cropped to where things move.</param>
/// <param name="Cuts">For a film: its cuts.</param>
/// <param name="Motion">For a shot: how things in it move, measured from its pixels (<see cref="VideoMotion"/>), in words.</param>
public sealed record FilmLook(string Text, byte[]? Sheet, IReadOnlyList<string> Faults, byte[]? Closer = null, IReadOnlyList<FilmCut>? Cuts = null, string? Motion = null);

/// <summary>A cut as a JPEG: the last frame of the shot before above the first frame of the shot after.</summary>
/// <param name="At">The index of the shot after the cut.</param>
public sealed record FilmCut(byte[] Picture, int At);

/// <summary>What the fresh looks at a whole film found: the shots to make again, and why; no reason when it is good.</summary>
public sealed record FilmVerdict(string Answer, IReadOnlyList<string> Redo, string? Reason);

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
            var small = VideoMotion.Read(reader);
            var motion = VideoMotion.Describe(VideoMotion.Measure(small));
            if (motion.Length > 0)
                text.AppendLine(motion);
            var closer = CloserLook(reader, small, text);
            return new FilmLook(text.ToString().TrimEnd(), sheet, faults, closer, Motion: motion.Length > 0 ? motion : null);
        }
        finally
        {
            foreach (var (_, bitmap) in frames)
                bitmap.Dispose();
        }
    }

    /// <summary>
    /// The first of two fresh looks at a shot: physics and bodies, from its frames (OVERVIEW) and a
    /// closer look at the stretch where the most changes. Video models make slop a viewer sees at once
    /// at normal speed: noodles bursting out of a head as he eats, a face smearing as he lunges. The
    /// question this replaced judged the clip against its prompt, so a prompt asking for "noodles
    /// flying" excused them, and said small things were no reason to redo, so a half-noticed glitch
    /// passed; from 8 small frames qwen3.8 read the burst as hair fluffing up. Asked this way, with
    /// the closer look, it asked for a redo both times (the judging thread's test, 2026-10-19).
    /// </summary>
    /// <param name="closer">Whether a closer look comes with the overview.</param>
    /// <param name="heard">What was heard sung or said under it, if it was listened to.</param>
    public static string JudgeShot(Storyboard board, FilmShot shot, bool closer, string? heard = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You are the quality judge for one shot of an AI-made film{Title(board)}. Video models often make \"slop\" that a viewer spots instantly at normal speed: " +
                      "physically impossible events and unnatural motion, not just warped faces. Your job is to catch it, and also not to reject a shot for nothing.");
        sb.AppendLine($"The shot lasts {N(shot.Seconds)} s and should show: {shot.Prompt.Trim()}" + (shot.Camera.Trim().Length > 0 ? $" The camera: {shot.Camera.Trim().TrimEnd('.')}." : ""));
        Says(shot, heard, sb);
        sb.AppendLine("What the shot asks for never excuses impossible physics or behaviour: if it asks for flying food, splashes or a messy action, the food must still come from a real source " +
                      "(a plate, a fork, a mouth) and fall naturally, and people must still move like real people.");
        sb.AppendLine(closer ? "You get two pictures:" : "You get one picture:");
        sb.AppendLine("- OVERVIEW: " + TopRow(board, shot) + "8 frames spread over the whole shot, with their times.");
        if (closer)
            sb.AppendLine("- CLOSER LOOK: 12 frames close together in time, from the stretch where the most changes, cropped to where things move, with their times.");
        sb.AppendLine(closer
            ? "Step 1. Narrate the CLOSER LOOK frame by frame, like slow motion: for every moving thing (people, hands, food, liquid, particles, hair, cloth, vehicles, water) say where it is, where it comes from and where it goes next."
            : "Step 1. Narrate the OVERVIEW frame by frame: for every moving thing say where it is, where it comes from and where it goes next.");
        sb.AppendLine("Step 2. Check, giving the times:");
        sb.AppendLine("A. Physics: does everything move as it would in the real world? Things fall under gravity, come from a real source, keep their shape and amount, " +
                      "don't pass through solid things or come out of a body, don't appear or vanish.");
        sb.AppendLine("B. Bodies: faces, hands, limbs warping, smearing, melting or merging with objects; wrong anatomy.");
        sb.AppendLine("C. Behaviour: would a real person or animal move like this? A pose held unnaturally long or frozen while things around move, a lunge, twitch or speed that looks wrong, an action that makes no sense.");
        sb.AppendLine("D. Continuity: the same person, clothes, objects and place all through" + (board.Frame(shot.First) is not null ? ", and as in the storyboard's frames." : "."));
        sb.AppendLine("E. Does the shot show what it should, and does the camera do what was asked?");
        sb.AppendLine("Step 3. Mark each problem SEVERE (a viewer would notice it at normal speed and it spoils the shot) or MINOR. Don't invent problems: if a frame is simply blurred by fast motion, that is normal.");
        sb.Append("End with exactly one line: VERDICT: good, or VERDICT: redo: and the main reason. Redo for any SEVERE problem.");
        return sb.ToString();
    }

    /// <summary>
    /// The second fresh look at a shot: behaviour over time, from its frames alone. Every frame of a
    /// man frozen face-down in his plate for two seconds looks like a man eating; asked only about
    /// what each person does from frame to frame, qwen3.8 caught the freeze both times and passed a
    /// talking close-up both times.
    /// </summary>
    /// <param name="motion">The movement measured from the pixels, in words, if any: it says when something moves, where frames a second apart can't.</param>
    public static string JudgeBehaviour(Storyboard board, FilmShot shot, string? heard = null, string? motion = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You judge one shot of an AI-made film{Title(board)} for ONE thing only: behaviour over time. Video models often make people or animals freeze in a pose, " +
                      "hold an awkward position far too long, or move in ways no real person would, even when every single frame looks fine.");
        sb.AppendLine($"The shot lasts {N(shot.Seconds)} s and should show: {shot.Prompt.Trim()}");
        Says(shot, heard, sb);
        sb.AppendLine("The picture shows " + TopRow(board, shot) + "8 frames spread evenly over the whole shot, with their times.");
        // A girl walking steadily to a mailbox read as frozen for 2 s and then teleporting, from frames 0.7 s apart.
        if (motion is { Length: > 0 })
            sb.AppendLine(motion).AppendLine("The measured movement is reliable about when something moves: a stretch where nothing moves much is a freeze, " +
                                             "and something moving steadily across stretches is not one, whatever two frames side by side suggest.");
        sb.AppendLine("1. For each person or animal, say what they are doing in each frame, and what they must have done between one frame and the next.");
        sb.AppendLine("2. Is there a stretch of more than about 1.5 s where they hold the same pose while the shot calls for action (eating, talking, walking), or a pose no real person would keep that long " +
                      "(slumped or bent double, mid-fall, mid-jump, an arm stuck in the air)? Give the times.");
        sb.AppendLine("3. Is any movement between frames impossible or bizarre for a real person (teleporting, a lunge or twist that makes no sense for what they are doing)?");
        sb.AppendLine("Talking to the camera with small head and mouth movements is normal behaviour, not a frozen pose. With no person or animal in it, say so: that is good.");
        sb.Append("End with exactly one line: VERDICT: good, or VERDICT: redo: and the main reason.");
        return sb.ToString();
    }

    /// <summary>
    /// The third fresh look at a shot: whether the world stays put. In a fox film made from one
    /// sentence the moon wandered across the sky and the fox drifted against the snow while the
    /// shot was a still camera on a standing fox; the user saw the fox float, and both other looks
    /// had passed it once.
    /// </summary>
    /// <param name="motion">The movement measured from the pixels, in words, if any.</param>
    public static string JudgeSteadiness(Storyboard board, FilmShot shot, string? motion = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You judge one shot of an AI-made film{Title(board)} for ONE thing only: whether the world in it stays put. Video models often let the background drift " +
                      "(the moon wandering across the sky, trees or buildings sliding, growing or shrinking) and let people or animals slide, float or sink against the ground, " +
                      "while every single frame looks fine.");
        sb.AppendLine($"The shot lasts {N(shot.Seconds)} s and should show: {shot.Prompt.Trim()}");
        sb.AppendLine("What the camera should do: " + (shot.Camera.Trim().Length > 0 ? shot.Camera.Trim().TrimEnd('.') + "." : "nothing said (most likely it holds still)."));
        sb.AppendLine("The picture shows " + TopRow(board, shot) + "8 frames spread evenly over the whole shot, with their times.");
        if (motion is { Length: > 0 })
            sb.AppendLine(motion).AppendLine("The measured movement is reliable about which way and how far things go: the whole picture moving is the camera; something moving against the rest is a person, an animal or a thing.");
        sb.AppendLine("1. For each frame, say where the fixed things are (the moon or sun, the horizon, trees, buildings, furniture) and how big the moon or sun is (as a share of the frame's width), " +
                      "and where each person's or animal's feet or base touch the ground.");
        sb.AppendLine("2. Given what the camera does, do the fixed things move, grow, shrink, appear or vanish when they shouldn't? A camera that pans or pushes in moves everything together; " +
                      "one fixed thing moving on its own while the rest stays is wrong.");
        sb.AppendLine("3. Does anyone or anything that stands, sits or walks slide, float, rise off or sink into the ground, or change size against the things around it? Give the times.");
        sb.AppendLine("Mark each problem SEVERE (a viewer would notice it at normal speed) or MINOR.");
        sb.Append("End with exactly one line: VERDICT: good, or VERDICT: redo: and the main reason. Redo for any SEVERE problem.");
        return sb.ToString();
    }

    /// <summary>
    /// The fourth fresh look at a shot: whether things move as the shot asks. The user found the
    /// looks catching botched takes but often missing motion gone wrong: a subject moving the wrong
    /// way, a pose bending wrong, a prop or a person doing what nobody asked. Each movement asked for
    /// is held against the frames and the movement measured from the pixels (<see cref="VideoMotion"/>).
    /// </summary>
    /// <param name="motion">The measured movement in words; none when it couldn't be measured.</param>
    public static string JudgeMotion(Storyboard board, FilmShot shot, string? motion)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You judge one shot of an AI-made film{Title(board)} for ONE thing only: whether things move as the shot asks. Video models often move a subject the wrong way, " +
                      "bend a body into a pose nobody asked for, turn a head or walk toward the wrong side, make a prop move by itself or someone do something unasked, " +
                      "while every single frame looks fine.");
        sb.AppendLine($"The shot lasts {N(shot.Seconds)} s and should show: {shot.Prompt.Trim()}");
        sb.AppendLine("What the camera should do: " + (shot.Camera.Trim().Length > 0 ? shot.Camera.Trim().TrimEnd('.') + "." : "nothing said (most likely it holds still)."));
        sb.AppendLine("The picture shows " + TopRow(board, shot) + "8 frames spread evenly over the whole shot, with their times.");
        if (motion is { Length: > 0 })
            sb.AppendLine(motion).AppendLine("The measured movement is reliable about which way and how far things go; the frames say what is moving.");
        sb.AppendLine("1. List each movement the shot asks for: who or what moves, which body part, which way on screen, from where to where, and when.");
        sb.AppendLine("2. For each, say what the frames" + (motion is { Length: > 0 } ? " and the measured movement" : "") + " show: does it happen, with that body part, " +
                      "the way and as far as asked, at about that time? A movement the other way, or by another part (the whole body sliding where only the head should turn), is wrong.");
        sb.AppendLine("3. List what moves that nobody asked for: a prop or object moving by itself, someone turning, stepping or reaching unasked, a limb or the body changing pose " +
                      "where it should stay put, things sliding, rising or sinking, the camera moving when it should hold still. Give the times.");
        sb.AppendLine("Mark each problem SEVERE (a viewer would see the wrong movement at normal speed) or MINOR (a small extra movement that doesn't spoil the shot).");
        sb.Append("End with exactly one line: VERDICT: good, or VERDICT: redo: and the main reason. Redo for any SEVERE problem.");
        return sb.ToString();
    }

    private static string Title(Storyboard board) => board.Title.Trim().Length > 0 ? $" (\"{board.Title.Trim()}\")" : "";

    /// <summary>What the top row of a shot's overview is: the storyboard's frames it should start and end on.</summary>
    private static string TopRow(Storyboard board, FilmShot shot)
    {
        var first = board.Frame(shot.First);
        var last = shot.LipSync ? null : board.Frame(shot.Last);
        return first is null && last is null ? ""
            : "in its top row, the storyboard's " + (first is not null && last is not null ? "frames the shot should start on (left) and end on (right)" : first is not null ? "frame the shot should start on" : "frame it should end on") +
              "; under it, ";
    }

    private static void Says(FilmShot shot, string? heard, StringBuilder sb)
    {
        if (Storyboard.Speaks(shot))
            sb.AppendLine($"The person speaking in it says \"{shot.Line.Trim()}\": their mouth should move with the words, and they should look like they say them.");
        else if (shot.LipSync)
            sb.AppendLine("The people in it should sing or speak along with the soundtrack, their mouths moving.");
        if (heard is { Length: > 0 })
            sb.AppendLine(heard);
    }

    /// <summary>
    /// The fresh looks at a shot, one question each (one question per kind of fault catches more
    /// than one big question): physics and bodies (<see cref="JudgeShot"/>), behaviour over time
    /// (<see cref="JudgeBehaviour"/>), whether the world stays put (<see cref="JudgeSteadiness"/>),
    /// whether things move as asked (<see cref="JudgeMotion"/>).
    /// As one answer ending with one verdict: redo when any asks for it. Null when none could be had.
    /// </summary>
    public static async Task<string?> JudgeShotAsync(Storyboard board, FilmShot shot, FilmLook look, string? heard,
        Func<IReadOnlyList<byte[]>, string, CancellationToken, Task<string?>> ask, CancellationToken ct)
    {
        if (look.Sheet is not { } sheet)
            return null;
        var looks = new List<(string Name, string? Answer)>
        {
            ("Physics and bodies", await ask(look.Closer is { } closer ? [sheet, closer] : [sheet], JudgeShot(board, shot, look.Closer is not null, heard), ct).ConfigureAwait(false)),
            ("Behaviour over time", await ask([sheet], JudgeBehaviour(board, shot, heard, look.Motion), ct).ConfigureAwait(false)),
            ("Whether the world stays put", await ask([sheet], JudgeSteadiness(board, shot, look.Motion), ct).ConfigureAwait(false)),
            ("Movement as asked", await ask([sheet], JudgeMotion(board, shot, look.Motion), ct).ConfigureAwait(false)),
        };
        if (looks.All(l => l.Answer is null))
            return null;
        var redo = looks.Select(l => l.Answer).OfType<string>().Where(a => Verdict(a) == "redo").Select(Reason).FirstOrDefault();
        var sb = new StringBuilder();
        foreach (var (name, answer) in looks.Where(l => l.Answer is not null))
            sb.AppendLine($"{name}: {answer!.Trim()}").AppendLine();
        sb.Append(redo is null ? "VERDICT: good" : $"VERDICT: redo: {redo}");
        return sb.ToString();
    }

    /// <summary>The reason a fresh look gave for a redo ("VERDICT: redo: his face melts"), or a plain one.</summary>
    public static string Reason(string answer) =>
        RedoReason().Matches(answer).LastOrDefault() is { } m && m.Groups[1].Value.Trim().TrimEnd('.') is { Length: > 0 } reason ? reason : "something is off";

    [GeneratedRegex(@"VERDICT\s*:\s*\**\s*redo\s*\**\s*[:,\-–]?\s*(.+)", RegexOptions.IgnoreCase)]
    private static partial Regex RedoReason();

    // ---- the closer look ------------------------------------------------------------------------------

    /// <summary>
    /// Twelve frames close together from the ~0.9 s of a shot where the most changes, cropped to where
    /// things move, with their times: in the 8 frames spread over a shot, a one-second glitch falls in
    /// one small frame or between two. The stretch is found from how much each small grey frame
    /// differs from the one before, each measured against its own brightness, and a frame that
    /// brightens or darkens as a whole (a fade) not counted. Null for a clip too short to have one.
    /// </summary>
    private static byte[]? CloserLook(IVideoReader reader, List<VideoMotion.Frame> frames, StringBuilder text)
    {
        const int w = VideoMotion.Width, h = VideoMotion.Height;
        var (times, grays, means) = (frames.Select(f => f.Time).ToList(), frames.Select(f => f.Gray).ToList(), frames.Select(f => f.Mean).ToList());
        var n = times.Count;
        if (n < 8)
            return null;
        var fps = reader.FrameRate > 0 ? reader.FrameRate : (n - 1) / Math.Max(0.1, times[^1] - times[0]);
        var diff = new double[n];
        for (var i = 1; i < n; i++)
            diff[i] = Math.Min(means[i], means[i - 1]) < 25 || Math.Abs(means[i] - means[i - 1]) > 0.08 * means[i - 1] ? 0 : MeanAbs(grays[i], grays[i - 1]);
        var win = Math.Clamp((int)(fps * 0.9), 1, n - 1);
        var (s, best) = (0, -1.0);
        for (var i = 0; i + win <= n; i++)
        {
            var score = 0.0;
            for (var j = i; j < i + win; j++)
                score += diff[j];
            if (score > best)
                (s, best) = (i, score);
        }
        if (best <= 0)
            return null;
        var e = s + win;
        // Where in the picture things move in that stretch: a box around the most changing tenth.
        var acc = new double[w * h];
        for (var i = Math.Max(1, s); i < e; i++)
            for (var p = 0; p < acc.Length; p++)
                acc[p] += Math.Abs(grays[i][p] - grays[i - 1][p]);
        var threshold = acc.Order().ElementAt((int)(acc.Length * 0.9));
        int x0 = w, x1 = 0, y0 = h, y1 = 0;
        for (var p = 0; p < acc.Length; p++)
            if (acc[p] > threshold)
                (x0, x1, y0, y1) = (Math.Min(x0, p % w), Math.Max(x1, p % w), Math.Min(y0, p / w), Math.Max(y1, p / w));
        var (width, height) = (reader.Width, reader.Height);
        var box = x1 < x0
            ? new SKRectI(0, 0, width, height)
            : Box(x0 / (double)w * width, (x1 + 1) / (double)w * width, y0 / (double)h * height, (y1 + 1) / (double)h * height, width, height);
        // Twelve frames over that stretch and a little either side of it.
        var (a, b) = (Math.Max(0, s - win / 4), Math.Min(n - 1, e + win / 4));
        var picks = Enumerable.Range(0, 12).Select(i => times[(int)Math.Round(a + i * (b - a) / 11.0)]).ToList();
        var cells = new List<(string, SKBitmap)>();
        try
        {
            reader.Seek(TimeSpan.FromSeconds(Math.Max(0, picks[0] - 0.001)));
            var at = 0;
            while (at < picks.Count && reader.Next() is { } next)
            {
                if (next.Time.TotalSeconds + 0.001 < picks[at])
                    continue;
                using var bitmap = MediaPreview.ToBitmap(next.Frame);
                var crop = new SKBitmap(box.Width, box.Height);
                using (var canvas = new SKCanvas(crop))
                    canvas.DrawBitmap(bitmap, box, new SKRect(0, 0, box.Width, box.Height));
                while (at < picks.Count && next.Time.TotalSeconds + 0.001 >= picks[at])
                {
                    cells.Add(($"{next.Time.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture)} s", crop));
                    at++;
                }
            }
            if (cells.Count < 4)
                return null;
            text.AppendLine($"The most changes between {N(times[s])} and {N(times[Math.Min(e, n - 1)])} s: the closer look is that stretch, cropped to where things move.");
            using var grid = GridBitmap(cells, box.Width, box.Height, columns: 3);
            return Jpeg(grid);
        }
        finally
        {
            foreach (var bitmap in cells.Select(c => c.Item2).Distinct())
                bitmap.Dispose();
        }

        static double MeanAbs(float[] p, float[] q)
        {
            var sum = 0.0;
            for (var i = 0; i < p.Length; i++)
                sum += Math.Abs(p[i] - q[i]);
            return sum / p.Length;
        }

        // At least 55% of the picture's width, in its shape, around the middle of what moves.
        static SKRectI Box(double left, double right, double top, double bottom, int width, int height)
        {
            var bw = Math.Min(width, Math.Max(right - left, width * 0.55));
            var bh = Math.Min(height, bw * height / width);
            var (cx, cy) = ((left + right) / 2, (top + bottom) / 2);
            var x = (int)Math.Clamp(cx - bw / 2, 0, width - bw);
            var y = (int)Math.Clamp(cy - bh / 2, 0, height - bh);
            return new SKRectI(x, y, x + (int)bw, y + (int)bh);
        }
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
        var middles = new List<(string Label, SKBitmap Frame)>();
        // Near each shot's start as well as its middle, a row a shot: in the middle of a boy's
        // climb he was a small figure from behind, and the side-by-side look passed him drawn as a
        // cartoon in one shot and photographed in the next.
        var both = board.Shots.Count <= 6;
        try
        {
            for (var i = 0; i < board.Shots.Count; i++)
            {
                var (at, middle) = (starts[i] + Math.Min(0.5, board.Shots[i].Seconds / 4), starts[i] + board.Shots[i].Seconds / 2);
                if (both && FrameAt(reader, at) is { } start)
                    middles.Add(($"{board.Shots[i].Id} at {N(at)} s", start.Frame));
                if (FrameAt(reader, middle) is { } frame)
                    middles.Add(($"{board.Shots[i].Id} at {N(middle)} s", frame.Frame));
            }
            // Where the camera should go on from one shot into the next, the picture should too.
            for (var i = 1; i < board.Shots.Count; i++)
            {
                var (before, after) = (board.Shots[i - 1], board.Shots[i]);
                if (after.First is null || after.First != before.Last && board.Frame(after.First)?.Of != before.Id || after.Transition != FilmShot.Cut)
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
            byte[]? sheet = null;
            if (middles.Count > 0)
            {
                using var grid = GridBitmap(middles, reader.Width, reader.Height, columns: both ? 2 : middles.Count <= 4 ? middles.Count : middles.Count <= 9 ? 3 : 4);
                sheet = Jpeg(grid);
            }
            return new FilmLook(text.ToString().TrimEnd(), sheet, faults, Cuts: Cuts(board, decoder));
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
        sb.AppendLine(board.Shots.Count <= 6
            ? "You check a short film for the person making it, from a sheet with a row for each shot, in order: a frame near its start and one from its middle, labelled with the shot and the time."
            : "You check a short film for the person making it, from a sheet with one frame from the middle of each shot, in order, labelled with the shot and its time.");
        About(board, sb);
        sb.AppendLine("The shots should show:");
        foreach (var s in board.Shots)
            sb.AppendLine($"- {s.Id}: {s.Prompt.Trim().ReplaceLineEndings(" ")}");
        sb.AppendLine("Say briefly and concretely: do the same people look the same in every shot (face, hair, clothes, colours)? Is every shot in the same style: " +
                      "all photographs, or all the same kind of drawing or render? Say for each person in each shot whether they look photographed, a 3D render or drawn. Does the look stay the same? " +
                      "Does each shot show what it should, and do they tell the story in order? Which shots stand out as wrong, and why?");
        sb.AppendLine("Ask for a redo only for what a viewer would clearly notice: someone who looks like another person or animal, someone drawn as a cartoon or a 3D render in one shot " +
                      "and photographed in another, a place that changes where it should be the same, " +
                      "a shot that doesn't show its action. A part of someone out of frame in a close-up, or small differences in pose and framing, are no reason to redo a shot.");
        sb.Append("Then end with exactly one line: VERDICT: good, or VERDICT: redo shots s1, s2…: and the main reason.");
        return sb.ToString();
    }

    /// <summary>
    /// The second fresh look at a film: its cuts, each the frame before above the frame after. From
    /// one frame in the middle of each shot, qwen3.8 found the fox film the same place all through,
    /// where its second shot had become an open clearing with the moon on the other side of the sky
    /// and the fox facing the other way: frames side by side read as "a snowy forest at night" each.
    /// The two frames of a cut are one above the other, not side by side: side by side, qwen3.8
    /// measured where the moon was across the whole picture, and found a moon that hadn't moved
    /// (at 0.27 of each frame's width) jumping from 0.13 to 0.6, in 4 runs of 4.
    /// </summary>
    /// <param name="at">The index of the shot after each cut, one for each picture.</param>
    public static string JudgeCuts(Storyboard board, IReadOnlyList<int> at)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"You judge the cuts of an AI-made film{Title(board)} for ONE thing only: whether the world stays the same across each cut. Each shot's frames are made " +
                      "on their own, so the shot after a cut often lands somewhere else: a dense forest becomes an open clearing, a kitchen another kitchen, the moon or sun jumps " +
                      "to the other side of the sky or changes size, day turns to dusk, someone's clothes, hair or fur change, a photo turns into a cartoon, while the story has the same place and moment.");
        sb.AppendLine("The shots should show:");
        foreach (var s in board.Shots)
            sb.AppendLine($"- {s.Id}: {s.Prompt.Trim().ReplaceLineEndings(" ")}");
        sb.AppendLine($"You get {(at.Count == 1 ? "one picture" : $"{at.Count} pictures, one per cut")}: on top the last frame of the shot before, below it the first frame of the shot after, " +
                      "each labelled, the same size and lined up, so what stays in the same place is straight above itself. The cuts:");
        foreach (var i in at)
        {
            var (before, after) = (board.Shots[i - 1], board.Shots[i]);
            var shared = Refs(board, before).Intersect(Refs(board, after), StringComparer.OrdinalIgnoreCase).Select(board.Frame).OfType<FilmFrame>().ToList();
            sb.AppendLine($"- {before.Id} to {after.Id}, a {after.Transition}" + (shared.Count > 0
                ? $"; both are made from the same reference pictures ({string.Join("; ", shared.Select(f => $"{f.Id}: \"{Gist(f)}\""))}), so what those show should look the same."
                : "."));
        }
        sb.AppendLine("1. For each cut, describe each of its two frames in a few words: the place (what is right behind the people or animals, the ground, how close and dense the trees, " +
                      "buildings or furniture are), the light (the moon, sun or brightest light: how far across its own frame its centre is, from 0 at the frame's left edge to 1 at its right, " +
                      "and how wide it is as a share of the frame's width), the time of day and weather, the style (photo, cartoon, painting), each person or animal (how they look, which way they face), " +
                      "and how close the camera is (wide, medium or close-up).");
        sb.AppendLine("2. Compare: would a viewer take the frame after the cut as the same place a moment later, unless the story moves on? A new angle may show another part of the same place, " +
                      "but not another kind of place; the moon or sun stays on the same side (its centre within about a quarter of the width) and about the same size unless the camera turned around; " +
                      "the same people look the same. What the shots say about the camera (still, locked) holds inside each shot, not across a cut: after a cut the camera may stand a little " +
                      "elsewhere or closer, so the moon, trees or furniture a little higher, lower or bigger, or another part of the same forest or room in view, are MINOR. " +
                      "Judge what the frames show: words in the storyboard that put the moon somewhere else don't make a jump less jarring. Where the story does move to another place or time, " +
                      "say whether the frame after shows the one it should.");
        sb.AppendLine("Mark each problem SEVERE (a viewer would notice it at the cut) or MINOR (a small difference in pose, framing or a detail).");
        sb.Append("End with exactly one line: VERDICT: good, or VERDICT: redo shots s2, s5…: and the main reason, naming the shot that breaks with the others: " +
                  "usually the one after the cut, or the one before it if that is the odd one out. Redo for any SEVERE problem.");
        return sb.ToString();
    }

    /// <summary>
    /// The fresh looks at a whole film: the shots side by side (<see cref="JudgeFilm"/>), then its
    /// cuts (<see cref="JudgeCuts"/>), up to four at a time. Null when none could be had.
    /// </summary>
    public static async Task<FilmVerdict?> JudgeFilmAsync(Storyboard board, FilmLook look,
        Func<IReadOnlyList<byte[]>, string, CancellationToken, Task<string?>> ask, CancellationToken ct)
    {
        var looks = new List<(string Name, string? Answer)>();
        if (look.Sheet is { } sheet)
            looks.Add(("The shots side by side", await ask([sheet], JudgeFilm(board), ct).ConfigureAwait(false)));
        foreach (var cuts in (look.Cuts ?? []).Chunk(4))
            looks.Add(("The cuts", await ask(cuts.Select(c => c.Picture).ToList(), JudgeCuts(board, cuts.Select(c => c.At).ToList()), ct).ConfigureAwait(false)));
        if (looks.All(l => l.Answer is null))
            return null;
        var (redo, reasons) = (new List<string>(), new List<string>());
        foreach (var answer in looks.Select(l => l.Answer).OfType<string>().Where(a => Verdict(a) == "redo"))
        {
            // The shots it names before its reason ("redo shots s2: … breaking with s1" is s2's).
            var said = Reason(answer);
            var rest = WithoutShots(board, said);
            var shots = rest != said ? said[..^rest.Length] : said;
            redo.AddRange(board.Shots.Select(s => s.Id).Where(id => !redo.Contains(id) && Regex.IsMatch(shots, $@"\b{Regex.Escape(id)}\b", RegexOptions.IgnoreCase)));
            reasons.Add(rest);
        }
        var sb = new StringBuilder();
        foreach (var (name, answer) in looks.Where(l => l.Answer is not null))
            sb.AppendLine($"{name}: {answer!.Trim()}").AppendLine();
        var reason = reasons.Count == 0 ? null : string.Join("; ", reasons);
        sb.Append(reason is null ? "VERDICT: good" : $"VERDICT: redo{(redo.Count > 0 ? " shots " + string.Join(", ", redo) : "")}: {reason}");
        return new FilmVerdict(sb.ToString(), redo, reason);
    }

    /// <summary>A redo's reason without the shots it names first ("shots s2, s3: the place changes").</summary>
    private static string WithoutShots(Storyboard board, string reason)
    {
        var colon = reason.IndexOf(':');
        if (colon <= 0 || colon > 80)
            return reason;
        var words = reason[..colon].Split([' ', ',', '&'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !w.Equals("shot", StringComparison.OrdinalIgnoreCase) && !w.Equals("shots", StringComparison.OrdinalIgnoreCase) && !w.Equals("and", StringComparison.OrdinalIgnoreCase)).ToList();
        return words.Count > 0 && words.All(w => board.Shot(w.Trim('*', '.')) is not null) && reason[(colon + 1)..].Trim() is { Length: > 0 } rest ? rest : reason;
    }

    /// <summary>The reference pictures a shot's frames are made from, however far back.</summary>
    private static HashSet<string> Refs(Storyboard board, FilmShot shot)
    {
        var refs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string? id)
        {
            if (id is null || !seen.Add(id) || board.Frame(id) is not { } frame)
                return;
            if (frame.Reference)
                refs.Add(frame.Id);
            else if (frame.Of is { } of && board.Shot(of) is { } before)
                Walk(before.First);
            else
                foreach (var from in frame.From)
                    Walk(from);
        }
        Walk(shot.First);
        Walk(shot.LipSync ? null : shot.Last);
        return refs;
    }

    /// <summary>A reference by its name, or the first words of its prompt.</summary>
    private static string Gist(FilmFrame frame)
    {
        if (frame.Name is { Length: > 0 } name)
            return name;
        var words = frame.Prompt.Trim().ReplaceLineEndings(" ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(14)).TrimEnd(',', '.') + (words.Length > 14 ? "…" : "");
    }

    /// <summary>
    /// The cuts of a film from its shots' clips (not the joined film, where a fade would be half
    /// through): the last frame of the shot before above the first of the shot after, a picture each.
    /// </summary>
    private static List<FilmCut> Cuts(Storyboard board, IMediaDecoder decoder)
    {
        var cuts = new List<FilmCut>();
        for (var i = 1; i < board.Shots.Count; i++)
        {
            var (before, after) = (board.Shots[i - 1], board.Shots[i]);
            using var end = Edge(board.ShotVideo(before), last: true);
            using var start = end is null ? null : Edge(board.ShotVideo(after), last: false);
            if (end is null || start is null)
                continue;
            using var picture = GridBitmap([($"{before.Id} ends", end), ($"{after.Id} starts", start)], board.Width, board.Height, columns: 1, sheetWidth: 768);
            cuts.Add(new FilmCut(Jpeg(picture), i));
        }
        return cuts;

        SKBitmap? Edge(string clip, bool last)
        {
            if (!File.Exists(clip))
                return null;
            using var reader = decoder.OpenVideo(clip, 512);
            return reader is null ? null : FrameAt(reader, last ? reader.Duration.TotalSeconds - 0.1 : 0, first: !last)?.Frame;
        }
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

    /// <summary>A clip's very last frame; null when none can be read.</summary>
    public static SKBitmap? LastFrame(IVideoReader reader)
    {
        reader.Seek(TimeSpan.FromSeconds(Math.Max(0, reader.Duration.TotalSeconds - 0.5)));
        SKBitmap? last = null;
        while (reader.Next() is { } next)
        {
            last?.Dispose();
            last = MediaPreview.ToBitmap(next.Frame);
        }
        return last;
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

    /// <summary>Pictures in a grid, each with its label in a corner, <see cref="SheetWidth"/> wide unless said.</summary>
    private static SKBitmap GridBitmap(IReadOnlyList<(string Label, SKBitmap Frame)> cells, int width, int height, int columns, int sheetWidth = SheetWidth)
    {
        columns = Math.Clamp(columns, 1, Math.Max(1, cells.Count));
        var rows = (cells.Count + columns - 1) / columns;
        var cellW = (sheetWidth - (columns - 1) * 4) / columns;
        var cellH = Math.Max(16, (int)Math.Round(cellW * (double)height / Math.Max(1, width)));
        var bitmap = new SKBitmap(sheetWidth, rows * cellH + (rows - 1) * 4);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(20, 20, 20));
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        // Times a model can read: qwen3.8 and Claude Haiku guessed them at a sixteenth of a cell.
        using var font = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), Math.Clamp(cellW / 9f, 16, 34));
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

    /// <summary>
    /// A clip as a row of its frames, one a second, each <paramref name="frameHeight"/> high: what
    /// a shot's block on the timeline shows. Null when the clip can't be read.
    /// </summary>
    public static byte[]? Strip(string clip, IMediaDecoder decoder, int frameHeight)
    {
        using var reader = decoder.OpenVideo(clip, Math.Max(64, frameHeight * 2));
        if (reader is null)
            return null;
        var seconds = Math.Max(0.5, reader.Duration.TotalSeconds);
        var count = Math.Clamp((int)Math.Ceiling(seconds), 1, 60);
        var frameWidth = (int)Math.Round(frameHeight * 16 / 9.0);
        using var strip = new SKBitmap(frameWidth * count, frameHeight);
        using (var canvas = new SKCanvas(strip))
        {
            canvas.Clear(new SKColor(20, 20, 20));
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            for (var i = 0; i < count; i++)
            {
                if (FrameAt(reader, Math.Min(seconds - 0.05, i + 0.5), first: i == 0) is not { } frame)
                    continue;
                using var bitmap = frame.Frame;
                var scale = Math.Max(frameWidth / (double)bitmap.Width, frameHeight / (double)bitmap.Height);
                var (w, h) = ((float)(bitmap.Width * scale), (float)(bitmap.Height * scale));
                var x = i * frameWidth;
                canvas.Save();
                canvas.ClipRect(new SKRect(x, 0, x + frameWidth, frameHeight));
                canvas.DrawBitmap(bitmap, new SKRect(x + (frameWidth - w) / 2, (frameHeight - h) / 2, x + (frameWidth + w) / 2, (frameHeight + h) / 2), paint);
                canvas.Restore();
            }
        }
        return Jpeg(strip);
    }

    /// <summary>A frame from the middle of a clip as a JPEG no bigger than <paramref name="maxSide"/>; null when it can't be read.</summary>
    public static byte[]? Poster(string clip, IMediaDecoder decoder, int maxSide)
    {
        using var reader = decoder.OpenVideo(clip, maxSide);
        if (reader is null || FrameAt(reader, reader.Duration.TotalSeconds / 2) is not { } frame)
            return null;
        using var bitmap = frame.Frame;
        return Jpeg(bitmap);
    }

    /// <summary>A picture file as a JPEG no bigger than <paramref name="maxSide"/>, to show a model; null when it can't be read.</summary>
    public static byte[]? Picture(string file, int maxSide)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(file);
            if (bitmap is null)
                return null;
            var scale = Math.Min(1, maxSide / (double)Math.Max(bitmap.Width, bitmap.Height));
            if (scale >= 1)
                return Jpeg(bitmap);
            using var small = bitmap.Resize(new SKImageInfo((int)(bitmap.Width * scale), (int)(bitmap.Height * scale)), SKFilterQuality.High);
            return small is null ? null : Jpeg(small);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
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
