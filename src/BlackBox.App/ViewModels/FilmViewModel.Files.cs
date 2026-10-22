using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Film;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- saving --------------------------------------------------------------------------------------------

    /// <summary>Something was changed in the window: saved a moment later, and what it changes shown.</summary>
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

    // ---- what is made --------------------------------------------------------------------------------------


    private string StateOf(IFilmPiece item, bool made, bool current)
    {
        if (!made)
            return item is ShotItem s && s.Shot.Prompt.Trim().Length == 0 ? "Say what happens in it" : "Not made yet";
        if (!current)
            return "Changed: make it again";
        return FilmTakes.LookIn(item.Piece.Workflow(_board)) is { } look
            ? look.Verdict == "redo" ? "Made · the look found a problem" : "Made · looked at"
            : "Made";
    }

    /// <summary>Thumbnails, clips, strips and each piece's state, from the files in the film's folder.</summary>
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
            if (frame == Selected || Selected is ShotItem { First: var first } && first == frame)
                LoadPicture(frame);
        }
        foreach (var shot in Shots)
        {
            var path = _board.ShotVideo(shot.Shot);
            var made = File.Exists(path);
            shot.IsMade = made;
            if (!shot.IsWorking)
                (shot.State, shot.IsError) = (StateOf(shot, made, FilmMaker.IsCurrent(_board, shot.Shot)), false);
            var stamp = made ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (made && (shot.Clip is null || shot.Clip.Path != path || _clipStamps.GetValueOrDefault(shot.Id) != stamp))
            {
                shot.Clip?.Dispose();
                shot.Clip = FilePreview.For(path, _previews);
                _clipStamps[shot.Id] = stamp;
                LoadStrip(shot, path, stamp);
            }
            else if (!made && shot.Clip is not null)
            {
                shot.Clip.Dispose();
                shot.Clip = null;
                shot.Strip = null;
            }
        }
        var song = _board.Sound.Source switch { FilmSound.Song => _board.SongFile, FilmSound.FromFile => _board.Sound.File, _ => null };
        var songMade = song is not null && File.Exists(song);
        Sound.IsMade = songMade;
        var heard = songMade && _songScore.File == song && _songScore.Stamp == File.GetLastWriteTimeUtc(song!) && _songScore.Score is { } s ? " · " + s.Describe() : "";
        if (!Sound.IsWorking)
            Sound.State = _board.Sound.Source switch
            {
                FilmSound.Song => !songMade ? "Not made yet" : FilmMaker.SongIsCurrent(_board) ? "Made" + heard : "Changed: make it again",
                FilmSound.FromFile => song is null ? "Pick a sound file" : songMade ? "Your file" : "The file isn't there",
                _ => "No soundtrack",
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
        if (_filmView is not null && (!HasFilm || _filmStamp != File.GetLastWriteTimeUtc(_board.FilmFile)))
        {
            _filmView.Player?.Dispose();
            _filmView = null;
        }
        _filmStamp = HasFilm ? File.GetLastWriteTimeUtc(_board.FilmFile) : default;
        OnPropertyChanged(nameof(HasFilm));
        OnPropertyChanged(nameof(ShotVolume));
        OnPropertyChanged(nameof(LengthText));
        OnPropertyChanged(nameof(Viewer));
        RefreshSound();
        Relayout();
    }

    private DateTime _filmStamp;

    /// <summary>How the song made last sounded to the judge, while it is the song in the folder.</summary>
    private (string? File, DateTime Stamp, SongScore? Score) _songScore;

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A title in a sentence: "shot 3", "the song", "start of shot 2"; a name ("Marco") as it is.</summary>
    private static string Lower(string s) =>
        new[] { "Shot ", "The ", "Start of", "End of", "Frame ", "Reference ", "A " }.Any(p => s.StartsWith(p, StringComparison.Ordinal)) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

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

    /// <summary>A frame's picture, bigger than its thumbnail, for the viewer.</summary>
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
                var bitmap = Bitmap.DecodeToWidth(stream, 1280, BitmapInterpolationMode.HighQuality);
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
                if (_clipStamps.GetValueOrDefault(shot.Id) != stamp)
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
            if (written != _ownWrite && !_saveTimer.IsEnabled && !IsWorking && File.Exists(_board.FilePath))
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
        StopPlaying();
        SaveNow();
        SaveTalk();
        _watcher?.Dispose();
        _job?.Cancel();
        foreach (var shot in Shots)
        {
            shot.Clip?.Dispose();
            shot.Strip?.Dispose();
        }
        Sound?.Player?.Dispose();
        _filmView?.Player?.Dispose();
        foreach (var take in Takes)
            take.Thumbnail?.Dispose();
        _ = _maker.ReleaseAsync();
    }
}
