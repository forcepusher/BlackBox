using System.Diagnostics;
using Sloperator.Core.Video;
using Microsoft.VisualBasic.FileIO;

namespace Sloperator.App.ViewModels;

public sealed partial class VideoGeneratorViewModel
{
    // ---- the right-click menus: the timeline is where the video is worked on ------------------------------------
    // (the user's call, 2026-10-24: "Right click on timeline -> Create. And then you pick either a file
    // reference to copy the file, or hit a generate button. Remove all that folder tab nonsense.")

    /// <summary>What right-clicking a row of the timeline away from its cards offers: Create, there.</summary>
    public IReadOnlyList<VideoMenuItem?> MenuForRow(string row, double seconds, int soundLine)
    {
        var free = !Busy(_board.Folder);
        return row switch
        {
            "references" => [new("Create", free, CreateReference)],
            "video" => [new("Create", free, AddShot)],
            "sounds" => [new("Create", free, () => CreateSound(seconds, soundLine)), null, new("Delete sound line", free && _board.SoundLines > 1, () => DeleteSoundLine(soundLine))],
            _ => [],
        };
    }

    /// <summary>What right-clicking a card of the References row offers.</summary>
    public IReadOnlyList<VideoMenuItem?> MenuFor(ReferenceCard card)
    {
        var free = !Busy(_board.Folder);
        var items = new List<VideoMenuItem?>
        {
            new("File…", free && (card.Frame is null || !card.Frame.IsMade), () => _ = PickFileForAsync(card)),
            new("Generate", free && card.Frame is not null, () => Generate(card.Frame!)),
            null,
            new("Rename", free, () => RenameAsked?.Invoke(card, card.Name), "F2"),
            new("Delete", free, () => DeleteReference(card)),
        };
        if (card.Frame is { } frame)
            AddVersions(items, frame);
        items.Add(null);
        items.Add(new("Create", free, CreateReference));
        return items;
    }

    /// <summary>What right-clicking a clip on the Video clips row offers.</summary>
    public IReadOnlyList<VideoMenuItem?> MenuFor(ShotItem shot)
    {
        var free = !Busy(_board.Folder);
        var items = new List<VideoMenuItem?>
        {
            new("Generate", free, () => Generate(shot)),
            null,
            new("Delete", free, () => DeleteShot(shot)),
        };
        AddVersions(items, shot);
        items.Add(null);
        items.Add(new("Create", free, AddShot));
        return items;
    }

    /// <summary>What right-clicking a clip's start or end frame offers; File… and Generate are on a slot not made too, when it has room.</summary>
    public IReadOnlyList<VideoMenuItem?> MenuFor(FrameSlot slot)
    {
        var free = !Busy(_board.Folder);
        var items = new List<VideoMenuItem?>
        {
            new("File…", free && slot.Frame is not { IsMade: true }, () => _ = PickFileForAsync(slot)),
            new("Generate", free, () => Generate(FrameFor(slot))),
        };
        if (slot.Frame is { } frame)
            AddVersions(items, frame);
        return items;
    }

    /// <summary>What right-clicking a sound on a sound line offers: File…, Foley and Music first, as a new one's buttons are.</summary>
    public IReadOnlyList<VideoMenuItem?> MenuFor(SoundItem sound)
    {
        var free = !Busy(_board.Folder);
        var generated = sound.Sound.Source != VideoSound.FromFile;
        var items = new List<VideoMenuItem?>
        {
            new("File…", free && (!generated || !sound.IsMade), () => _ = PickFileForAsync(sound)),
            new("Foley", free && generated, () => MakeFoley(sound)),
            new("Music", free && generated, () => MakeMusic(sound)),
            null,
            new("Rename", free, () => RenameAsked?.Invoke(sound, sound.Title), "F2"),
            new("Delete", free, () => DeleteSound(sound)),
        };
        AddVersions(items, sound);
        return items;
    }

    /// <summary>What the video's name at the top offers: the videos to open, a new one, and this one renamed or deleted.</summary>
    public IReadOnlyList<VideoMenuItem?> VideoMenu()
    {
        var idle = !Busy(_board.Folder);
        var items = new List<VideoMenuItem?>();
        foreach (var video in Storyboard.Recent(_videosRoot).Append(_board.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(f => Path.GetRelativePath(_videosRoot, f), ByName))
        {
            var path = video;
            var open = SamePath(path, _board.Folder);
            items.Add(new((open ? "● " : "") + Path.GetRelativePath(_videosRoot, path), !open, () => Open(path)));
        }
        items.Add(null);
        items.Add(new("New video", true, () => NewVideoCommand.Execute(null)));
        items.Add(new("Rename", idle, () => RenameVideoAsked?.Invoke()));
        items.Add(new("Delete", idle, DeleteVideo));
        items.Add(new("Show in Explorer", true, () => ShowInExplorer(_board.Folder)));
        return items;
    }

    /// <summary>A piece's earlier versions, as a submenu: picked, one is shown, to put back.</summary>
    private void AddVersions(List<VideoMenuItem?> items, IVideoPiece piece)
    {
        var versions = VideoVersions.List(_board, piece.Piece);
        if (versions.Count == 0)
            return;
        items.Add(new("Versions", true, () => { })
        {
            Items = versions.Select(v => (VideoMenuItem?)new VideoMenuItem($"Version {v.Number} · {v.Made.ToLocalTime():d MMM, HH:mm}", true, () => ShowVersion(piece, v))).ToList(),
        });
    }

    /// <summary>Whether the video's folder is written into now (a piece being made, the chat at work on it): renamed or deleted, the work went astray.</summary>
    private bool Busy(string folder) =>
        VideoMaker.IsMaking(folder) || SamePath(folder, _board.Folder) && _chat.VideoCall() is { } call && IsThisVideo(call.Args);

    // ---- creating: a card or clip that is a file of the user's or generated --------------------------------------

    /// <summary>A new card on the References row, not chosen yet: File… copies a picture in, Generate has Sloperator make one.</summary>
    private void CreateReference()
    {
        var name = "New image";
        for (var n = 2; _board.Frames.Any(f => f.Reference && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                        File.Exists(Path.Combine(_board.ReferencesFolder, name + ".png")); n++)
            name = $"New image {n}";
        var frame = new VideoFrame { Id = _board.NewId("r"), Name = name, Reference = true, Seed = Storyboard.NewSeed() };
        _board.Frames.Add(frame);
        Changed();
        SaveNow(force: true);
        Rebuild();
        Show(_frames.GetValueOrDefault(frame.Id));
    }

    /// <summary>The dashed + on a sound line: a new sound there, after its last.</summary>
    public void CreateSound(SoundLineItem line)
    {
        if (!Busy(_board.Folder))
            CreateSound(line.AddSeconds, line.Index);
    }

    /// <summary>
    /// A new sound on a sound line from <paramref name="seconds"/>, not chosen yet: File… copies a
    /// sound in, Foley has Sloperator make the sounds of what the clips under it show, Music a song.
    /// </summary>
    private void CreateSound(double seconds, int soundLine)
    {
        var name = "New sound";
        for (var n = 2; _board.Sounds.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"New sound {n}";
        var sound = new VideoSound { Id = _board.NewId("a"), Name = name, Start = Math.Round(seconds * 10) / 10, SoundLine = soundLine, Seed = Storyboard.NewSeed() };
        _board.Sounds.Add(sound);
        Changed();
        SaveNow(force: true);
        Rebuild();
        Show(Sounds.FirstOrDefault(s => s.Id == sound.Id));
    }

    /// <summary>Another sound line, under the last.</summary>
    public void AddSoundLine()
    {
        if (_board.SoundLines >= Storyboard.MaxSoundLines)
            return;
        _board.SoundLines++;
        while (_board.SoundLineVolumes.Count < _board.SoundLines)
            _board.SoundLineVolumes.Add(1);
        Changed();
        SaveNow(force: true);
        Rebuild();
    }

    /// <summary>
    /// Asks the panel for files of the user's to copy in (the Open dialog), of a kind: "image", "reference"
    /// (a picture or a video) or "sound"; set by the panel.
    /// </summary>
    public Func<string, Task<IReadOnlyList<string>>>? PickFiles { get; set; }

    /// <summary>File… on a card: the picture or video picked is copied into References and is that card from then on.</summary>
    private async Task PickFileForAsync(ReferenceCard card)
    {
        if (PickFiles is null || (await PickFiles("reference")).FirstOrDefault() is not { } picture)
            return;
        try
        {
            SaveNow(force: true);
            string copy;
            if (card.Frame is { IsMade: false } frame)
                copy = FillReference(frame, picture);
            else if (!string.Equals(Path.GetExtension(picture), Path.GetExtension(card.Path), StringComparison.OrdinalIgnoreCase))
            {
                // Another kind of file (a video for a picture): a card of its own beside it, the one there kept.
                copy = Storyboard.KeepPicture(picture, _board.ReferencesFolder);
            }
            else
            {
                // An image file of the user's replaced by another: the new one in its place, under its name.
                Core.Media.MediaRelease.Release(card.Path);
                File.Copy(picture, card.Path, overwrite: true);
                copy = card.Path;
            }
            Rebuild();
            ShowFile(copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The file couldn't be copied in: {ex.Message}", ex);
        }
    }

    /// <summary>File… on a clip's frame: the picture picked is that frame, cut to the video's size, at once.</summary>
    private async Task PickFileForAsync(FrameSlot slot)
    {
        if (PickFiles is null || (await PickFiles("image")).FirstOrDefault() is not { } picture)
            return;
        var item = FrameFor(slot);
        try
        {
            // The user's picture, where it is, as the chat gives one ("file"); not the clip before's end any more.
            (item.Frame.File, item.Frame.Of) = (picture, null);
            Changed();
            SaveNow(force: true);
            VideoMaker.TakeFile(_board, item.Frame);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VideoException)
        {
            ShowError($"The picture couldn't be used: {ex.Message}", ex);
        }
        Rebuild();
        Show(_frames.GetValueOrDefault(item.Id));
    }

    /// <summary>
    /// A clip's frame at a slot; a clip with none there gets a new one, made with what the clip's other
    /// frame is (or the frames before), for the user to say in the chat what it shows.
    /// </summary>
    private FrameItem FrameFor(FrameSlot slot)
    {
        if (slot.Frame is { } had)
            return had;
        SaveNow();
        var shot = slot.Shot.Shot;
        var near = _board.Frame(shot.First) ?? _board.Frame(shot.Last);
        var frame = new VideoFrame
        {
            Id = _board.NewId("f"), Seed = Storyboard.NewSeed(),
            Model = near?.Model ?? FrameModelNear(_board.Shots.TakeWhile(s => s != shot).LastOrDefault()),
            From = [.. near?.From ?? FromNear(_board.Shots.TakeWhile(s => s != shot).LastOrDefault())],
        };
        _board.Frames.Add(frame);
        if (slot.IsEnd)
            shot.Last = frame.Id;
        else
            shot.First = frame.Id;
        Changed();
        SaveNow(force: true);
        Rebuild();
        return _frames[frame.Id];
    }

    /// <summary>File… on a sound: the sound picked is copied into References and plays in that clip's place.</summary>
    private async Task PickFileForAsync(SoundItem item)
    {
        if (PickFiles is null || (await PickFiles("sound")).FirstOrDefault() is not { } file)
            return;
        DragOver(new VideoDropPlace("sounds", item.Sound.Start, item.Sound.SoundLine, item.IsNew ? item : null));
        if (!item.IsNew)
        {
            // In its place: the clip there now goes, its place taken by the new file.
            _board.Sounds.Remove(item.Sound);
            Changed();
        }
        Drop([file]);
    }

    /// <summary>A sound line's slider moved: its volume, saved with the video.</summary>
    private void SetLineVolume(int soundLine, double volume)
    {
        if (_loading)
            return;
        while (_board.SoundLineVolumes.Count <= soundLine)
            _board.SoundLineVolumes.Add(1);
        var level = Math.Round(Math.Clamp(volume, 0, 2), 2);
        if (_board.SoundLineVolumes[soundLine] == level)
            return;
        _board.SoundLineVolumes[soundLine] = level;
        Changed();
    }

    /// <summary>
    /// Foley on a sound: it is made from what the clips under it show, so there is nothing to say
    /// first: into the chat's box, and Sloperator makes it (what is heard can be said in the chat too).
    /// </summary>
    private void MakeFoley(SoundItem item)
    {
        if (item.Sound.Source != VideoSound.Foley)
        {
            item.Sound.Source = VideoSound.Foley;
            Changed();
            SaveNow(force: true);
            Rebuild();
        }
        if (Sounds.FirstOrDefault(s => s.Id == item.Id) is not { } sound)
            return;
        Show(sound);
        _chat.SendFromToolkit("Make it.", withPicks: true);
    }

    /// <summary>Music on a sound: a song, made once the chat is told its style (else at once, its style said).</summary>
    private void MakeMusic(SoundItem item)
    {
        if (item.Sound.Source != VideoSound.Song)
        {
            item.Sound.Source = VideoSound.Song;
            Changed();
            SaveNow(force: true);
            Rebuild();
        }
        if (Sounds.FirstOrDefault(s => s.Id == item.Id) is not { } sound)
            return;
        Generate(sound);
        if (sound.Sound.Tags.Trim().Length == 0)
            Status = $"Say in the chat what music {Lower(sound.Title)} should be (its style, and words if it is sung), and Sloperator makes it.";
    }

    /// <summary>Generate: the piece goes into the chat's box; with what to make said, Sloperator makes it, else the user says what it should be.</summary>
    private void Generate(IVideoPiece piece)
    {
        Show(piece);
        var said = piece switch
        {
            FrameItem f => f.Frame.Prompt.Trim().Length > 0,
            ShotItem s => s.Shot.Prompt.Trim().Length > 0,
            SoundItem a => a.Sound.Tags.Trim().Length > 0,
            _ => false,
        };
        if (said)
            _chat.SendFromToolkit("Make it.", withPicks: true);
        else
            Status = $"Say in the chat what {Lower(piece.Title)} should be, and Sloperator makes it.";
    }

    // ---- renaming ------------------------------------------------------------------------------------------------

    /// <summary>A card or sound is to be renamed: the panel shows a box with its name to type the new one in.</summary>
    public event Action<object, string>? RenameAsked;

    /// <summary>The name typed for a card, a sound, or (with null) the video.</summary>
    public void Rename(object? target, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;
        switch (target)
        {
            case null:
                RenameVideo(name);
                break;
            case ReferenceCard { Frame: { } frame }:
                RenameGenerated(frame, name);
                break;
            case ReferenceCard card:
                RenamePicture(card.Path, name);
                break;
            case SoundItem sound:
                RenameSound(sound, name);
                break;
        }
    }

    /// <summary>Renames the video: its folder, named after it, and the joined video in it.</summary>
    private void RenameVideo(string name)
    {
        if (name == VideoName)
            return;
        SaveNow();
        // What shows a file of it lets go of it first: the folder can't be moved while a file in it is open.
        StopPlaying();
        DropViewer();
        try
        {
            var from = _board.Folder;
            _board.Rename(name);
            Moved(from, _board.Folder);
            if (_savedJson is not null)
                _savedJson = _board.ToJson();
            Status = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VideoException)
        {
            ShowError(ex is VideoException ? $"The video couldn't be renamed: {ex.Message}" : $"The video couldn't be renamed: {ex.Message} A file in it may be open in another program.", ex);
        }
        Rebuild();
        OnPropertyChanged(nameof(Folder));
        Clear();
    }

    /// <summary>Renames a generated image: its name, and its picture with what goes with it once made.</summary>
    private void RenameGenerated(FrameItem item, string name)
    {
        var frame = item.Frame;
        var from = _board.FramePicture(frame);
        var safe = Storyboard.Safe(name);
        if (_board.Frames.Any(f => f != frame && f.Reference && string.Equals(f.Name, safe, StringComparison.OrdinalIgnoreCase)))
        {
            Status = $"There is an image called {safe} already.";
            return;
        }
        SaveNow();
        frame.Name = safe;
        var to = Path.GetFullPath(Path.Combine(_board.Folder, _board.PlaceFor(frame)));
        if (File.Exists(from) && !SamePath(from, to))
        {
            if (File.Exists(to))
            {
                Status = $"There is a {Path.GetFileName(to)} there already.";
                frame.Name = item.Label;
                return;
            }
            try { MoveWith(from, to); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowError($"The image couldn't be renamed: {ex.Message} It may be open in another program.", ex);
                return;
            }
            frame.Place = Path.GetRelativePath(_board.Folder, to).Replace(Path.DirectorySeparatorChar, '/');
        }
        else
            frame.Place = null;
        Changed();
        SaveNow();
        Rebuild();
        Show(_frames.GetValueOrDefault(frame.Id));
    }

    /// <summary>
    /// Renames an image file of the user's (the user's call, 2026-10-24: "dragged-and-dropped stuff
    /// should be copied and able to be renamed"), keeping its kind of file; the frames made from it
    /// are told its new name.
    /// </summary>
    private void RenamePicture(string path, string name)
    {
        var extension = Path.GetExtension(path);
        var stem = Storyboard.Safe(name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name[..^extension.Length] : name);
        var to = Path.Combine(Path.GetDirectoryName(path)!, stem + extension);
        if (stem.Length == 0 || SamePath(to, path))
            return;
        if (File.Exists(to) || File.Exists(Storyboard.MetaOf(to)))
        {
            Status = $"There is a {Path.GetFileName(to)} there already.";
            return;
        }
        var (oldName, newName) = (_board.InReferences(path), _board.InReferences(to));
        DropViewer();
        try
        {
            File.Move(path, to);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The picture couldn't be renamed: {ex.Message} It may be open in another program.", ex);
            return;
        }
        var bare = Path.GetFileNameWithoutExtension(path);
        foreach (var frame in _board.Frames)
        {
            for (var i = 0; i < frame.From.Count; i++)
                if (string.Equals(frame.From[i], oldName, StringComparison.OrdinalIgnoreCase) || string.Equals(frame.From[i], bare, StringComparison.OrdinalIgnoreCase) ||
                    Path.IsPathFullyQualified(frame.From[i]) && SamePath(frame.From[i], path))
                    frame.From[i] = newName;
            if (frame.File is { } file && SamePath(file, path))
                frame.File = to;
        }
        Changed();
        SaveNow();
        Rebuild();
        ShowFile(to);
    }

    /// <summary>Renames a sound: its name, and a sound file of the user's with it.</summary>
    private void RenameSound(SoundItem item, string name)
    {
        var sound = item.Sound;
        SaveNow();
        if (sound.Source == VideoSound.FromFile && File.Exists(_board.SoundFile(sound)))
        {
            var from = _board.SoundFile(sound);
            var to = Path.Combine(Path.GetDirectoryName(from)!, Storyboard.Safe(name) + Path.GetExtension(from));
            if (!SamePath(from, to))
            {
                if (File.Exists(to))
                {
                    Status = $"There is a {Path.GetFileName(to)} there already.";
                    return;
                }
                StopPlaying();
                DropViewer();
                try { MoveWith(from, to); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ShowError($"The sound couldn't be renamed: {ex.Message} It may be open in another program.", ex);
                    return;
                }
                sound.Place = Path.GetRelativePath(_board.Folder, to).Replace(Path.DirectorySeparatorChar, '/');
            }
        }
        sound.Name = name;
        Changed();
        SaveNow();
        Rebuild();
        Show(Sounds.FirstOrDefault(s => s.Id == sound.Id));
    }

    /// <summary>A file moved with its .json, workflow and versions.</summary>
    private static void MoveWith(string from, string to)
    {
        var (fromStem, toStem) = (Path.GetFileNameWithoutExtension(from), Path.GetFileNameWithoutExtension(to));
        foreach (var file in Storyboard.FilesOf(from).Where(File.Exists).ToList())
        {
            var name = Path.GetFileName(file);
            var target = Path.Combine(Path.GetDirectoryName(file)!, toStem + name[fromStem.Length..]);
            File.Move(file, target);
        }
    }

    // ---- deleting, to the Recycle Bin ------------------------------------------------------------------------

    /// <summary>A folder or file to the Recycle Bin, as Explorer's Delete puts it: it can be brought back from there.</summary>
    private static void ToRecycleBin(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new IOException("Only Windows has a Recycle Bin to put it in.");
        if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else if (File.Exists(path))
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <summary>The open video to the Recycle Bin; the video changed last opens.</summary>
    private void DeleteVideo()
    {
        var folder = _board.Folder;
        StopPlaying();
        DropViewer();
        try
        {
            if (Directory.Exists(folder))
                ToRecycleBin(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowError($"The video couldn't be deleted: {ex.Message}", ex, folder);
            RefreshViewer();
            return;
        }
        // Nothing of it is saved again: it would come back.
        _saveTimer.Stop();
        _dirty = false;
        OpenLatest();
        Status = $"{Path.GetFileName(folder)} is in the Recycle Bin.";
    }

    /// <summary>A clip out of the video, its files to the Recycle Bin with the frames only it uses (see <see cref="Storyboard.DeleteShot"/>).</summary>
    public void DeleteShot(ShotItem item)
    {
        if (Busy(_board.Folder) || _board.Shot(item.Id) is not { } shot)
            return;
        SaveNow();
        StopPlaying();
        DropViewer();
        try
        {
            _board.DeleteShot(shot, ToRecycleBin);
            if (_savedJson is not null)
            {
                _board.Save();
                _savedJson = _board.ToJson();
                _dirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowError($"The clip couldn't be deleted: {ex.Message}", ex);
            Reload();
        }
        Rebuild();
        Clear();
    }

    /// <summary>A card to the Recycle Bin: an image file, or a generated image with what goes with it; frames made from it no longer name it.</summary>
    private void DeleteReference(ReferenceCard card)
    {
        SaveNow();
        StopPlaying();
        DropViewer();
        try
        {
            foreach (var file in Storyboard.FilesOf(card.Path).Where(File.Exists).ToList())
                ToRecycleBin(file);
            var names = new[] { card.Frame?.Id, _board.InReferences(card.Path), Path.GetFileNameWithoutExtension(card.Path) }.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (card.Frame is { } frame)
                _board.Frames.Remove(frame.Frame);
            foreach (var f in _board.Frames)
                f.From.RemoveAll(names.Contains);
            Changed();
            SaveNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowError($"It couldn't be deleted: {ex.Message}", ex);
        }
        Rebuild();
        Clear();
    }

    /// <summary>A sound off its sound line, its file to the Recycle Bin (a song with its versions; a sound file of the user's, its copy).</summary>
    private void DeleteSound(SoundItem item)
    {
        SaveNow();
        StopPlaying();
        DropViewer();
        try
        {
            foreach (var file in Storyboard.FilesOf(_board.SoundFile(item.Sound)).Where(File.Exists).ToList())
                ToRecycleBin(file);
            _board.Sounds.Remove(item.Sound);
            Changed();
            SaveNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowError($"The sound couldn't be deleted: {ex.Message}", ex);
        }
        Rebuild();
        Clear();
    }

    /// <summary>A sound line with its sounds; the sound lines under it move up.</summary>
    private void DeleteSoundLine(int soundLine)
    {
        foreach (var sound in Sounds.Where(s => s.Sound.SoundLine == soundLine).ToList())
            DeleteSound(sound);
        foreach (var sound in _board.Sounds.Where(s => s.SoundLine > soundLine))
            sound.SoundLine--;
        if (soundLine < _board.SoundLineVolumes.Count && _board.SoundLines > 1)
            _board.SoundLineVolumes.RemoveAt(soundLine);
        _board.SoundLines = Math.Max(1, _board.SoundLines - 1);
        Changed();
        SaveNow();
        Rebuild();
    }

    /// <summary>The video read again from its folder, after a change to it failed halfway.</summary>
    private void Reload()
    {
        try
        {
            if (Storyboard.IsVideo(_board.Folder))
            {
                _board = Storyboard.Load(_board.Folder);
                _savedJson = _board.ToJson();
                _dirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or VideoException) { }
    }

    // ---- a sound dragged on its sound line ----------------------------------------------------------------------

    /// <summary>A sound moved along its sound line or to another, or trimmed at an edge, as a video editor's clip is; saved once let go.</summary>
    public void MoveSound(SoundItem item, double start, int soundLine, double trim, double seconds)
    {
        var sound = item.Sound;
        (sound.Start, sound.SoundLine, sound.Trim, sound.Seconds) =
            (Math.Max(0, Math.Round(start * 10) / 10), Math.Clamp(soundLine, 0, _board.SoundLines - 1), Math.Max(0, Math.Round(trim * 10) / 10), Math.Max(0, Math.Round(seconds * 10) / 10));
        Relayout();
        Changed();
    }

    // ---- Explorer --------------------------------------------------------------------------------------------

    /// <summary>A folder opened in Explorer, or a file shown in its folder.</summary>
    private static void ShowInExplorer(string path)
    {
        while (!File.Exists(path) && !Directory.Exists(path) && Path.GetDirectoryName(path) is { } up)
            path = up;
        _ = Task.Run(() =>
        {
            try
            {
                if (OperatingSystem.IsWindows() && File.Exists(path))
                    Process.Start("explorer.exe", $"/select,\"{path}\"")?.Dispose();
                else
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        });
    }
}
