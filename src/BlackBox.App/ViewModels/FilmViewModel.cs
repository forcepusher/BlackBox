using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Comfy;
using BlackBox.Core.Film;
using BlackBox.Core.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BlackBox.App.ViewModels;

/// <summary>
/// The Filmmaking window: a film's storyboard to plan and make. Each shot goes from a first frame to
/// a last one in so many seconds; frames are made from a prompt with a fixed seed (or taken from the
/// user's own pictures), from earlier frames to keep people and places the same; shots, the song and
/// the joined film are made here or by the agent, which also checks each shot and fixes what is
/// wrong ("Make the film"). Everything is kept in the film folder's storyboard.json as it is typed,
/// and what the agent changes there shows here.
/// </summary>
public sealed partial class FilmViewModel : ObservableObject, IDisposable
{
    private readonly PreviewServices _previews;
    private readonly Action<string> _sendToChat;
    private readonly FilmMaker _maker;
    private Storyboard _board = new();
    private readonly Dictionary<string, FrameItem> _frames = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _refreshTimer;
    private FileSystemWatcher? _watcher;
    private DateTime _ownWrite;
    private bool _loading;

    public FilmViewModel(PreviewServices previews, ComfyHub hub, Action<string> sendToChat, string? folder)
    {
        _previews = previews;
        _sendToChat = sendToChat;
        _maker = new FilmMaker(hub, previews.Decoder);
        _saveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => SaveNow());
        _refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, (_, _) =>
        {
            _refreshTimer!.Stop();
            OnFolderChanged();
        });
        RefreshFilms();
        var open = folder ?? Films.FirstOrDefault()?.Folder;
        if (open is not null && File.Exists(Path.Combine(open, Storyboard.FileName)))
            Open(open);
        else
            NewFilm();
    }

    // ---- the film --------------------------------------------------------------------------------------

    public ObservableCollection<FilmChoice> Films { get; } = [];

    [ObservableProperty]
    private FilmChoice? _selectedFilm;

    partial void OnSelectedFilmChanged(FilmChoice? value)
    {
        if (value is not null && !_loading && !string.Equals(value.Folder, _board.Folder, StringComparison.OrdinalIgnoreCase))
            Open(value.Folder);
    }

    private void RefreshFilms()
    {
        var films = Storyboard.Recent().ToList();
        if (_board.Folder.Length > 0 && !films.Contains(_board.Folder, StringComparer.OrdinalIgnoreCase))
            films.Insert(0, _board.Folder);
        _loading = true;
        Films.Clear();
        foreach (var f in films)
            Films.Add(new FilmChoice(f, TitleOf(f)));
        SelectedFilm = Films.FirstOrDefault(f => string.Equals(f.Folder, _board.Folder, StringComparison.OrdinalIgnoreCase));
        _loading = false;

        static string TitleOf(string folder)
        {
            try { return Storyboard.Load(folder).Title is { Length: > 0 } t ? t : Path.GetFileName(folder); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException) { return Path.GetFileName(folder); }
        }
    }

    public string Folder => _board.Folder;

    /// <summary>Opens the film in a folder (or the storyboard.json given).</summary>
    public void Open(string path)
    {
        SaveNow();
        try
        {
            _board = Storyboard.Load(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException)
        {
            Status = $"That film can't be opened: {ex.Message}";
            return;
        }
        Watch();
        Rebuild();
        RefreshFilms();
        OnPropertyChanged(nameof(Folder));
    }

    /// <summary>
    /// A new film in Videos\BlackBox films, with one shot of two frames to start from. Its folder is
    /// made with the first change: opening the window to look leaves nothing behind.
    /// </summary>
    [RelayCommand]
    private void NewFilm()
    {
        SaveNow();
        var root = Storyboard.DefaultRoot;
        var name = "New film";
        for (var n = 2; Directory.Exists(Path.Combine(root, name)); n++)
            name = $"New film {n}";
        _board = Storyboard.New(Path.Combine(root, name), name);
        _ownWrite = default;
        _watcher?.Dispose();
        _watcher = null;
        Rebuild();
        RefreshFilms();
        OnPropertyChanged(nameof(Folder));
    }

    [RelayCommand]
    private void ShowFolder()
    {
        if (Directory.Exists(_board.Folder))
            _ = Task.Run(() =>
            {
                try { Process.Start(new ProcessStartInfo(_board.Folder) { UseShellExecute = true })?.Dispose(); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            });
    }

    public string Title
    {
        get => _board.Title;
        set
        {
            if (_board.Title == value) return;
            _board.Title = value;
            OnPropertyChanged();
            Changed(this);
        }
    }

    public string Story
    {
        get => _board.Story;
        set
        {
            if (_board.Story == value) return;
            _board.Story = value;
            OnPropertyChanged();
            Changed(this);
        }
    }

    public string Style
    {
        get => _board.Style;
        set
        {
            if (_board.Style == value) return;
            _board.Style = value;
            OnPropertyChanged();
            Changed(this);
        }
    }

    public decimal? FrameWidth
    {
        get => _board.Width;
        set
        {
            if (value is not { } v) return;
            _board.Width = Math.Clamp((int)v / 2 * 2, 64, 4096);
            OnPropertyChanged();
            Changed(this);
        }
    }

    public decimal? FrameHeight
    {
        get => _board.Height;
        set
        {
            if (value is not { } v) return;
            _board.Height = Math.Clamp((int)v / 2 * 2, 64, 4096);
            OnPropertyChanged();
            Changed(this);
        }
    }

    public decimal? Fps
    {
        get => _board.Fps;
        set
        {
            if (value is not { } v) return;
            _board.Fps = Math.Clamp((int)v, 1, 120);
            OnPropertyChanged();
            Changed(this);
        }
    }

    /// <summary>How loud the soundtrack is, 0 to 100.</summary>
    public double SoundVolume
    {
        get => _board.SoundVolume * 100;
        set
        {
            _board.SoundVolume = Math.Clamp(value / 100, 0, 2);
            OnPropertyChanged();
            Changed(this);
        }
    }

    /// <summary>How loud the shots' own sound is under the soundtrack, 0 to 100.</summary>
    public double ShotVolume
    {
        get => (_board.ShotVolume ?? (_board.Sound.Source == FilmSound.None ? 1 : 0.3)) * 100;
        set
        {
            _board.ShotVolume = Math.Clamp(value / 100, 0, 2);
            OnPropertyChanged();
            Changed(this);
        }
    }

    /// <summary>How long the film is, and in how many shots.</summary>
    public string LengthText
    {
        get
        {
            var (_, length) = _board.Timing();
            return $"{length:0.#} s in {_board.Shots.Count} shot{(_board.Shots.Count == 1 ? "" : "s")}";
        }
    }

    // ---- the storyboard -----------------------------------------------------------------------------------

    public ObservableCollection<ShotItem> Shots { get; } = [];

    /// <summary>Characters, places and things other frames are made from, and frames no shot uses.</summary>
    public ObservableCollection<FrameItem> References { get; } = [];

    public SoundItem Sound { get; private set; } = null!;

    /// <summary>What the details panel shows: a frame, a shot, the soundtrack or the joined film.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedFrame), nameof(SelectedShot), nameof(SelectedFilmFile), nameof(NothingSelected))]
    private object? _selected;

    public FrameItem? SelectedFrame => Selected as FrameItem;
    public ShotItem? SelectedShot => Selected as ShotItem;
    public JoinedFilm? SelectedFilmFile => Selected as JoinedFilm;
    public bool NothingSelected => Selected is null;

    public void Select(object? item)
    {
        if (Selected is FrameItem oldFrame) oldFrame.IsSelected = false;
        if (Selected is ShotItem oldShot) oldShot.IsSelected = false;
        if (item is FrameItem frame)
        {
            frame.IsSelected = true;
            frame.RefreshChoices(AllFrames().Where(f => f != frame));
            LoadPicture(frame);
        }
        if (item is ShotItem shot)
            shot.IsSelected = true;
        Selected = item;
    }

    /// <summary>The frames in the order the storyboard shows them: references, then each shot's.</summary>
    private IEnumerable<FrameItem> AllFrames() =>
        References.Concat(Shots.SelectMany(s => new[] { s.First, s.Last })).OfType<FrameItem>().Distinct();

    /// <summary>Builds the items from the storyboard, keeping what was selected.</summary>
    private void Rebuild()
    {
        var selected = Selected switch { FrameItem f => "f:" + f.Id, ShotItem s => "s:" + s.Id, _ => null };
        _loading = true;
        foreach (var shot in Shots)
            shot.Clip?.Dispose();
        Sound?.Player?.Dispose();
        _frames.Clear();
        Shots.Clear();
        References.Clear();
        foreach (var frame in _board.Frames)
            _frames[frame.Id] = new FrameItem(this, frame);
        for (var i = 0; i < _board.Shots.Count; i++)
        {
            var shot = _board.Shots[i];
            Shots.Add(new ShotItem(this, shot)
            {
                Index = i,
                First = shot.First is { } first ? _frames.GetValueOrDefault(first) : null,
                Last = shot.Last is { } last ? _frames.GetValueOrDefault(last) : null,
            });
        }
        var inShots = _board.Shots.SelectMany(s => new[] { s.First, s.Last }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in _board.Frames.Where(f => f.Reference || !inShots.Contains(f.Id)))
            References.Add(_frames[frame.Id]);
        Sound = new SoundItem(this, _board.Sound);
        _loading = false;
        Relabel();
        RefreshStates();
        foreach (var p in new[] { nameof(Title), nameof(Story), nameof(Style), nameof(FrameWidth), nameof(FrameHeight), nameof(Fps),
                                  nameof(SoundVolume), nameof(ShotVolume), nameof(Sound), nameof(LengthText), nameof(HasFilm) })
            OnPropertyChanged(p);
        Select(selected switch
        {
            ['f', ':', .. var id] => _frames.GetValueOrDefault(id),
            ['s', ':', .. var id] => Shots.FirstOrDefault(s => s.Id == id),
            _ => Shots.FirstOrDefault(),
        });
    }

    /// <summary>Names each frame by where it is used: "Start of shot 2", "End of shot 1, start of shot 2".</summary>
    public void Relabel()
    {
        foreach (var (id, item) in _frames)
        {
            var uses = new List<string>();
            for (var i = 0; i < _board.Shots.Count; i++)
            {
                if (string.Equals(_board.Shots[i].First, id, StringComparison.OrdinalIgnoreCase)) uses.Add($"start of shot {i + 1}");
                if (string.Equals(_board.Shots[i].Last, id, StringComparison.OrdinalIgnoreCase)) uses.Add($"end of shot {i + 1}");
            }
            var where = string.Join(", ", uses.OrderBy(u => u.StartsWith("end", StringComparison.Ordinal) ? 0 : 1));
            item.Label = item.Frame.Name is { Length: > 0 } name ? name + (where.Length > 0 ? $" ({where})" : "")
                : where.Length > 0 ? char.ToUpperInvariant(where[0]) + where[1..] : "A picture no shot uses";
        }
    }

    /// <summary>Something was changed in the window: saved a moment later, and what it changes shown.</summary>
    public void Changed(object source)
    {
        if (_loading)
            return;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
        OnPropertyChanged(nameof(LengthText));
        // What the change makes out of date shows once it is saved (the stamps are computed from the files).
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Whether the storyboard has changes not saved yet.</summary>
    private bool _dirty;

    /// <summary>Saves what changed (with <paramref name="force"/>, saves anyway: before a job, which needs the film's folder).</summary>
    private void SaveNow(bool force = false)
    {
        _saveTimer.Stop();
        if (_board.Folder.Length == 0 || !_dirty && !force)
            return;
        try
        {
            _board.Save();
            _dirty = false;
            _ownWrite = File.GetLastWriteTimeUtc(_board.FilePath);
            if (_watcher is null)
                Watch();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"The storyboard couldn't be saved: {ex.Message}";
        }
    }

    // ---- shots and frames -----------------------------------------------------------------------------------

    [RelayCommand]
    private void AddShot()
    {
        SaveNow();
        var before = _board.Shots.LastOrDefault();
        // A new shot goes on from the last one's end when it has one, the usual way a scene goes on.
        string first;
        if (before?.Last is { } end)
            first = end;
        else
        {
            first = _board.NewId("f");
            _board.Frames.Add(new FilmFrame { Id = first, Seed = Storyboard.NewSeed() });
        }
        var last = _board.NewId("f");
        _board.Frames.Add(new FilmFrame { Id = last, Seed = Storyboard.NewSeed() });
        var shot = new FilmShot { Id = _board.NewId("s"), First = first, Last = last, Seconds = 5, Seed = Storyboard.NewSeed() };
        _board.Shots.Add(shot);
        SaveNow(force: true);
        Rebuild();
        Select(Shots.FirstOrDefault(s => s.Id == shot.Id));
    }

    [RelayCommand]
    private void AddReference()
    {
        SaveNow();
        var frame = new FilmFrame { Id = _board.NewId("f"), Reference = true, Name = $"Reference {References.Count(r => r.Frame.Reference) + 1}", Seed = Storyboard.NewSeed() };
        _board.Frames.Add(frame);
        SaveNow(force: true);
        Rebuild();
        Select(_frames.GetValueOrDefault(frame.Id));
    }

    public void DeleteReference(FrameItem item)
    {
        SaveNow();
        _board.Frames.Remove(item.Frame);
        foreach (var f in _board.Frames)
            f.From.RemoveAll(id => id.Equals(item.Id, StringComparison.OrdinalIgnoreCase));
        SaveNow(force: true);
        Rebuild();
    }

    public void DeleteShot(ShotItem item)
    {
        SaveNow();
        var at = _board.Shots.IndexOf(item.Shot);
        _board.Shots.Remove(item.Shot);
        // Its frames go with it, unless another shot starts or ends on them, or others are made from them.
        foreach (var id in new[] { item.Shot.First, item.Shot.Last }.OfType<string>())
            if (!Used(id))
                _board.Frames.RemoveAll(f => f.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && !f.Reference);
        SaveNow(force: true);
        Rebuild();
        Select(Shots.ElementAtOrDefault(Math.Min(at, Shots.Count - 1)));
    }

    private bool Used(string id) =>
        _board.Shots.Any(s => string.Equals(s.First, id, StringComparison.OrdinalIgnoreCase) || string.Equals(s.Last, id, StringComparison.OrdinalIgnoreCase)) ||
        _board.Frames.Any(f => f.From.Contains(id, StringComparer.OrdinalIgnoreCase));

    public void Move(ShotItem item, int by)
    {
        var at = _board.Shots.IndexOf(item.Shot);
        var to = at + by;
        if (at < 0 || to < 0 || to >= _board.Shots.Count)
            return;
        SaveNow();
        _board.Shots.RemoveAt(at);
        _board.Shots.Insert(to, item.Shot);
        SaveNow(force: true);
        Rebuild();
        Select(Shots.FirstOrDefault(s => s.Id == item.Id));
    }

    public bool ContinuesFromBefore(ShotItem item)
    {
        var at = _board.Shots.IndexOf(item.Shot);
        return at > 0 && item.Shot.First is { } first && string.Equals(_board.Shots[at - 1].Last, first, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Starts the shot on the frame the one before ends on (the camera goes on), or on a frame of its
    /// own (a cut to another scene or angle). The one before is given a last frame if it has none.
    /// </summary>
    public void SetContinues(ShotItem item, bool continues)
    {
        var at = _board.Shots.IndexOf(item.Shot);
        if (at <= 0 || continues == ContinuesFromBefore(item))
            return;
        SaveNow();
        var old = item.Shot.First;
        if (continues)
        {
            var before = _board.Shots[at - 1];
            if (before.Last is null)
            {
                before.Last = _board.NewId("f");
                _board.Frames.Add(new FilmFrame { Id = before.Last, Seed = Storyboard.NewSeed() });
            }
            item.Shot.First = before.Last;
            item.Shot.Transition = FilmShot.Cut;
        }
        else
        {
            item.Shot.First = _board.NewId("f");
            _board.Frames.Add(new FilmFrame { Id = item.Shot.First, Seed = Storyboard.NewSeed() });
        }
        if (old is not null && !Used(old))
            _board.Frames.RemoveAll(f => f.Id.Equals(old, StringComparison.OrdinalIgnoreCase) && !f.Reference);
        SaveNow(force: true);
        Rebuild();
    }

    /// <summary>Gives the shot a frame to end on, or lets it go where its prompt takes it.</summary>
    public void SetLastFrame(ShotItem item, bool has)
    {
        if (has == (item.Shot.Last is not null))
            return;
        SaveNow();
        if (has)
        {
            item.Shot.Last = _board.NewId("f");
            _board.Frames.Add(new FilmFrame { Id = item.Shot.Last, Seed = Storyboard.NewSeed() });
        }
        else
        {
            var old = item.Shot.Last!;
            item.Shot.Last = null;
            // The next shot going on from it now starts on a frame of its own.
            if (!Used(old))
                _board.Frames.RemoveAll(f => f.Id.Equals(old, StringComparison.OrdinalIgnoreCase) && !f.Reference);
        }
        SaveNow(force: true);
        Rebuild();
    }

    // ---- making -------------------------------------------------------------------------------------------

    /// <summary>What the bottom line says: a job's progress, what was made, what went wrong.</summary>
    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorking))]
    private int _working;

    public bool IsWorking => Working > 0;

    private readonly List<CancellationTokenSource> _jobs = [];

    private async Task<FilmMade?> RunAsync(Func<Action<string>, CancellationToken, Task<FilmMade>> job, Action<string, bool> report, CancellationToken ct)
    {
        SaveNow(force: true);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_jobs) _jobs.Add(both);
        Working++;
        try
        {
            var made = await Task.Run(() => job(s => report(s, false), both.Token), both.Token);
            report($"Made in {FilePreview.Clock(made.Took)}" + (made.Template is { } t ? $" with {t}" : "") + (made.Notes.Count > 0 ? ". " + string.Join(" ", made.Notes) : ""), false);
            return made;
        }
        catch (OperationCanceledException)
        {
            report("Stopped.", false);
        }
        catch (Exception ex) when (ex is FilmException or FilmStillRunningException or ComfyApiException or ComfyWorkflowException or IOException or UnauthorizedAccessException)
        {
            report(ex.Message, true);
        }
        finally
        {
            lock (_jobs) _jobs.Remove(both);
            Working--;
        }
        return null;
    }

    public async Task RunFrameAsync(FrameItem item, CancellationToken ct)
    {
        item.IsWorking = true;
        try
        {
            await RunAsync((progress, token) => _maker.MakeFrameAsync(_board, item.Frame, progress, CancellationToken.None, token), (s, e) => item.Report(s, e), ct);
        }
        finally
        {
            item.IsWorking = false;
            RefreshStates();
        }
    }

    public async Task RunShotAsync(ShotItem item, CancellationToken ct)
    {
        item.IsWorking = true;
        try
        {
            await RunAsync((progress, token) => _maker.MakeShotAsync(_board, item.Shot, progress, CancellationToken.None, token), (s, e) => item.Report(s, e), ct);
        }
        finally
        {
            item.IsWorking = false;
            RefreshStates();
        }
    }

    public async Task RunSongAsync(SoundItem item, CancellationToken ct)
    {
        item.IsWorking = true;
        try
        {
            await RunAsync((progress, token) => _maker.MakeSongAsync(_board, progress, CancellationToken.None, token), (s, e) => item.Report(s, e), ct);
        }
        finally
        {
            item.IsWorking = false;
            RefreshStates();
        }
    }

    public bool HasFilm => File.Exists(_board.FilmFile);

    /// <summary>Joins the shots made so far, with the soundtrack, into the film.</summary>
    [RelayCommand]
    private async Task Join()
    {
        var made = await RunAsync((progress, token) => FilmMontage.JoinAsync(_board, _previews.Decoder?.Ffmpeg, _previews.Decoder, progress, token),
            (s, error) => Dispatcher.UIThread.Post(() => Status = s), CancellationToken.None);
        if (made is not null)
        {
            Status = $"Joined: {made.File}" + (made.Notes.Count > 0 ? " " + string.Join(" ", made.Notes) : "");
            OnPropertyChanged(nameof(HasFilm));
            ShowFilm();
        }
    }

    [RelayCommand]
    private void ShowFilm()
    {
        if (!File.Exists(_board.FilmFile))
            return;
        var film = new JoinedFilm(_board.FilmFile) { Player = FilePreview.For(_board.FilmFile, _previews) };
        if (Selected is JoinedFilm old)
            old.Player?.Dispose();
        Select(film);
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

    // ---- what is made --------------------------------------------------------------------------------------

    /// <summary>Thumbnails, players and each item's state, from the files in the film's folder.</summary>
    public void RefreshStates()
    {
        foreach (var frame in _frames.Values)
        {
            var path = _board.FramePicture(frame.Frame);
            var made = File.Exists(path);
            frame.IsMade = made;
            if (!frame.IsWorking && !frame.IsError)
                frame.State = !made ? "Not made yet." : FilmMaker.IsCurrent(_board, frame.Frame) ? "Made." : "Changed since it was made: make it again.";
            LoadThumbnail(frame, path);
            if (frame == SelectedFrame)
                LoadPicture(frame);
        }
        foreach (var shot in Shots)
        {
            var path = _board.ShotVideo(shot.Shot);
            var made = File.Exists(path);
            shot.IsMade = made;
            if (!shot.IsWorking && !shot.IsError)
                shot.State = !made ? "No clip yet." : FilmMaker.IsCurrent(_board, shot.Shot) ? "Clip made." : "Changed since the clip was made: make it again.";
            var stamp = made ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (made && (shot.Clip is null || shot.Clip.Path != path || _clipStamps.GetValueOrDefault(shot.Id) != stamp))
            {
                shot.Clip?.Dispose();
                shot.Clip = FilePreview.For(path, _previews);
                _clipStamps[shot.Id] = stamp;
            }
            else if (!made && shot.Clip is not null)
            {
                shot.Clip.Dispose();
                shot.Clip = null;
            }
            shot.Refresh();
        }
        var song = _board.Sound.Source switch { FilmSound.Song => _board.SongFile, FilmSound.FromFile => _board.Sound.File, _ => null };
        var songMade = song is not null && File.Exists(song);
        Sound.IsMade = _board.Sound.Source == FilmSound.Song && songMade;
        if (!Sound.IsWorking && !Sound.IsError)
            Sound.State = _board.Sound.Source switch
            {
                FilmSound.Song => !songMade ? "Not made yet." : FilmMaker.SongIsCurrent(_board) ? "Made." : "Changed since it was made (or the film's length did): make it again.",
                FilmSound.FromFile => song is null ? "Pick a sound file." : songMade ? "" : "The file isn't there.",
                _ => "",
            };
        var songStamp = songMade ? File.GetLastWriteTimeUtc(song!) : DateTime.MinValue;
        if (songMade && (Sound.Player is null || Sound.Player.Path != song || _clipStamps.GetValueOrDefault("\nsong") != songStamp))
        {
            Sound.Player?.Dispose();
            Sound.Player = FilePreview.For(song!, _previews);
            _clipStamps["\nsong"] = songStamp;
        }
        else if (!songMade && Sound.Player is not null)
        {
            Sound.Player.Dispose();
            Sound.Player = null;
        }
        OnPropertyChanged(nameof(HasFilm));
        OnPropertyChanged(nameof(ShotVolume));
    }

    private readonly Dictionary<string, DateTime> _clipStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FrameItem, DateTime> _thumbStamps = [];

    private void LoadThumbnail(FrameItem frame, string path)
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        if (_thumbStamps.TryGetValue(frame, out var had) && had == stamp)
            return;
        _thumbStamps[frame] = stamp;
        if (stamp == DateTime.MinValue)
        {
            frame.Thumbnail = null;
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(stream, 320, BitmapInterpolationMode.HighQuality);
                Dispatcher.UIThread.Post(() =>
                {
                    var old = frame.Thumbnail;
                    frame.Thumbnail = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        });
    }

    /// <summary>The selected frame's picture, bigger than its thumbnail.</summary>
    private void LoadPicture(FrameItem frame)
    {
        var path = _board.FramePicture(frame.Frame);
        if (!File.Exists(path))
        {
            frame.Picture = null;
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                var bitmap = Bitmap.DecodeToWidth(stream, 720, BitmapInterpolationMode.HighQuality);
                Dispatcher.UIThread.Post(() =>
                {
                    var old = frame.Picture;
                    frame.Picture = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        });
    }

    // ---- the folder, as the agent and the jobs change it ----------------------------------------------------

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(_board.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(() =>
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            });
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Something in the film's folder changed: the agent changed the storyboard (it is read again),
    /// or a frame, clip or song was made (it is shown).
    /// </summary>
    private void OnFolderChanged()
    {
        try
        {
            var written = File.GetLastWriteTimeUtc(_board.FilePath);
            if (written != _ownWrite && !_saveTimer.IsEnabled && File.Exists(_board.FilePath))
            {
                var fresh = Storyboard.Load(_board.Folder);
                if (fresh.ToJson() != _board.ToJson())
                {
                    _board = fresh;
                    _ownWrite = written;
                    Rebuild();
                    return;
                }
                _ownWrite = written;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException) { }
        RefreshStates();
    }

    public void Dispose()
    {
        SaveNow();
        _watcher?.Dispose();
        lock (_jobs)
            foreach (var job in _jobs)
                job.Cancel();
        foreach (var shot in Shots)
            shot.Clip?.Dispose();
        Sound?.Player?.Dispose();
        if (Selected is JoinedFilm film)
            film.Player?.Dispose();
        _ = _maker.ReleaseAsync();
    }
}
