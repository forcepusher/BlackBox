using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Film;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- saving --------------------------------------------------------------------------------------------

    /// <summary>Something was changed in the panel: saved a moment later, and what it changes shown.</summary>
    public void Changed()
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

    /// <summary>The storyboard as this panel last read it from its file or wrote it there; null before it has a file.</summary>
    private string? _savedJson;

    private bool _disposing;

    /// <summary>Saves what changed (with <paramref name="force"/>, saves anyway: before a job, which needs the film's folder).</summary>
    private void SaveNow(bool force = false)
    {
        _saveTimer.Stop();
        // Nothing changed here, and the film is on the disk already: nothing to write.
        if (_board.Folder.Length == 0 || !_dirty && (!force || _savedJson is not null))
            return;
        try
        {
            // The chat changed the film while a change made here waited to be saved (a shot's
            // length dragged as the film tool rewrote a prompt): both stay. Saved whole, the
            // panel's copy wrote over the chat's change, and nothing showed it.
            var merged = false;
            // A new film whose folder another window's new film took since this one was named: a
            // folder of its own. Both picked "New film", and the second wrote over the first.
            if (_savedJson is null && Storyboard.IsFilm(_board.Folder))
            {
                var root = Path.GetDirectoryName(_board.Folder)!;
                var name = Path.GetFileName(_board.Folder);
                var n = 2;
                while (Directory.Exists(Path.Combine(root, $"{name} {n}")))
                    n++;
                _board.Folder = Path.Combine(root, $"{name} {n}");
                Directory.CreateDirectory(_board.Folder);
                _watcher?.Dispose();
                _watcher = null;
                OnPropertyChanged(nameof(Folder));
            }
            if (_savedJson is not null && Storyboard.IsFilm(_board.Folder))
            {
                Storyboard? theirs = null;
                try { theirs = Storyboard.Load(_board.Folder); }
                catch (FilmException) { /* not a film that can be read: this panel's is written */ }
                if (theirs is not null && theirs.ToJson() != _savedJson)
                {
                    var folder = _board.Folder;
                    _board = StoryboardMerge.Merge(Storyboard.Parse(_savedJson), _board, theirs);
                    _board.Folder = folder;
                    merged = true;
                }
            }
            _board.Save();
            _dirty = false;
            _savedJson = _board.ToJson();
            if (_watcher is null)
                Watch();
            if (merged && !_disposing)
                Rebuild();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The film couldn't be saved: {ex.Message}", ex);
        }
    }

    // ---- what is made --------------------------------------------------------------------------------------

    private string StateOf(IFilmPiece item, bool made, bool current)
    {
        if (!made)
            return item is ShotItem s && s.Shot.Prompt.Trim().Length == 0 ? "Say what happens in it" : "Not made yet";
        if (!current)
            return "Changed: make it again";
        return FilmTakes.LookIn(item.Piece.Meta(_board)) is { } look
            ? look.Verdict == "redo" ? "Made · the look found a problem" : "Made · looked at"
            : "Made";
    }

    /// <summary>Thumbnails, strips and each piece's state, from the files in the film's folder; the tree and the viewer follow.</summary>
    public void RefreshStates()
    {
        foreach (var frame in _frames.Values)
        {
            var path = _board.FramePicture(frame.Frame);
            var made = File.Exists(path);
            frame.IsMade = made;
            if (!frame.IsWorking)
                (frame.State, frame.IsError) = (StateOf(frame, made, FilmMaker.IsCurrent(_board, frame.Frame)), false);
            LoadThumbnail(frame, path);
        }
        foreach (var shot in Shots)
        {
            var path = _board.ShotVideo(shot.Shot);
            var made = File.Exists(path);
            shot.IsMade = made;
            if (!shot.IsWorking)
                (shot.State, shot.IsError) = (StateOf(shot, made, FilmMaker.IsCurrent(_board, shot.Shot)), false);
            var stamp = made ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (made && _clipStamps.GetValueOrDefault(shot.Id) != stamp)
            {
                _clipStamps[shot.Id] = stamp;
                LoadStrip(shot, path, stamp);
            }
            else if (!made && shot.Strip is not null)
            {
                _clipStamps.Remove(shot.Id);
                var strip = shot.Strip;
                shot.Strip = null;
                strip.Dispose();
            }
        }
        var song = _board.Sound.Source switch { FilmSound.Song => _board.SongFile, FilmSound.FromFile => _board.Sound.File, _ => null };
        var songMade = song is not null && File.Exists(song);
        Sound.IsMade = songMade;
        // How the song sounds to its judge, kept with it when it was made.
        var heard = songMade && FilmTakes.LookIn(_board.SongMeta) is { } judged ? " · " + judged.Said : "";
        if (!Sound.IsWorking)
            Sound.State = _board.Sound.Source switch
            {
                FilmSound.Song => !songMade ? "Not made yet" : FilmMaker.SongIsCurrent(_board) ? "Made" + heard : "Changed: make it again",
                FilmSound.FromFile => song is null ? "Pick a sound file" : songMade ? "Your file" : "The file isn't there",
                _ => "No soundtrack",
            };
        OnPropertyChanged(nameof(HasFilm));
        OnPropertyChanged(nameof(ShotVolume));
        OnPropertyChanged(nameof(LengthText));
        RefreshSound();
        Relayout();
        RefreshTree();
        RefreshViewer();
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A title in a sentence: "shot 3", "the song", "start of shot 2"; a name ("Marco") as it is.</summary>
    private static string Lower(string s) =>
        new[] { "Shot ", "The ", "Start of", "End of", "Frame ", "Reference ", "A " }.Any(p => s.StartsWith(p, StringComparison.Ordinal)) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private readonly Dictionary<string, DateTime> _clipStamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<FrameItem, DateTime> _thumbStamps = [];

    /// <summary>A frame's picture, small, for the timeline's blocks while its shot isn't made and for playing the storyboard.</summary>
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

    /// <summary>A shot's clip as a row of frames, for its block on the timeline.</summary>
    private void LoadStrip(ShotItem shot, string path, DateTime stamp)
    {
        if (_previews.Decoder is not { } decoder)
            return;
        _ = Task.Run(() =>
        {
            byte[]? jpeg = null;
            try { jpeg = FilmCheck.Strip(path, decoder, 64); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or FilmException) { }
            if (jpeg is null)
                return;
            using var stream = new MemoryStream(jpeg);
            var bitmap = new Bitmap(stream);
            Dispatcher.UIThread.Post(() =>
            {
                if (_clipStamps.GetValueOrDefault(shot.Id) != stamp || _disposing)
                {
                    bitmap.Dispose();
                    return;
                }
                var old = shot.Strip;
                shot.Strip = bitmap;
                old?.Dispose();
            });
        });
    }

    // ---- the folder, as the agent and the jobs change it ----------------------------------------------------

    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            var watcher = new FileSystemWatcher(_board.Folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size };
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
    /// Something in the film's folder changed: the agent changed the storyboard in the chat (it is
    /// read again, unless a change made here is still to be saved), a frame, clip or song was made
    /// or put away as a take, or the user put a picture in References: it shows.
    /// </summary>
    private void OnFolderChanged()
    {
        if (_disposing)
            return;
        try
        {
            // Not while a change made here waits to be saved: it is put together with theirs then.
            if (!_saveTimer.IsEnabled && !_dirty && Storyboard.IsFilm(_board.Folder))
            {
                var fresh = Storyboard.Load(_board.Folder);
                var json = fresh.ToJson();
                if (json != _savedJson)
                {
                    var title = _board.Title;
                    _savedJson = json;
                    _board = fresh;
                    Rebuild();
                    // Named in the chat: the list of films shows the new title.
                    if (_board.Title != title)
                        RefreshFilms();
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException) { }
        RefreshStates();
    }

    public void Dispose()
    {
        if (_disposing)
            return;
        _disposing = true;
        _chat.Attachments.CollectionChanged -= OnBoxChanged;
        _chatTimer.Stop();
        _refreshTimer.Stop(); // a refresh due after the panel closed made players nobody disposed
        StopPlaying();
        SaveNow();
        _watcher?.Dispose();
        foreach (var shot in Shots)
            shot.Strip?.Dispose();
        foreach (var frame in _frames.Values)
            frame.Thumbnail?.Dispose();
        DropViewer();
        DropThumbnails(keep: []);
    }
}
