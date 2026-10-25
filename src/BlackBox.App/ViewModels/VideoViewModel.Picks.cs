using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Video;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

/// <summary>Where files dragged over the timeline would go: a card or sound clip, or a row at a time (a sound line's by its number).</summary>
public sealed record VideoDropPlace(string Row, double Seconds, int SoundLine, object? Card);

public sealed partial class VideoViewModel
{
    /// <summary>Names as Explorer orders them: "Video 2" before "Video 10".</summary>
    private static readonly StringComparer ByName =
        StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);

    private static long StampOf(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    // ---- what is picked, into the chat's box -----------------------------------------------------------------

    /// <summary>
    /// What is picked on the timeline and shown in the viewer: it goes into the chat's box. Taken off
    /// the box (its ×), or sent, it is no longer in the box; the viewer keeps it.
    /// </summary>
    [ObservableProperty]
    private VideoPick? _viewing;

    /// <summary>Picks a card, clip, frame, sound or version: shown in the viewer, and into the chat's box.</summary>
    private void Choose(VideoPick pick)
    {
        Viewing = pick;
        _chat.Pick([(pick.Path, pick.Caption)]);
    }

    /// <summary>A piece clicked on the timeline: picked; null shows the whole video.</summary>
    public void Show(IVideoPiece? piece)
    {
        if (piece is null)
        {
            Clear();
            return;
        }
        var path = piece.Piece.File(_board) ?? _board.Folder;
        Choose(new VideoPick(path, $"{piece.Title} · {Path.GetFileName(path)}") { Piece = piece, IsPlanned = !File.Exists(path) });
    }

    /// <summary>A card of the References row clicked: its generated image, or the user's image file.</summary>
    public void Show(ReferenceCard card)
    {
        if (card.Frame is { } frame)
            Show(frame);
        else
            Choose(new VideoPick(card.Path, $"Image file · {Path.GetFileName(card.Path)}"));
    }

    /// <summary>A clip's start or end frame clicked: picked (a clip with none there gets one).</summary>
    public void Show(FrameSlot slot) => Show(FrameFor(slot));

    /// <summary>An earlier version of a piece, from its right-click menu: shown, to put back with Use this version.</summary>
    private void ShowVersion(IVideoPiece piece, VideoVersion version) =>
        Choose(new VideoPick(version.File, $"Version {version.Number} of {Lower(piece.Title)} · {Path.GetFileName(version.File)}") { Version = version, VersionOf = piece });

    /// <summary>Nothing picked: what is said next is about the whole video, which the viewer shows.</summary>
    public void Clear()
    {
        Viewing = null;
        _chat.Pick([]);
    }

    /// <summary>The chat's box changed: a pick taken off it there is no longer picked here (the viewer keeps showing it).</summary>
    private void OnBoxChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
    }

    /// <summary>The message went with what was picked; the viewer keeps showing it, as the chat makes it.</summary>
    public void Sent()
    {
    }

    // ---- files dropped -----------------------------------------------------------------------------------------

    /// <summary>Where files dragged over the timeline would go now; null for elsewhere (the viewer: References or a sound line by their kind).</summary>
    private VideoDropPlace? _drop;

    /// <summary>Files dragged over the timeline, at <paramref name="place"/>: the card or clip there is marked as where they go.</summary>
    public void DragOver(VideoDropPlace? place)
    {
        _drop = place;
        foreach (var card in References)
            card.IsDropTarget = ReferenceEquals(card, place?.Card);
        foreach (var sound in Sounds)
            sound.IsDropTarget = ReferenceEquals(sound, place?.Card);
    }

    /// <summary>
    /// Files dropped on the panel are copied in, as they are, under names of their own (see
    /// <see cref="Storyboard.KeepPicture"/>): pictures into References as image files (one dropped on
    /// a new card is what that card is), sounds into References too, laid on the sound line they were
    /// dropped on from where they were dropped (one dropped on a new clip is what that clip is), else
    /// on the first sound line from the playhead, one after another. Anything else goes with the next message.
    /// </summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        var place = _drop;
        DragOver(null);
        var pictures = paths.Where(p => File.Exists(p) && Storyboard.PictureExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToList();
        var sounds = paths.Where(p => File.Exists(p) && Storyboard.SoundExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToList();
        if (pictures.Count + sounds.Count == 0)
            return false;
        var added = new List<string>();
        try
        {
            SaveNow(force: true); // a new video's folder is made with it
            if (pictures.Count > 0 && place?.Card is ReferenceCard { IsNew: true, Frame: { } fill })
                added.Add(FillReference(fill, pictures[0]));
            else
                foreach (var picture in pictures)
                    added.Add(Storyboard.KeepPicture(picture, _board.ReferencesFolder));
            var at = place is { Row: "sounds" } ? place.Seconds : PlayPosition;
            var soundLine = place is { Row: "sounds" } ? place.SoundLine : 0;
            foreach (var file in sounds)
            {
                var copy = Storyboard.KeepPicture(file, _board.ReferencesFolder);
                added.Add(copy);
                var sound = place?.Card is SoundItem { IsNew: true } clip && file == sounds[0] ? clip.Sound : null;
                if (sound is null)
                {
                    sound = new VideoSound { Id = _board.NewId("a"), Start = at, SoundLine = soundLine };
                    _board.Sounds.Add(sound);
                }
                (sound.Source, sound.Name, sound.Place, sound.Seed, sound.Tags, sound.Lyrics) =
                    (VideoSound.FromFile, Path.GetFileNameWithoutExtension(copy), Path.GetRelativePath(_board.Folder, copy).Replace(Path.DirectorySeparatorChar, '/'), 0, "", "");
                at = sound.Start + Math.Max(1, (_previews.Decoder?.Probe(copy)?.Duration.TotalSeconds ?? 0) - sound.Trim);
            }
            if (sounds.Count > 0)
            {
                _board.Normalize();
                Changed();
                SaveNow();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The file couldn't be copied in: {ex.Message}", ex);
        }
        _chat.Attach(paths.Except(pictures).Except(sounds));
        Status = added.Count switch
        {
            0 => Status,
            1 => $"Copied {Path.GetFileName(added[0])} in: say in the chat what to do with it.",
            _ => $"Copied {added.Count} files in: say in the chat what to do with them.",
        };
        Rebuild();
        if (added.Count > 0)
            ShowFile(added[0]);
        return true;
    }

    /// <summary>
    /// A new card filled with a picture of the user's: the picture is copied into References as an
    /// image file, which the card is from then on (the generated image it was waits no more).
    /// </summary>
    private string FillReference(FrameItem card, string picture)
    {
        var copy = Storyboard.KeepPicture(picture, _board.ReferencesFolder);
        _board.Frames.Remove(card.Frame);
        foreach (var frame in _board.Frames)
            for (var i = 0; i < frame.From.Count; i++)
                if (string.Equals(frame.From[i], card.Id, StringComparison.OrdinalIgnoreCase))
                    frame.From[i] = _board.InReferences(copy);
        Changed();
        SaveNow();
        return copy;
    }

    /// <summary>Picks what a file in the video is on the timeline: a card, a sound, a clip or a frame.</summary>
    private void ShowFile(string path)
    {
        if (References.FirstOrDefault(c => SamePath(c.Path, path)) is { } card)
            Show(card);
        else if (Sounds.FirstOrDefault(s => SamePath(_board.SoundFile(s.Sound), path)) is { } sound)
            Show(sound);
        else if (Shots.FirstOrDefault(s => SamePath(_board.ShotVideo(s.Shot), path)) is { } shot)
            Show(shot);
        else if (_frames.Values.FirstOrDefault(f => SamePath(_board.FramePicture(f.Frame), path)) is { } frame)
            Show(frame);
    }

    // ---- the viewer ---------------------------------------------------------------------------------------

    partial void OnViewingChanged(VideoPick? value)
    {
        var piece = value?.Piece ?? value?.VersionOf;
        foreach (var item in _frames.Values.Concat<IVideoPiece>(Shots).Concat(Sounds))
            item.IsSelected = ReferenceEquals(item, piece);
        foreach (var card in References)
            card.IsSelected = card.Frame is { } f ? ReferenceEquals(f, piece) : value is not null && SamePath(card.Path, value.Path);
        OnPropertyChanged(nameof(IsVersionShown));
        RefreshViewer();
        RefreshDetails();
    }

    /// <summary>A version is shown: it can be put back in the video.</summary>
    public bool IsVersionShown => Viewing?.Version is not null;

    /// <summary>The viewer's content: a picture, a clip, a sound or the video with its player, or a card for what has none.</summary>
    [ObservableProperty]
    private object? _viewer;

    /// <summary>The player or picture in the viewer, and the file and its time it shows, so a file made again is shown again.</summary>
    private (string Path, long Stamp, IDisposable Shown)? _shown;

    private void RefreshViewer()
    {
        if (_disposing)
            return;
        var pick = Viewing;
        var path = pick?.Path ?? (HasJoinedVideo ? _board.JoinedFile : null);
        if (path is not null && pick is not { IsPlanned: true } && File.Exists(path))
        {
            var stamp = StampOf(path);
            if (_shown is { } shown && string.Equals(shown.Path, path, StringComparison.OrdinalIgnoreCase) && shown.Stamp == stamp)
                return;
            DropViewer();
            var kind = Core.Media.MediaPreview.KindOf(path);
            if (kind == "image")
            {
                var caption = pick?.Caption ?? Path.GetFileName(path);
                Viewer = new PictureView(null, caption, null);
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var stream = File.OpenRead(path);
                        var bitmap = Bitmap.DecodeToWidth(stream, 1280, BitmapInterpolationMode.HighQuality);
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (_disposing || !string.Equals(_shown?.Path, path, StringComparison.OrdinalIgnoreCase) || _shown?.Stamp != stamp)
                            {
                                bitmap.Dispose();
                                return;
                            }
                            _shown = (path, stamp, bitmap);
                            Viewer = new PictureView(bitmap, caption, null);
                        });
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
                });
                _shown = (path, stamp, new NoPicture());
                return;
            }
            if (kind is "video" or "audio" or "animation")
            {
                var player = FilePreview.For(path, _previews, 1280);
                _shown = (path, stamp, player);
                Viewer = player;
                return;
            }
            Viewer = new PictureView(null, pick?.Caption ?? Path.GetFileName(path), "Say in the chat what to do with it.");
            _shown = (path, stamp, new NoPicture());
            return;
        }
        DropViewer();
        Viewer = pick switch
        {
            { Piece: ShotItem shot } => new PictureView(shot.First?.Thumbnail, $"{shot.Title} is not made yet", shot.Summary),
            { Piece: FrameItem { Frame.Reference: true, Frame.Prompt.Length: 0 } reference } => new PictureView(null, reference.Title,
                "A new image: pick a file of yours for it (File… on its card, or drop one there), or say in the chat what to generate."),
            { Piece: SoundItem { IsNew: true } sound } => new PictureView(null, sound.Title,
                "A new sound: File… copies a sound of yours in (or drop one there), Foley makes the sounds of what the clips under it show, Music makes a song. Or say in the chat what it should be."),
            { Piece: SoundItem { Sound.Source: VideoSound.Foley } foley } => new PictureView(null, $"{foley.Title} is not made yet",
                "Foley: BlackBox makes the sounds of what the clips under it show. Say in the chat to make it, or what should be heard."),
            { Piece: { } piece } => new PictureView(null, $"{piece.Title} is not made yet", "Say in the chat what it should be, or to make it."),
            // The video's name: what is said with nothing picked is about all of it.
            null => new PictureView(null, VideoName is { Length: > 0 } name ? name : "The video", _board.Shots.All(s => s.Prompt.Trim().Length == 0)
                ? "The whole video is selected. Say in the chat what video you want: BlackBox lays out its references, clips and sounds on the timeline. Nothing is made until you ask for it."
                : "The whole video is selected. Click a card, clip or sound on the timeline to work on it instead, or press Make the video."),
            _ => new PictureView(null, pick.Caption, "It isn't there."),
        };
    }

    /// <summary>The viewer's player of a file about to be written over, stopped; its playback, still holding the file, handed back to end.</summary>
    internal IDisposable? LetGo(string path) =>
        _shown is { Shown: PlayerPreview player } && string.Equals(Path.GetFullPath(player.Path), path, StringComparison.OrdinalIgnoreCase) ? player.LetGo() : null;

    /// <summary>Stops and lets go of what the viewer showed.</summary>
    private void DropViewer()
    {
        if (_shown is { Shown: var shown })
            shown.Dispose();
        _shown = null;
    }

    /// <summary>What the viewer holds for a file it shows no picture or player of.</summary>
    private sealed class NoPicture : IDisposable
    {
        public void Dispose() { }
    }

    /// <summary>The file the viewer shows now, for the full-screen viewer to start from; null with none.</summary>
    public string? ShownFile => _shown?.Path;

    /// <summary>The services the video's previews play with, for the full-screen viewer.</summary>
    public PreviewServices Previews => _previews;

    /// <summary>
    /// The video's pictures and clips as the full-screen viewer goes through them, in the timeline's
    /// order: the References row, each clip's frames and the clip, then the joined video; only what is made.
    /// </summary>
    public List<MediaEntry> MediaInVideo()
    {
        var files = References.Select(c => (c.Path, Caption: $"{c.Name} · {c.Kind}"))
            .Concat(Shots.SelectMany(s => new[] { s.First, s.Last }.OfType<FrameItem>().Select(f => (Path: _board.FramePicture(f.Frame), Caption: f.Title))
                .Append((Path: _board.ShotVideo(s.Shot), Caption: s.Title))))
            .Append((Path: _board.JoinedFile, Caption: "The video"));
        return files.Where(f => File.Exists(f.Path) && MainWindowViewModel.Viewable(f.Path)).DistinctBy(f => Path.GetFullPath(f.Path), StringComparer.OrdinalIgnoreCase)
            .Select(f => new MediaEntry(f.Path, f.Path, null, f.Caption)).ToList();
    }

    public bool HasJoinedVideo => File.Exists(_board.JoinedFile);
}
