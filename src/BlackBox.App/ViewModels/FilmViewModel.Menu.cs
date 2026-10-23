using System.Diagnostics;
using BlackBox.Core.Film;
using Microsoft.VisualBasic.FileIO;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- the right-click menus: the tree as Explorer, and a shot on the timeline ------------------------------

    /// <summary>
    /// What right-clicking in the tree offers (the user's calls, 2026-10-24: "right click and rename
    /// films", a new reference, shot or sound made from where it goes, a shot deleted): null for a
    /// line between entries. Away from every folder and file, New film.
    /// </summary>
    public IReadOnlyList<FilmMenuItem?> MenuFor(FilmNode? node)
    {
        var free = !Busy(_board.Folder);
        var items = new List<FilmMenuItem?>();
        switch (node)
        {
            case null:
                items.Add(new("New film", true, NewFilm));
                return items;
            case { IsFilm: true }:
                var open = SamePath(node.Path, _board.Folder);
                var idle = !Busy(node.Path);
                items.Add(new("Rename", idle, () => StartRename(node), "F2"));
                items.Add(new("Delete", idle, () => DeleteFilm(node)));
                items.Add(null);
                items.Add(new("New film", true, NewFilm));
                items.Add(new("New shot", idle, () =>
                {
                    if (!open)
                        Open(node.Path);
                    AddShot();
                }));
                break;
            case { IsReferences: true }:
                items.Add(new("New reference", free, NewReference));
                break;
            case { References: not null }:
                items.Add(new("Rename", CanRename(node), () => StartRename(node), "F2"));
                items.Add(new("Delete", free, () => DeleteReference(node)));
                items.Add(null);
                items.Add(new("New reference", free, NewReference));
                break;
            case { Shot: { } shot }:
                items.Add(new("Delete", free, () => DeleteShot(shot)));
                items.Add(null);
                items.Add(new("New shot", free, AddShot));
                break;
            case { IsSound: true }:
                items.Add(new("New sound", free && _board.Sound.Source == FilmSound.None, NewSound));
                items.Add(new("Delete", free && _board.Sound.Source != FilmSound.None, DeleteSound));
                break;
            case { Piece: SoundItem }:
                items.Add(new("Delete", free, DeleteSound));
                break;
        }
        if (items.Count > 0)
            items.Add(null);
        items.Add(new("Show in Explorer", true, () => ShowInExplorer(node.Path)));
        return items;
    }

    /// <summary>What right-clicking a shot on the timeline offers.</summary>
    public IReadOnlyList<FilmMenuItem?> MenuFor(ShotItem shot)
    {
        var free = !Busy(_board.Folder);
        return [new("New shot", free, AddShot), new("Delete shot", free, () => DeleteShot(shot))];
    }

    /// <summary>Whether a film's folder is written into now (a piece being made, the chat at work on it): renamed or deleted, the work went astray.</summary>
    private bool Busy(string folder) =>
        FilmMaker.IsMaking(folder) || SamePath(folder, _board.Folder) && _chat.FilmCall() is { } call && IsThisFilm(call.Args);

    // ---- renaming in the tree --------------------------------------------------------------------------------

    /// <summary>The folder whose name is being typed in the tree; the tree waits meanwhile.</summary>
    private FilmNode? _renaming;

    /// <summary>A film's folder can be renamed, and a reference's whose pictures aren't made yet (a new reference).</summary>
    public bool CanRename(FilmNode node) =>
        node.IsFilm ? !Busy(node.Path) : node.References is { Count: > 0 } references && references.All(r => !r.IsMade) && !Busy(_board.Folder);

    /// <summary>Its name becomes a box in the tree to type the new one in, as in Explorer (F2).</summary>
    public void StartRename(FilmNode node)
    {
        if (_renaming is { } other)
            FinishRename(other, keep: true);
        if (!CanRename(node))
            return;
        node.EditName = node.Name;
        _renaming = node;
        node.IsRenaming = true;
    }

    /// <summary>The name typed: kept (Enter, or a click elsewhere), or not (Esc).</summary>
    public void FinishRename(FilmNode node, bool keep)
    {
        if (!node.IsRenaming)
            return;
        node.IsRenaming = false;
        _renaming = null;
        var name = node.EditName.Trim();
        if (keep && name.Length > 0 && name != node.Name)
        {
            if (node.IsFilm)
                RenameFilm(node, name);
            else
                RenameReference(node, name);
        }
        else
            RefreshTree();
    }

    /// <summary>Renames a film: its folder, named after it, and the joined film in it. The open one is shown on from its new folder.</summary>
    private void RenameFilm(FilmNode node, string name)
    {
        if (!SamePath(node.Path, _board.Folder))
        {
            try { Storyboard.Rename(node.Path, name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FilmException)
            {
                ShowError(RenameError(ex), ex, node.Path);
            }
            RefreshTree();
            return;
        }
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
            ShowError(RenameError(ex), ex);
        }
        Rebuild();
        OnPropertyChanged(nameof(Folder));
        _chat.Pick([]);
        PickFilm();

        static string RenameError(Exception ex) =>
            ex is FilmException ? $"The film couldn't be renamed: {ex.Message}" : $"The film couldn't be renamed: {ex.Message} A file in it may be open in another program.";
    }

    /// <summary>Renames a reference not made yet: its folder in References is named after it.</summary>
    private void RenameReference(FilmNode node, string name)
    {
        var safe = Storyboard.Safe(name);
        var ids = node.References!.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var to = Path.Combine(_board.ReferencesFolder, safe);
        if (!SamePath(to, node.Path) && (Directory.Exists(to) || _board.Frames.Any(f => f.Reference && !ids.Contains(f.Id) && FolderName(f).Equals(safe, StringComparison.OrdinalIgnoreCase))))
        {
            Status = $"There is a reference called {safe} already.";
            RefreshTree();
            return;
        }
        foreach (var frame in _board.Frames.Where(f => ids.Contains(f.Id)))
        {
            // "Mira/side" is another picture of Mira: it goes with her folder.
            var rest = (frame.Name ?? frame.Id).Split('/', 2) is [_, var more] ? "/" + more : "";
            frame.Name = safe + rest;
            frame.Place = null;
        }
        Changed();
        SaveNow();
        // Its old folder, empty now: it held only the .json of what is to be made there.
        try
        {
            if (Directory.Exists(node.Path) && !SamePath(to, node.Path) && !Directory.EnumerateFileSystemEntries(node.Path).Any())
                Directory.Delete(node.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Rebuild();
        Select(to);

        static string FolderName(FilmFrame frame) => (frame.Name ?? frame.Id).Split('/', '\\')[0].Trim();
    }

    /// <summary>Picks something in the tree by its path, as a click there does, its folders opened.</summary>
    private FilmNode? Select(string path)
    {
        if (Find(path) is not { } node)
            return null;
        foreach (var folder in Tree.SelectMany(All).Where(n => All(n).Contains(node) && n != node))
            folder.IsExpanded = true;
        SelectedNode = node;
        return node;
    }

    // ---- what is made new from the tree ----------------------------------------------------------------------

    /// <summary>
    /// A new reference, not made yet: its folder in References with its picture to be made, named as
    /// it is typed; then the user picks it and says in the chat what it is to be.
    /// </summary>
    private void NewReference()
    {
        var name = "New reference";
        for (var n = 2; Taken(name); n++)
            name = $"New reference {n}";
        _board.Frames.Add(new FilmFrame { Id = _board.NewId("r"), Name = name, Reference = true, Seed = Storyboard.NewSeed() });
        Changed();
        SaveNow();
        Rebuild();
        if (Select(Path.Combine(_board.ReferencesFolder, name)) is { } node)
            StartRename(node);

        bool Taken(string folder) =>
            Directory.Exists(Path.Combine(_board.ReferencesFolder, folder)) ||
            _board.Frames.Any(f => f.Reference && (f.Name ?? f.Id).Split('/', '\\')[0].Trim().Equals(folder, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A song for the film, not made yet, in Sound: the user picks it and says in the chat what music they want.</summary>
    private void NewSound()
    {
        if (_board.Sound.Source != FilmSound.None)
            return;
        _board.Sound.Source = FilmSound.Song;
        Changed();
        SaveNow();
        RefreshStates();
        Select(_board.SongFile);
    }

    // ---- deleting, to the Recycle Bin ------------------------------------------------------------------------

    /// <summary>A folder to the Recycle Bin, as Explorer's Delete puts it: it can be brought back from there.</summary>
    private static void ToRecycleBin(string folder)
    {
        if (!OperatingSystem.IsWindows())
            throw new IOException("Only Windows has a Recycle Bin to put it in.");
        FileSystem.DeleteDirectory(folder, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <summary>A film to the Recycle Bin; the open one gives way to the film changed last.</summary>
    private void DeleteFilm(FilmNode node)
    {
        var open = SamePath(node.Path, _board.Folder);
        if (open)
        {
            StopPlaying();
            DropViewer();
        }
        try
        {
            if (Directory.Exists(node.Path))
                ToRecycleBin(node.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowError($"The film couldn't be deleted: {ex.Message}", ex, node.Path);
            RefreshViewer();
            return;
        }
        if (!open)
        {
            RefreshTree();
            return;
        }
        // Nothing of it is saved again: it would come back.
        _saveTimer.Stop();
        _dirty = false;
        OpenLatest();
        Status = $"{Path.GetFileName(node.Path)} is in the Recycle Bin.";
    }

    /// <summary>
    /// A shot out of the film, its folder to the Recycle Bin with the frames only it uses; the frame
    /// the next shot goes on from stays (see <see cref="Storyboard.DeleteShot"/>).
    /// </summary>
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
            ShowError($"The shot couldn't be deleted: {ex.Message}", ex);
            Reload();
        }
        Rebuild();
        Clear(viewer: true);
    }

    /// <summary>A reference's folder to the Recycle Bin, with the references in it; what was made from them no longer names them.</summary>
    private void DeleteReference(FilmNode node)
    {
        SaveNow();
        StopPlaying();
        DropViewer();
        var ids = (node.References ?? []).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (Directory.Exists(node.Path))
                ToRecycleBin(node.Path);
            _board.Frames.RemoveAll(f => ids.Contains(f.Id));
            foreach (var frame in _board.Frames)
                frame.From.RemoveAll(ids.Contains);
            Changed();
            SaveNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowError($"The reference couldn't be deleted: {ex.Message}", ex);
        }
        Rebuild();
        Clear(viewer: true);
    }

    /// <summary>The film's soundtrack taken out: a song made goes to the Recycle Bin with its takes; a file of the user's stays where it is.</summary>
    private void DeleteSound()
    {
        SaveNow();
        StopPlaying();
        DropViewer();
        try
        {
            var folder = Path.GetDirectoryName(_board.SongFile)!;
            if (_board.Sound.Source == FilmSound.Song && (File.Exists(_board.SongFile) || Directory.Exists(Path.Combine(folder, Storyboard.TakesName))))
                ToRecycleBin(folder);
            var voice = (_board.Sound.Voice, _board.Sound.VoiceSeed);
            _board.Sound.Source = FilmSound.None;
            (_board.Sound.File, _board.Sound.Tags, _board.Sound.Lyrics, _board.Sound.Model) = (null, "", "", null);
            (_board.Sound.Voice, _board.Sound.VoiceSeed) = voice; // the lines' voice is the shots'
            Changed();
            SaveNow();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            ShowError($"The soundtrack couldn't be deleted: {ex.Message}", ex);
        }
        Rebuild();
        Clear(viewer: true);
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

    // ---- Explorer --------------------------------------------------------------------------------------------

    /// <summary>A folder opened in Explorer, or a file shown in its folder; for a piece not made yet, the folder it is to be in, as far as there is one.</summary>
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
