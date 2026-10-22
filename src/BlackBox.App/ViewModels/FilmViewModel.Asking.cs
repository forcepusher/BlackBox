using System.Diagnostics;
using Avalonia.Threading;
using BlackBox.Core.Comfy;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- telling BlackBox what to do ----------------------------------------------------------------------------

    /// <summary>What the user is typing to BlackBox about the piece picked.</summary>
    [ObservableProperty]
    private string _request = "";

    /// <summary>Sends what the user typed: BlackBox changes the piece (or the film) and makes what it should.</summary>
    [RelayCommand]
    private async Task Send()
    {
        var text = Request.Trim();
        if (text.Length == 0)
            return;
        if (IsWorking)
        {
            Status = "BlackBox is still busy with the last thing: wait for it, or stop it.";
            return;
        }
        if (_ask is null)
        {
            Status = "BlackBox's chat model isn't set up, so it can't be asked: set a server in the main window first.";
            return;
        }
        var ask = _ask;
        Request = "";
        var picked = Selected as IFilmPiece;
        var piece = picked?.Piece;
        var key = piece?.Id ?? FilmTalk.Film;
        _talk.Add(key, FilmTalk.You, text);
        Say(key, "Thinking it over…");
        SaveNow(force: true);
        SaveTalk();
        await RunJobAsync(key, picked, async (progress, ct) =>
        {
            IReadOnlyList<FilmOption> installed = [];
            if (piece is not null)
            {
                progress("Looking at what ComfyUI has installed…");
                installed = await InstalledAsync(piece, progress, ct);
            }
            var picture = await Task.Run(() => PictureOf(piece), ct);
            progress("Thinking it over…");
            var direction = await FilmDirector.DirectAsync(_board, piece, text, _talk, installed, picture, ask, ct);
            var changed = direction.Board != _board;
            if (changed)
            {
                var title = _board.Title;
                _board = direction.Board;
                _dirty = true;
                SaveNow(force: true);
                Rebuild();
                if (_board.Title != title)
                    RefreshFilms();
            }
            var reply = direction.Reply + (direction.Notes.Count > 0 ? " " + string.Join(" ", direction.Notes.Distinct()) : "");
            if (piece is null && changed && Missing() is not null)
                reply += " Pick a piece to make it, or press Make what's missing.";
            Say(key, reply, replace: true);
            SaveTalk();
            foreach (var next in direction.Make)
                await MakePieceAsync(next, key, ct);
        });
    }

    /// <summary>Makes the piece picked as it is (no change asked for), or again.</summary>
    [RelayCommand]
    private async Task MakePicked()
    {
        if (Selected is not IFilmPiece picked || IsWorking)
            return;
        await RunJobAsync(picked.Piece.Id, picked, (_, ct) => MakePieceAsync(picked.Piece, picked.Piece.Id, ct));
    }

    /// <summary>Another take of the piece picked: the same settings with a new seed.</summary>
    [RelayCommand]
    private async Task AnotherTake()
    {
        if (Selected is not IFilmPiece picked || IsWorking)
            return;
        switch (picked)
        {
            case FrameItem f: f.Frame.Seed = Storyboard.NewSeed(); break;
            case ShotItem s: s.Shot.Seed = Storyboard.NewSeed(); break;
            case SoundItem: _board.Sound.Seed = Storyboard.NewSeed(); break;
        }
        _talk.Add(picked.Piece.Id, FilmTalk.You, "Another take, please.");
        SaveNow(force: true);
        await RunJobAsync(picked.Piece.Id, picked, (_, ct) => MakePieceAsync(picked.Piece, picked.Piece.Id, ct));
    }

    /// <summary>The user picked a picture of their own for the frame picked: it is taken at once, cut to the film's frame.</summary>
    public async Task UseOwnPicture(string path)
    {
        if (Selected is not FrameItem frame || IsWorking)
            return;
        frame.TakeOwnPicture(path);
        _talk.Add(frame.Id, FilmTalk.You, $"Use my picture {Path.GetFileName(path)}.");
        SaveNow(force: true);
        await RunJobAsync(frame.Id, frame, (_, ct) => MakePieceAsync(frame.Piece, frame.Id, ct));
    }

    /// <summary>The user picked a sound file of their own as the film's soundtrack.</summary>
    public void UseOwnSound(string path)
    {
        Sound.TakeOwnSound(path);
        _talk.Add(FilmTalk.Song, FilmTalk.You, $"Use my sound file {Path.GetFileName(path)}.");
        SaveNow(force: true);
        SaveTalk();
        RefreshStates();
        RefreshTalk();
        RefreshDetails();
    }

    /// <summary>Looks at the piece picked again, as it is.</summary>
    [RelayCommand]
    private async Task CheckAgain()
    {
        if (Selected is not IFilmPiece picked || IsWorking || _ask is null)
            return;
        var key = picked.Piece.Id;
        Say(key, "Looking at it…");
        await RunJobAsync(key, picked, async (_, ct) =>
        {
            var check = await FilmDirector.CheckAsync(_board, picked.Piece, _previews.Decoder, _ask, ct);
            if (check is { } c && picked.Piece.Workflow(_board) is { } wf)
                FilmTakes.RememberLook(wf, c.Verdict, c.Said);
            if (check?.Reason is not null)
                Say(key, check.Value.Said + " Say what to change, or ask for another take.", replace: true);
            else
                Say(key, check?.Said ?? "It couldn't be looked at: it isn't made yet, or the chat model doesn't see pictures.", replace: true);
            SaveTalk();
        });
    }

    /// <summary>Looks at the joined film again (its shots side by side and each cut) and fixes what the looks find.</summary>
    [RelayCommand]
    private async Task LookAtFilm()
    {
        if (IsWorking || _ask is null)
            return;
        if (!HasFilm)
        {
            Status = "The film isn't joined yet: Make what's missing makes and joins it.";
            return;
        }
        await RunJobAsync(FilmTalk.Film, null, async (_, ct) =>
        {
            await LookAtFilmAsync(ct);
            SaveTalk();
        });
    }

    /// <summary>How many times a piece the look finds wrong is fixed and made again before the user is told what is left.</summary>
    private const int Fixes = 2;

    /// <summary>
    /// Makes a piece: the one there kept as a take, the new one made as the storyboard says, and a
    /// fresh look at it, all said in the piece's talk (and <paramref name="key"/>'s, where the request was).
    /// What the look finds wrong is fixed and made again, up to <see cref="Fixes"/> times: the user
    /// found a pass the look should have failed dismissive, and a flagged piece left for them lenient.
    /// </summary>
    /// <param name="round">How many fixes came before this make.</param>
    private async Task MakePieceAsync(FilmPiece piece, string key, CancellationToken ct, int round = 0)
    {
        // A shot starts on a picture a picture model makes: one without gets a first frame, and its frames are made first.
        if (piece.Kind == FilmPieceKind.Shot && _board.Shot(piece.Id) is { } shot)
        {
            if (_board.Frame(shot.First) is null && _board.AddFirstFrames().Count > 0)
            {
                _dirty = true;
                SaveNow(force: true);
                Rebuild();
            }
            foreach (var id in new[] { shot.First, shot.LipSync ? null : shot.Last }.OfType<string>().Distinct())
                if (_board.Frame(id) is { } frame && !FilmMaker.IsCurrent(_board, frame) && (frame.File is { Length: > 0 } || frame.Prompt.Trim().Length > 0 || frame.Of is not null))
                    await MakePieceAsync(FilmPiece.Of(frame), key, ct, Fixes);
        }
        string? fix = null;
        var item = ItemOf(piece);
        var label = Lower(item?.Title ?? piece.Id);
        if (item is not null)
            item.IsWorking = true;
        try
        {
            FilmTakes.Keep(_board, piece);
            Say(key, $"Making {label}…");
            var clock = Stopwatch.StartNew();
            void Progress(string s) => Dispatcher.UIThread.Post(() =>
            {
                if (item is not null)
                    item.State = s;
                _talk.ReplaceLast(key, $"Making {label}: {s}");
                if (key == TalkKey && Talk.Count > 0)
                    Talk[^1] = new TalkItem($"Making {label}: {s} · {Core.Chats.ChatTimes.Stopwatch(clock.Elapsed)}", false, true);
            });
            FilmMade made = piece.Kind switch
            {
                FilmPieceKind.Frame => await _maker.MakeFrameAsync(_board, _board.Frame(piece.Id)!, Progress, CancellationToken.None, ct),
                FilmPieceKind.Shot => await _maker.MakeShotAsync(_board, _board.Shot(piece.Id)!, Progress, CancellationToken.None, ct),
                _ => await _maker.MakeSongAsync(_board, Progress, CancellationToken.None, ct, takes: 3),
            };
            if (piece.Kind == FilmPieceKind.Song)
            {
                _songScore = (made.File, File.GetLastWriteTimeUtc(made.File), made.Score);
                _dirty = true;
                SaveNow(force: true); // the seed of the song kept
            }
            var said = $"Made {label} in {Core.Chats.ChatTimes.Stopwatch(made.Took)}" + (made.Template is { } t ? $" with {Short(t)}" : "") + "." +
                       (made.Score is { } score ? $" {Capital(score.Describe())}." : "") +
                       string.Concat(made.Notes.Where(Worth).Take(3).Select(n => " " + n));
            Say(key, said, replace: true);
            if (_ask is not null && piece.Kind != FilmPieceKind.Song)
            {
                Say(key, said + " Looking at it…", replace: true);
                if (item is not null)
                    item.State = "Looking at it…";
                var check = await FilmDirector.CheckAsync(_board, piece, _previews.Decoder, _ask, ct);
                if (check is { } c)
                {
                    if (piece.Workflow(_board) is { } wf)
                        FilmTakes.RememberLook(wf, c.Verdict, c.Said);
                    fix = c.Reason;
                    Say(key, said + " " + c.Said + (fix is null ? "" : round < Fixes ? $" Fixing it ({round + 1} of {Fixes})…" :
                        $" It still isn't right after {Fixes} fixes: say what to change, or pick an earlier take."), replace: true);
                }
                else
                    Say(key, said, replace: true);
            }
            if (key != piece.Id)
                _talk.Add(piece.Id, FilmTalk.BlackBox, _talk.About(key)[^1].Text);
        }
        finally
        {
            if (item is not null)
                item.IsWorking = false;
            SaveTalk();
            RefreshStates();
            if (Selected is IFilmPiece { Piece: var picked } && picked == piece)
            {
                RefreshTakes();
                RefreshDetails();
            }
        }
        if (fix is not null && round < Fixes && !ct.IsCancellationRequested)
            await FixAsync(piece, key, fix, ct, round);
    }

    /// <summary>
    /// What the look found wrong with a piece, handed to BlackBox to change what it is made from
    /// (its prompt, its frames, a new seed), then made again and looked at again.
    /// </summary>
    /// <param name="seen">What the look saw it on, when not the piece alone (the film's cuts), and what that picture is.</param>
    private async Task FixAsync(FilmPiece piece, string key, string found, CancellationToken ct, int round, (byte[] Picture, string Text)? seen = null)
    {
        if (_ask is not { } ask)
            return;
        var installed = await InstalledAsync(piece, _ => { }, ct);
        var picture = seen?.Picture ?? await Task.Run(() => PictureOf(piece), ct);
        // A shot between two frames that disagree on what stays put moves it: the fox film's frames put
        // the moon "in the upper left" and "directly above", and three takes of rewording the action
        // couldn't hold it still.
        var frames = piece.Kind == FilmPieceKind.Shot && _board.Shot(piece.Id) is { LipSync: false } shot && _board.Frame(shot.First) is { } first && _board.Frame(shot.Last) is { } last
            ? $" Its first frame ({first.Id}) is: \"{first.Prompt.Trim()}\". Its last frame ({last.Id}) is: \"{last.Prompt.Trim()}\". Compare where the things that stay put " +
              "(the moon, the trees, the horizon, where someone stands) and the camera's angle are in the two: the shot moves whatever differs, so if they differ, " +
              $"that is the cause: change the last frame ({last.Id}) to agree with the first, keeping only the change the shot is for. " +
              // The picture model may not draw what a frame's prompt says: a girl the prompt put beside a mailbox
              // stood two steps from it, so every take walked her back there, whatever the shot's words.
              "The picture's top row shows the two frames as they were made, which may not be what their prompts say: if the last one doesn't show where the shot " +
              $"takes everyone (someone not where they end up, a prop not in hand), the video goes there anyway, so make the last frame ({last.Id}) again, from the first."
            // A shot's last frame made afresh from the references lost the first's framing: a girl's
            // mailbox moved and grew, and the fix that dropped the first frame from "from" made it worse.
            : piece.Kind == FilmPieceKind.Frame && _board.Shots.FirstOrDefault(s => !s.LipSync && s.Last == piece.Id && s.First is not null && s.First != piece.Id) is { } ending
            ? $" It is the last frame of {ending.Id}, which moves from {ending.First} to it: keep it made from {ending.First} (in its \"from\", with the references), " +
              $"so it keeps {ending.First}'s framing and everything that stays put, and say in its prompt only what the shot changes."
            : "";
        var direction = await FilmDirector.DirectAsync(_board, piece,
            $"A fresh look at it as just made found this wrong: {found}. Change what it is made from so that it doesn't happen again, keeping what was asked for." + (seen is { Text: var text } ? " " + text : "") + frames +
            " Otherwise write the action so a video model can't get it wrong, or only give it a new seed if nothing else would help. Keep the model it is made with unless the user asked for another.",
            _talk, installed, picture, ask, ct, think: true);
        var board = direction.Board;
        // Made again whatever was changed: with no change, at least another seed.
        if (piece.IsCurrent(board))
        {
            switch (piece.Kind)
            {
                case FilmPieceKind.Frame when board.Frame(piece.Id) is { } f: f.Seed = Storyboard.NewSeed(); break;
                case FilmPieceKind.Shot when board.Shot(piece.Id) is { } s: s.Seed = Storyboard.NewSeed(); break;
            }
        }
        _board = board;
        _dirty = true;
        SaveNow(force: true);
        Rebuild();
        Say(key, direction.Reply);
        SaveTalk();
        foreach (var other in direction.Make.Where(p => p != piece && !p.IsCurrent(_board)))
            await MakePieceAsync(other, key, ct, Fixes);
        await MakePieceAsync(piece, key, ct, round + 1);
    }

    /// <summary>A note from making a piece worth telling the user: not what is so by design (a lip-synced shot made from its first frame alone).</summary>
    private static bool Worth(string note) =>
        !note.Contains("no place for the size", StringComparison.Ordinal) && !note.Contains("made from its first frame only", StringComparison.Ordinal) &&
        !note.StartsWith("Using the ComfyUI", StringComparison.Ordinal) && !note.StartsWith("Started ComfyUI", StringComparison.Ordinal);

    /// <summary>The next piece not made or out of date, in the order they have to be made: references, frames, the song, shots.</summary>
    private FilmPiece? Missing()
    {
        bool Stale(FilmFrame f) => !FilmMaker.IsCurrent(_board, f);
        // A frame taken from where a shot's clip ends, once that shot is made as it is now.
        bool Taken(FilmFrame f) => f.Of is { } of && _board.Shot(of) is { } before && FilmMaker.IsCurrent(_board, before);
        var needed = _board.Shots.SelectMany(s => new[] { s.First, s.LipSync ? null : s.Last }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The frames shots need, and what those are made from, a reference before what is made from it.
        var frames = new List<FilmFrame>();
        void Need(string id, int depth)
        {
            if (depth > 6 || _board.Frame(id) is not { } f || frames.Contains(f))
                return;
            foreach (var from in f.From)
                Need(from, depth + 1);
            frames.Add(f);
        }
        foreach (var id in needed)
            Need(id, 0);
        foreach (var f in _board.Frames.Where(f => f.Reference))
            Need(f.Id, 0);
        if (frames.FirstOrDefault(f => Stale(f) && (f.File is { Length: > 0 } || f.Prompt.Trim().Length > 0 || Taken(f)) && f.From.All(id => _board.Frame(id) is not { } src || !Stale(src))) is { } frame)
            return FilmPiece.Of(frame);
        if (_board.Sound.Source == FilmSound.Song && _board.Sound.Tags.Trim().Length > 0 && !FilmMaker.SongIsCurrent(_board))
            return FilmPiece.Song;
        return _board.Shots.FirstOrDefault(s => !FilmMaker.IsCurrent(_board, s) && s.Prompt.Trim().Length > 0 &&
                                                new[] { s.First, s.LipSync ? null : s.Last }.OfType<string>().All(id => _board.Frame(id) is not { } f || !Stale(f))) is { } shot
            ? FilmPiece.Of(shot) : null;
    }

    /// <summary>Makes every piece not made or out of date, one after another, each looked at, then joins the film.</summary>
    [RelayCommand]
    private async Task MakeMissing()
    {
        if (IsWorking)
            return;
        if (_board.Shots.Any(s => s.Prompt.Trim().Length == 0))
        {
            Status = "Some shots don't say what happens in them yet: pick them and say it.";
            return;
        }
        // Every shot starts on a picture a picture model makes.
        if (_board.AddFirstFrames() is { Count: > 0 } added)
        {
            _dirty = true;
            SaveNow(force: true);
            Rebuild();
            Say(FilmTalk.Film, $"{(added.Count == 1 ? "A shot" : "Some shots")} had no first frame, so I gave {(added.Count == 1 ? "it one" : "each one")} " +
                               $"({string.Join(", ", added.Select(f => f.Id))}) from what happens in {(added.Count == 1 ? "it" : "them")}: a picture model makes what a shot starts on.");
        }
        if (Missing() is null && Joined())
        {
            Status = "Everything is made and joined.";
            return;
        }
        Say(FilmTalk.Film, Missing() is null ? "Everything is made: joining the film and looking at it…" : "Making what's missing, one piece after another…");
        await RunJobAsync(FilmTalk.Film, null, async (_, ct) =>
        {
            var made = 0;
            var tried = new HashSet<FilmPiece>();
            while (Missing() is { } next && tried.Add(next))
            {
                Say(FilmTalk.Film, $"Making what's missing: {Lower(ItemOf(next)?.Title ?? next.Id)} now ({made} made so far). Pick it to watch it being made.", replace: true);
                await MakePieceAsync(next, next.Id, ct);
                made++;
            }
            Say(FilmTalk.Film, $"Made {made} piece{(made == 1 ? "" : "s")}." + (Missing() is { } left ? $" {Capital(Lower(ItemOf(left)?.Title ?? left.Id))} couldn't be made: pick it to see why." : " Joining the film…"), replace: true);
            if (Missing() is null)
            {
                await JoinAsync(ct);
                await LookAtFilmAsync(ct);
            }
            SaveTalk();
        });
    }

    /// <summary>Whether the film is joined from the clips and soundtrack there are now.</summary>
    private bool Joined()
    {
        if (!HasFilm)
            return false;
        var newest = _board.Shots.Select(s => _board.ShotVideo(s)).Append(_board.SoundtrackFile).Where(p => p is not null && File.Exists(p))
            .Select(p => File.GetLastWriteTimeUtc(p!)).DefaultIfEmpty(DateTime.MinValue).Max();
        return File.GetLastWriteTimeUtc(_board.FilmFile) >= newest;
    }

    /// <summary>
    /// Fresh looks at the joined film: a frame from each shot side by side, and each cut, the frame
    /// before beside the frame after. They see what no look at one shot can: a fix that moved shot 2
    /// of the fox film to an open clearing with the moon on the other side passed all of shot 2's own
    /// looks. The shots they name are fixed and made again and the film joined again, once; then
    /// looked at again, to say what is left.
    /// </summary>
    private async Task LookAtFilmAsync(CancellationToken ct, bool fix = true)
    {
        if (_ask is not { } ask || _previews.Decoder is not { } decoder || !HasFilm)
            return;
        Say(FilmTalk.Film, fix ? "Looking at the whole film…" : "Looking at the whole film again…");
        var look = await Task.Run(() => FilmCheck.Film(_board, _board.FilmFile, decoder), ct);
        var verdict = await FilmCheck.JudgeFilmAsync(_board, look,
            (pictures, question, c) => ask("You look at pictures carefully and say plainly what you see, faults included. You don't praise what isn't good.", question, pictures, true, c), ct);
        if (verdict is not { Reason: { } reason })
        {
            Say(FilmTalk.Film, verdict is null ? "The whole film couldn't be looked at."
                : "Looked at the whole film: the same people and places all through and across every cut, and each shot shows what it should.", replace: true);
            return;
        }
        var named = verdict.Redo.Select(_board.Shot).OfType<FilmShot>().Select(FilmPiece.Of).ToList();
        if (named.Count == 0 || !fix)
        {
            Say(FilmTalk.Film, $"The look at the whole film found: {reason}." + (fix ? " Say what to change." : $" It still isn't right after fixing: say what to change, or pick a shot and say it."), replace: true);
            return;
        }
        Say(FilmTalk.Film, $"The look at the whole film found: {reason}. Fixing {string.Join(", ", named.Select(p => Lower(ItemOf(p)?.Title ?? p.Id)))}…", replace: true);
        foreach (var piece in named)
        {
            var at = _board.Shots.FindIndex(s => s.Id == piece.Id);
            var cut = look.Cuts?.FirstOrDefault(c => c.At == at) ?? look.Cuts?.FirstOrDefault(c => c.At == at + 1);
            await FixAsync(piece, piece.Id, $"(seen with the other shots of the film) {reason}", ct, 0, cut is null ? null : (cut.Picture,
                "The picture shows a cut of the film: on top the last frame of the shot before, below it the first frame of the shot after. " +
                $"Make {piece.Id}'s frames agree with the shots next to it unless the story moves on: the same kind of place, the moon, sun or light on the same side and the same size, " +
                "the same time and weather, the same people looking the same, framed as the story needs; and keep its last frame made from its first, so the shot stays put."));
        }
        if (Missing() is null)
        {
            await JoinAsync(ct);
            await LookAtFilmAsync(ct, fix: false);
        }
    }

    /// <summary>What is installed for the kinds of job a piece can be, for the model to name.</summary>
    private async Task<IReadOnlyList<FilmOption>> InstalledAsync(FilmPiece piece, Action<string> progress, CancellationToken ct)
    {
        try
        {
            _maker.Use();
            var api = await _hub.ConnectAsync(null, [], progress, ct);
            var jobs = piece.Kind switch
            {
                FilmPieceKind.Frame => new[] { FilmJob.Frame, FilmJob.Edit },
                FilmPieceKind.Song => [FilmJob.Song],
                _ => [FilmJob.Shot, FilmJob.ShotFromFirst, FilmJob.ShotFromText, FilmJob.LipSync],
            };
            var all = new List<FilmOption>();
            foreach (var job in jobs)
                all.AddRange((await FilmTemplates.OptionsAsync(_hub, api, job, ct)).Where(o => all.All(a => a.Name != o.Name)));
            return all;
        }
        catch (Exception ex) when (ex is ComfyApiException or IOException or FilmException)
        {
            return [];
        }
    }

    /// <summary>The piece as it is, for the model to see: a frame's picture, a shot's sheet (or its first frame).</summary>
    private byte[]? PictureOf(FilmPiece? piece)
    {
        try
        {
            switch (piece?.Kind)
            {
                case FilmPieceKind.Frame when _board.Frame(piece.Id) is { } f && File.Exists(_board.FramePicture(f)):
                    return FilmCheck.Picture(_board.FramePicture(f), 768);
                case FilmPieceKind.Shot when _board.Shot(piece.Id) is { } s:
                    if (File.Exists(_board.ShotVideo(s)) && _previews.Decoder is { } decoder)
                        return FilmCheck.Shot(_board, s, _board.ShotVideo(s), decoder).Sheet;
                    return _board.Frame(s.First) is { } first && File.Exists(_board.FramePicture(first)) ? FilmCheck.Picture(_board.FramePicture(first), 768) : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException or InvalidOperationException) { }
        return null;
    }

    private IFilmPiece? ItemOf(FilmPiece piece) => piece.Kind switch
    {
        FilmPieceKind.Frame => _frames.GetValueOrDefault(piece.Id),
        FilmPieceKind.Shot => Shots.FirstOrDefault(s => s.Id == piece.Id),
        _ => Sound,
    };

    private static string Short(string template) =>
        template.StartsWith(FilmTemplates.CheckpointPrefix, StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(template) : Path.GetFileName(template);

    // ---- jobs ------------------------------------------------------------------------------------------------

    /// <summary>What the bottom line says: what the window is doing, what went wrong.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private int _working;

    public bool IsWorking => Working > 0;

    private CancellationTokenSource? _job;

    /// <summary>The talk the running job writes to, so the panel shows it working.</summary>
    private string? _jobKey;

    /// <summary>Runs one job at a time: what BlackBox does for a request, or a piece being made; what goes wrong is said in the talk.</summary>
    private async Task RunJobAsync(string key, IFilmPiece? item, Func<Action<string>, CancellationToken, Task> job)
    {
        SaveNow(force: true);
        using var cts = new CancellationTokenSource();
        _job = cts;
        _jobKey = key;
        Working++;
        RefreshTalk();
        void Progress(string s) => Dispatcher.UIThread.Post(() =>
        {
            Status = s;
            if (key == TalkKey && Talk.Count > 0 && !Talk[^1].Mine)
                Talk[^1] = Talk[^1] with { Text = s, Working = true };
        });
        try
        {
            await job(Progress, cts.Token);
            Status = "";
        }
        catch (OperationCanceledException)
        {
            Say(key, "Stopped.", replace: true);
        }
        catch (Exception ex) when (ex is FilmException or FilmStillRunningException or ComfyApiException or ComfyWorkflowException or IOException or UnauthorizedAccessException)
        {
            Say(key, ex.Message, replace: true);
            if (item is not null)
                item.Report(ex.Message, true);
        }
        finally
        {
            Working--;
            _job = null;
            _jobKey = null;
            SaveTalk();
            RefreshTalk();
            RefreshStates();
        }
    }

    [RelayCommand]
    private void Stop() => _job?.Cancel();

    public bool HasFilm => File.Exists(_board.FilmFile);

    /// <summary>Joins the shots made so far, with the soundtrack, into the film, and shows it.</summary>
    [RelayCommand]
    private async Task Join()
    {
        if (IsWorking)
            return;
        await RunJobAsync(FilmTalk.Film, null, (_, ct) => JoinAsync(ct));
    }

    private async Task JoinAsync(CancellationToken ct)
    {
        Say(FilmTalk.Film, "Joining the clips…");
        var made = await FilmMontage.JoinAsync(_board, _previews.Decoder?.Ffmpeg, _previews.Decoder, s => Dispatcher.UIThread.Post(() => Status = s), ct);
        Say(FilmTalk.Film, $"Joined the film ({Path.GetFileName(made.File)}): it plays in the viewer, and it is in the film's folder." +
                           (made.Notes.Count > 0 ? " " + string.Join(" ", made.Notes) : ""), replace: true);
        SaveTalk();
        _filmView?.Player?.Dispose();
        _filmView = null;
        OnPropertyChanged(nameof(HasFilm));
        Select(null);
    }

    /// <summary>
    /// Hands the storyboard to the agent in the chat: it makes what isn't made yet, checks each shot
    /// against what it should show, fixes what is wrong, joins the film and checks it.
    /// </summary>
    [RelayCommand]
    private void MakeFilm()
    {
        SaveNow(force: true);
        _sendToChat($"Make the film from my storyboard: {_board.FilePath}\n" +
                    "Make what isn't made yet or is out of date (frames, the song, each shot's clip), check every shot and fix the ones that don't show what they should, " +
                    "then join them and check the whole film.");
        Status = "BlackBox is making the film in the chat; what it makes shows here as it goes.";
    }
}
