using System.Diagnostics;
using BlackBox.Core.Film;
using Microsoft.VisualBasic.FileIO;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- the right-click menus: the timeline is where the film is worked on ------------------------------------
    // (the user's call, 2026-10-24: "Right click on timeline -> Create. And then you pick either a file
    // reference to copy the file, or hit a generate button. Remove all that folder tab nonsense.")

    /// <summary>What right-clicking a row of the timeline away from its cards offers: Create, there.</summary>
    public IReadOnlyList<FilmMenuItem?> MenuForRow(string row, double seconds, int track)
    {
        var free = !Busy(_board.Folder);
        return row switch
        {
            "references" => [new("Create", free, CreateReference)],
            "video" => [new("Create", free, AddShot)],
            "sounds" => [new("Create", free, () => CreateSound(seconds, track)), null, new("Delete track", free && _board.Tracks > 1, () => DeleteTrack(track))],
            _ => [],
        };
    }

    /// <summary>What right-clicking a card of the References row offers.</summary>
    public IReadOnlyList<FilmMenuItem?> MenuFor(ReferenceCard card)
    {
        var free = !Busy(_board.Folder);
        var items = new List<FilmMenuItem?>
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

    /// <summary>What right-clicking a take on the Video row offers.</summary>
    public IReadOnlyList<FilmMenuItem?> MenuFor(ShotItem shot)
    {
        var free = !Busy(_board.Folder);
        var items = new List<FilmMenuItem?>
        {
            new("Generate", free, () => Generate(shot)),
            new("Start frame", shot.First is not null, () => Show(shot.First)),
            new("End frame", shot.Last is not null, () => Show(shot.Last)),
            null,
            new("Delete", free, () => DeleteShot(shot)),
        };
        AddVersions(items, shot);
        items.Add(null);
        items.Add(new("Create", free, AddShot));
        return items;
    }

    /// <summary>What right-clicking a sound on a sound track offers.</summary>
    public IReadOnlyList<FilmMenuItem?> MenuFor(SoundItem sound)
    {
        var free = !Busy(_board.Folder);
        var items = new List<FilmMenuItem?>
        {
            new("File…", free && (sound.Sound.Source == FilmSound.FromFile || !sound.IsMade), () => _ = PickFileForAsync(sound)),
            new("Generate", free && sound.Sound.Source == FilmSound.Song, () => Generate(sound)),
            null,
            new("Rename", free, () => RenameAsked?.Invoke(sound, sound.Title), "F2"),
            new("Delete", free, () => DeleteSound(sound)),
        };
        AddVersions(items, sound);
        return items;
    }

    /// <summary>What the film's name at the top offers: the films to open, a new one, and this one renamed or deleted.</summary>
    public IReadOnlyList<FilmMenuItem?> FilmMenu()
    {
        var idle = !Busy(_board.Folder);
        var items = new List<FilmMenuItem?>();
        foreach (var film in Storyboard.Recent(_filmsRoot).Append(_board.Folder).Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(f => Path.GetRelativePath(_filmsRoot, f), ByName))
        {
            var path = film;
            var open = SamePath(path, _board.Folder);
            items.Add(new((open ? "● " : "") + Path.GetRelativePath(_filmsRoot, path), !open, () => Open(path)));
        }
        items.Add(null);
        items.Add(new("New film", true, () => NewFilmCommand.Execute(null)));
        items.Add(new("Rename", idle, () => RenameFilmAsked?.Invoke()));
        items.Add(new("Delete", idle, DeleteFilm));
        items.Add(new("Show in Explorer", true, () => ShowInExplorer(_board.Folder)));
        return items;
    }

    /// <summary>A piece's earlier versions, as a submenu: picked, one is shown, to put back.</summary>
    private void AddVersions(List<FilmMenuItem?> items, IFilmPiece piece)
    {
        var versions = FilmTakes.List(_board, piece.Piece);
        if (versions.Count == 0)
            return;
        items.Add(new("Versions", true, () => { })
        {
            Items = versions.Select(v => (FilmMenuItem?)new FilmMenuItem($"Version {v.Number} · {v.Made.ToLocalTime():d MMM, HH:mm}", true, () => ShowVersion(piece, v))).ToList(),
        });
    }

    /// <summary>Whether the film's folder is written into now (a piece being made, the chat at work on it): renamed or deleted, the work went astray.</summary>
    private bool Busy(string folder) =>
        FilmMaker.IsMaking(folder) || SamePath(folder, _board.Folder) && _chat.FilmCall() is { } call && IsThisFilm(call.Args);

    // ---- creating: a card or clip that is a file of the user's or generated --------------------------------------

    /// <summary>A new card on the References row, not chosen yet: File… copies a picture in, Generate has BlackBox make one.</summary>
    private void CreateReference()
    {
        var name = "New image";
        for (var n = 2; _board.Frames.Any(f => f.Reference && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                        File.Exists(Path.Combine(_board.ReferencesFolder, name + ".png")); n++)
            name = $"New image {n}";
        var frame = new FilmFrame { Id = _board.NewId("r"), Name = name, Reference = true, Seed = Storyboard.NewSeed() };
        _board.Frames.Add(frame);
        Changed();
        SaveNow(force: true);
        Rebuild();
        Show(_frames.GetValueOrDefault(frame.Id));
    }

    /// <summary>A new sound on a track from <paramref name="seconds"/>, not chosen yet: File… copies a sound in, Generate has BlackBox make a song.</summary>
    private void CreateSound(double seconds, int track)
    {
        var name = "New sound";
        for (var n = 2; _board.Sounds.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"New sound {n}";
        var sound = new FilmSound { Id = _board.NewId("a"), Name = name, Start = Math.Round(seconds * 10) / 10, Track = track, Seed = Storyboard.NewSeed() };
        _board.Sounds.Add(sound);
        Changed();
        SaveNow(force: true);
        Rebuild();
        Show(Sounds.FirstOrDefault(s => s.Id == sound.Id));
    }

    /// <summary>Another sound track, under the last.</summary>
    public void AddTrack()
    {
        if (_board.Tracks >= Storyboard.MaxTracks)
            return;
        _board.Tracks++;
        Changed();
        SaveNow(force: true);
        Rebuild();
    }

    /// <summary>
    /// Asks the panel for files of the user's to copy in (the Open dialog), of a kind: "image" or
    /// "sound"; set by the panel.
    /// </summary>
    public Func<string, Task<IReadOnlyList<string>>>? PickFiles { get; set; }

    /// <summary>File… on a card: the picture picked is copied into References and is that card from then on.</summary>
    private async Task PickFileForAsync(ReferenceCard card)
    {
        if (PickFiles is null || (await PickFiles("image")).FirstOrDefault() is not { } picture)
            return;
        try
        {
            SaveNow(force: true);
            string copy;
            if (card.Frame is { IsMade: false } frame)
                copy = FillReference(frame, picture);
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
            ShowError($"The picture couldn't be copied in: {ex.Message}", ex);
        }
    }

    /// <summary>File… on a sound: the sound picked is copied into References and plays in that clip's place.</summary>
    private async Task PickFileForAsync(SoundItem item)
    {
        if (PickFiles is null || (await PickFiles("sound")).FirstOrDefault() is not { } file)
            return;
        DragOver(new FilmDropPlace("sounds", item.Sound.Start, item.Sound.Track, item.IsNew ? item : null));
        if (!item.IsNew)
        {
            // In its place: the clip there now goes, its place taken by the new file.
            _board.Sounds.Remove(item.Sound);
            Changed();
        }
        Drop([file]);
    }

    /// <summary>Generate: the piece goes into the chat's box; with what to make said, BlackBox makes it, else the user says what it should be.</summary>
    private void Generate(IFilmPiece piece)
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
            _chat.SendFromToolkit("Make it.");
        else
            Status = $"Say in the chat what {Lower(piece.Title)} should be, and BlackBox makes it.";
    }

    // ---- renaming ------------------------------------------------------------------------------------------------

    /// <summary>A card or sound is to be renamed: the panel shows a box with its name to type the new one in.</summary>
    public event Action<object, string>? RenameAsked;

    /// <summary>The name typed for a card, a sound, or (with null) the film.</summary>
    public void Rename(object? target, string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return;
        switch (target)
        {
            case null:
                RenameFilm(name);
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

    /// <summary>Renames the film: its folder, named after it, and the joined film in it.</summary>
    private void RenameFilm(string name)
    {
        if (name == FilmName)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException)
        {
            ShowError(ex is FilmException ? $"The film couldn't be renamed: {ex.Message}" : $"The film couldn't be renamed: {ex.Message} A file in it may be open in another program.", ex);
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
        if (sound.Source == FilmSound.FromFile && File.Exists(_board.SoundFile(sound)))
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

    /// <summary>The open film to the Recycle Bin; the film changed last opens.</summary>
    private void DeleteFilm()
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
            ShowError($"The film couldn't be deleted: {ex.Message}", ex, folder);
            RefreshViewer();
            return;
        }
        // Nothing of it is saved again: it would come back.
        _saveTimer.Stop();
        _dirty = false;
        OpenLatest();
        Status = $"{Path.GetFileName(folder)} is in the Recycle Bin.";
    }

    /// <summary>A take out of the film, its files to the Recycle Bin with the frames only it uses (see <see cref="Storyboard.DeleteShot"/>).</summary>
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
            ShowError($"The take couldn't be deleted: {ex.Message}", ex);
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

    /// <summary>A sound off its track, its file to the Recycle Bin (a song with its versions; a sound file of the user's, its copy).</summary>
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

    /// <summary>A sound track with its sounds; the tracks under it move up.</summary>
    private void DeleteTrack(int track)
    {
        foreach (var sound in Sounds.Where(s => s.Sound.Track == track).ToList())
            DeleteSound(sound);
        foreach (var sound in _board.Sounds.Where(s => s.Track > track))
            sound.Track--;
        _board.Tracks = Math.Max(1, _board.Tracks - 1);
        Changed();
        SaveNow();
        Rebuild();
    }

    /// <summary>The film read again from its folder, after a change to it failed halfway.</summary>
    private void Reload()
    {
        try
        {
            if (Storyboard.IsFilm(_board.Folder))
            {
                _board = Storyboard.Load(_board.Folder);
                _savedJson = _board.ToJson();
                _dirty = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException) { }
    }

    // ---- a sound dragged on its track ----------------------------------------------------------------------

    /// <summary>A sound moved along its track or to another, or trimmed at an edge, as a video editor's clip is; saved once let go.</summary>
    public void MoveSound(SoundItem item, double start, int track, double trim, double seconds)
    {
        var sound = item.Sound;
        (sound.Start, sound.Track, sound.Trim, sound.Seconds) =
            (Math.Max(0, Math.Round(start * 10) / 10), Math.Clamp(track, 0, _board.Tracks - 1), Math.Max(0, Math.Round(trim * 10) / 10), Math.Max(0, Math.Round(seconds * 10) / 10));
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
