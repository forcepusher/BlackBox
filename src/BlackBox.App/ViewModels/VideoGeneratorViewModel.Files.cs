using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Video;

namespace BlackBox.App.ViewModels;

public sealed partial class VideoGeneratorViewModel
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
        _videoTouched = true;
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Whether the storyboard has changes not saved yet.</summary>
    private bool _dirty;

    /// <summary>The storyboard as this panel last read it from its file or wrote it there; null before it has a file.</summary>
    private string? _savedJson;

    private bool _disposing;

    /// <summary>Saves what changed (with <paramref name="force"/>, saves anyway: before a job, which needs the video's folder).</summary>
    private void SaveNow(bool force = false)
    {
        _saveTimer.Stop();
        // Nothing changed here, and the video is on the disk already: nothing to write.
        if (_board.Folder.Length == 0 || !_dirty && (!force || _savedJson is not null))
            return;
        // The video's folder went (renamed or deleted elsewhere): written, it would come back.
        if (_savedJson is not null && !Directory.Exists(_board.Folder))
            return;
        try
        {
            // The chat changed the video while a change made here waited to be saved (a shot's
            // length dragged as the video tool rewrote a prompt): both stay. Saved whole, the
            // panel's copy wrote over the chat's change, and nothing showed it.
            var merged = false;
            // A new video whose folder another window's new video took since this one was named: a
            // folder of its own. Both picked "Untitled video", and the second wrote over the first.
            if (_savedJson is null && Storyboard.IsVideo(_board.Folder))
            {
                var root = Path.GetDirectoryName(_board.Folder)!;
                var name = Path.GetFileName(_board.Folder);
                var n = 2;
                while (Directory.Exists(Path.Combine(root, $"{name} {n}")))
                    n++;
                _board.Folder = Path.Combine(root, $"{name} {n}");
                Directory.CreateDirectory(_board.Folder);
                OnPropertyChanged(nameof(Folder));
            }
            if (_savedJson is not null && Storyboard.IsVideo(_board.Folder))
            {
                Storyboard? theirs = null;
                try { theirs = Storyboard.Load(_board.Folder); }
                catch (VideoException) { /* not a video that can be read: this panel's is written */ }
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
            ShowError($"The video couldn't be saved: {ex.Message}", ex);
        }
    }

    // ---- what is made --------------------------------------------------------------------------------------

    private string StateOf(IVideoPiece item, bool made, bool current)
    {
        if (!made)
            return item is ShotItem s && s.Shot.Prompt.Trim().Length == 0 ? "Say what happens in it" : "Not made yet";
        if (!current)
            return "Changed: make it again";
        return VideoVersions.LookIn(item.Piece.Meta(_board)) is { } look
            ? look.Verdict == "redo" ? "Made · the look found a problem" : "Made · looked at"
            : "Made";
    }

    /// <summary>Thumbnails, strips and each piece's state, from the files in the video's folder; the tree and the viewer follow.</summary>
    public void RefreshStates()
    {
        foreach (var frame in _frames.Values)
        {
            var path = _board.FramePicture(frame.Frame);
            var made = File.Exists(path);
            frame.IsMade = made;
            if (!frame.IsWorking)
                (frame.State, frame.IsError) = (StateOf(frame, made, VideoMaker.IsCurrent(_board, frame.Frame)), false);
            LoadThumbnail(frame, path);
        }
        foreach (var shot in Shots)
        {
            var path = _board.ShotVideo(shot.Shot);
            var made = File.Exists(path);
            shot.IsMade = made;
            if (!shot.IsWorking)
                (shot.State, shot.IsError) = (StateOf(shot, made, VideoMaker.IsCurrent(_board, shot.Shot)), false);
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
        foreach (var sound in Sounds)
        {
            var made = File.Exists(_board.SoundFile(sound.Sound));
            sound.IsMade = made;
            sound.Refresh();
            if (!sound.IsWorking)
                (sound.State, sound.IsError) = sound.Sound.Source == VideoSound.FromFile
                    ? (made ? "Sound file" : "The file isn't there", !made)
                    : sound.IsNew ? ("File…, Foley or Music", false)
                    : (!made ? "Not made yet" : VideoMaker.IsCurrent(_board, sound.Sound) ? "Made" : "Changed: make it again", false);
        }
        RefreshReferences();
        foreach (var card in References)
        {
            card.Refresh();
            if (card.Frame is { } frame)
            {
                card.Thumbnail = frame.Thumbnail;
                card.State = card.IsNew ? "File… or Generate" : frame.State;
            }
            else
            {
                card.State = card.Kind;
                LoadCardThumbnail(card);
            }
        }
        OnPropertyChanged(nameof(HasJoinedVideo));
        OnPropertyChanged(nameof(LengthText));
        Relayout();
        RefreshViewer();
    }

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A title in a sentence: "clip 3", "the song", "start of clip 2"; a name ("Marco") as it is.</summary>
    private static string Lower(string s) =>
        new[] { "Clip ", "The ", "Start of", "End of", "Frame ", "Reference ", "A " }.Any(p => s.StartsWith(p, StringComparison.Ordinal)) ? char.ToLowerInvariant(s[0]) + s[1..] : s;

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
                    foreach (var card in References.Where(c => ReferenceEquals(c.Frame, frame)))
                        card.Thumbnail = bitmap;
                    old?.Dispose();
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
        });
    }

    /// <summary>An image file's picture, small, for its card; a video file's, a frame from its middle.</summary>
    private void LoadCardThumbnail(ReferenceCard card)
    {
        if (card.Thumbnail is not null)
            return;
        var path = card.Path;
        var decoder = card.IsVideo ? _previews.Decoder : null;
        if (card.IsVideo && decoder is null)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                using Stream stream = decoder is null ? File.OpenRead(path) : new MemoryStream(VideoCheck.Poster(path, decoder, 256) ?? throw new InvalidOperationException("no frame"));
                var bitmap = Bitmap.DecodeToHeight(stream, 128, BitmapInterpolationMode.MediumQuality);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_disposing || !References.Contains(card))
                        bitmap.Dispose();
                    else
                        card.Thumbnail = bitmap;
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or VideoException) { }
        });
    }

    /// <summary>A clip as a row of frames, for its block on the timeline.</summary>
    private void LoadStrip(ShotItem shot, string path, DateTime stamp)
    {
        if (_previews.Decoder is not { } decoder)
            return;
        _ = Task.Run(() =>
        {
            byte[]? jpeg = null;
            try { jpeg = VideoCheck.Strip(path, decoder, 64); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or VideoException) { }
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

    // ---- the videos folder, as the agent, the jobs and Explorer change it ---------------------------------------

    /// <summary>Something changed in this video's folder since the last refresh (else elsewhere in the videos folder: the tree alone shows it).</summary>
    private bool _videoTouched;

    /// <summary>Where this video's folder was renamed to (in Explorer, by the chat, in another window), to follow it.</summary>
    private string? _renamedTo;

    /// <summary>
    /// Watches the videos folder, all of it: what the agent and the jobs change in this video shows,
    /// and so does a video made, renamed or deleted elsewhere (the chat, another window, Explorer);
    /// this video's folder renamed is followed.
    /// </summary>
    private void Watch()
    {
        _watcher?.Dispose();
        _watcher = null;
        try
        {
            if (!Directory.Exists(_videosRoot))
                return;
            var watcher = new FileSystemWatcher(_videosRoot) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size };
            void Changed(string path, string? from) => Dispatcher.UIThread.Post(() =>
            {
                if (from is not null && SamePath(from, _board.Folder))
                    _renamedTo = path;
                _videoTouched |= IsIn(path, _board.Folder) || from is not null && IsIn(from, _board.Folder);
                _refreshTimer.Stop();
                _refreshTimer.Start();
            });
            watcher.Changed += (_, e) => Changed(e.FullPath, null);
            watcher.Created += (_, e) => Changed(e.FullPath, null);
            watcher.Deleted += (_, e) => Changed(e.FullPath, null);
            watcher.Renamed += (_, e) => Changed(e.FullPath, e.OldFullPath);
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a path is a folder or in it.</summary>
    private static bool IsIn(string path, string folder) =>
        SamePath(path, folder) || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Something in the videos folder changed. In this video's: the agent changed the storyboard in
    /// the chat (it is read again, unless a change made here is still to be saved), a frame, clip
    /// or song was made or put away as a version, or the user put a picture in References: it shows.
    /// This video's folder renamed: the video is followed there; gone: the latest video opens.
    /// Elsewhere: the tree shows the videos as they are.
    /// </summary>
    private void OnFolderChanged()
    {
        if (_disposing)
            return;
        var touched = _videoTouched;
        _videoTouched = false;
        if (_savedJson is not null && !Directory.Exists(_board.Folder))
        {
            var (gone, to) = (Path.GetFileName(_board.Folder), _renamedTo);
            _renamedTo = null;
            if (to is not null && Storyboard.IsVideo(to))
                Follow(to);
            else
            {
                _dirty = false;
                OpenLatest();
                Status = $"{gone} isn't in the videos folder any more.";
            }
            return;
        }
        _renamedTo = null;
        if (!touched)
            return;
        try
        {
            // Not while a change made here waits to be saved: it is put together with theirs then.
            if (!_saveTimer.IsEnabled && !_dirty && Storyboard.IsVideo(_board.Folder))
            {
                var fresh = Storyboard.Load(_board.Folder);
                var json = fresh.ToJson();
                if (json != _savedJson)
                {
                    _savedJson = json;
                    _board = fresh;
                    Rebuild();
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VideoException) { }
        RefreshStates();
    }

    /// <summary>This video's folder was renamed elsewhere: the video is opened there, with a change made here meanwhile.</summary>
    private void Follow(string to)
    {
        var from = _board.Folder;
        _board.Folder = to;
        _board.Title = Path.GetFileName(to);
        Moved(from, to);
        Open(to);
    }

    /// <summary>The video's folder moved: what the chat named goes with it.</summary>
    private void Moved(string from, string to)
    {
        if (_chatVideo is not null && SamePath(_chatVideo, from))
            _chatVideo = to;
    }

    /// <summary>The video changed last, or a new one when there is none.</summary>
    private void OpenLatest()
    {
        if (Storyboard.Recent(_videosRoot).FirstOrDefault() is { } latest)
            Open(latest);
        else
            Untitled();
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
        foreach (var card in References.Where(c => c.Frame is null))
            card.Thumbnail?.Dispose();
        DropViewer();
    }
}
