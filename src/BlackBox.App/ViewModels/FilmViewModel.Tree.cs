using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using BlackBox.Core.Film;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BlackBox.App.ViewModels;

public sealed partial class FilmViewModel
{
    // ---- the film's folder as a tree -------------------------------------------------------------------------

    /// <summary>
    /// The films folder as Explorer shows it (the user's call, 2026-10-24: "I want it to work just
    /// like a file explorer"): a folder for each film, the open one with all in it (References, a
    /// folder for each shot, Sound and the film, with what is planned and isn't made yet).
    /// </summary>
    public ObservableCollection<FilmNode> Tree { get; } = [];

    /// <summary>The folders open in the tree, by their paths: kept as the tree is built again when the folder changes.</summary>
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the tree was last built from, to build it again only when that changes.</summary>
    private string _treeSignature = "";

    private void RefreshTree()
    {
        // Not while a name is typed in it: built again, the box went from under the keys.
        if (_renaming is not null)
        {
            _treeSignature = "";
            return;
        }
        var root = new FilmNode(_board.Folder, isFolder: true, Path.GetFileName(_board.Folder)) { IsFilm = true };
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frameOf = _board.Frames.ToDictionary(f => Path.GetFullPath(_board.FramePicture(f)), f => _frames[f.Id], StringComparer.OrdinalIgnoreCase);

        // References: a folder for each reference, with its pictures, made or planned, and the user's own there.
        var references = Folder(_board.ReferencesFolder, "a folder for each reference: the pictures frames are made from");
        references.IsReferences = true;
        var referencesPath = Path.GetFullPath(_board.ReferencesFolder);
        var ownPictures = _board.ReferenceFiles();
        foreach (var dir in _board.Frames.Where(f => f.Reference).Select(f => Path.GetDirectoryName(Path.GetFullPath(_board.FramePicture(f)))!)
                     .Concat(Folders(referencesPath))
                     .Where(d => string.Equals(Path.GetDirectoryName(d), referencesPath, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList())
        {
            var reference = Folder(dir, "a reference");
            reference.References = _board.Frames.Where(f => f.Reference && string.Equals(Path.GetDirectoryName(Path.GetFullPath(_board.FramePicture(f))), dir, StringComparison.OrdinalIgnoreCase))
                .Select(f => _frames[f.Id]).ToList();
            ReferencePictures(reference);
            references.Children.Add(reference);
        }
        ReferencePictures(references);
        root.Children.Add(references);

        // A folder for each shot, in the film's order: the frames it starts and ends on (one another
        // shot's, where it is: the shot goes on from it), its clip, and what else is in it.
        foreach (var shot in Shots)
        {
            var folder = Folder(_board.ShotFolder(shot.Shot), $"{Lower(shot.Title)} · {shot.LengthText}");
            folder.Shot = shot;
            foreach (var (id, part) in new[] { (shot.Shot.First, "start"), (shot.Shot.LipSync ? null : shot.Shot.Last, "end") })
                if (_board.Frame(id) is { } frame && _frames.GetValueOrDefault(frame.Id) is { } item)
                {
                    var path = _board.FramePicture(frame);
                    var where = Path.GetDirectoryName(Path.GetFullPath(path))!;
                    // The frame of the shot before, which this one goes on from: in that shot's folder, as it says.
                    folder.Children.Add(string.Equals(where, Path.GetFullPath(folder.Path), StringComparison.OrdinalIgnoreCase)
                        ? PieceNode(path, item, $"the {part} of {Lower(shot.Title)}")
                        : new FilmNode(path, isFolder: false) { Label = $"the {part} of {Lower(shot.Title)}, in {Path.GetFileName(where)}", IsPlanned = !File.Exists(path) });
                }
            folder.Children.Add(PieceNode(_board.ShotVideo(shot.Shot), shot, $"{Lower(shot.Title)}'s clip"));
            Others(folder);
            root.Children.Add(folder);
        }

        var sound = Folder(Path.GetDirectoryName(_board.SongFile)!, "the soundtrack");
        sound.IsSound = true;
        if (_board.Sound.Source == FilmSound.Song)
            sound.Children.Add(PieceNode(_board.SongFile, Sound, "the song"));
        else if (_board.Sound.Source == FilmSound.FromFile && _board.Sound.File is { Length: > 0 } own)
            sound.Children.Add(PieceNode(own, Sound, "your sound file"));
        Others(sound);
        root.Children.Add(sound);

        // Any other folder (frames in no shot, or what the agent or the user put there), and the
        // film's own files: the film, joined, first.
        try
        {
            if (Directory.Exists(_board.Folder))
            {
                foreach (var dir in Directory.EnumerateDirectories(_board.Folder).Where(d => !shown.Contains(Path.GetFullPath(d)) && !Path.GetFileName(d).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var node = Folder(dir, "");
                    foreach (var (path, item) in frameOf.Where(f => string.Equals(Path.GetDirectoryName(f.Key), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)))
                        node.Children.Add(PieceNode(path, item, Lower(item.Title)));
                    Others(node);
                    root.Children.Add(node);
                }
                var joined = _board.JoinedFile;
                foreach (var file in Directory.EnumerateFiles(_board.Folder).Where(Shown).OrderBy(f => string.Equals(f, joined, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(f => f, StringComparer.OrdinalIgnoreCase))
                    root.Children.Add(new FilmNode(file, isFolder: false) { Label = string.Equals(file, joined, StringComparison.OrdinalIgnoreCase) ? "the film, joined" : "" });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // The other films, folders beside this one's; picked, one opens.
        var films = FilmNodes(root);

        // Built again only when what it shows changed: as a job writes, the folder changes every
        // second, and a tree built anew each time loses its scroll and the pointer's place.
        var signature = string.Concat(films.Select(Signature));
        if (signature == _treeSignature && Tree.Count > 0)
            return;
        _treeSignature = signature;
        // The film's folders open, and the references' in References.
        var opened = root.Children.Concat(references.Children).Where(n => n.IsFolder).ToList();
        if (_expanded.Count == 0)
            _expanded.UnionWith(opened.Select(n => n.Path).Prepend(root.Path));
        var selected = SelectedNode?.Path;
        var viewing = Viewing?.Path;
        foreach (var node in films.SelectMany(All))
        {
            // The open film's folder is open, and the folder of films it is in.
            node.IsExpanded = (_expanded.Contains(node.Path) && node.Children.Count > 0 || node == root || All(node).Contains(root)) && (node.IsFolder || node.Children.Count > 0);
            node.PropertyChanged += (sender, e) =>
            {
                if (e.PropertyName == nameof(FilmNode.IsExpanded) && sender is FilmNode n)
                    _ = n.IsExpanded ? _expanded.Add(n.Path) : _expanded.Remove(n.Path);
            };
        }
        // A shot or reference added since opens with its folder.
        foreach (var folder in opened.Where(n => !_seenFolders.Contains(n.Path)))
        {
            folder.IsExpanded = true;
            _expanded.Add(folder.Path);
        }
        _seenFolders.UnionWith(opened.Select(n => n.Path));
        _syncing = true;
        try
        {
            Tree.Clear();
            foreach (var film in films)
                Tree.Add(film);
            SelectedNode = selected is null ? null : Find(selected);
            if (viewing is not null)
                Viewing = Find(viewing) ?? (Viewing is { IsPlanned: false } gone && !File.Exists(gone.Path) && !Directory.Exists(gone.Path) ? null : Viewing);
        }
        finally
        {
            _syncing = false;
        }
        LoadThumbnails(root);

        FilmNode Folder(string path, string label)
        {
            shown.Add(Path.GetFullPath(path));
            return new FilmNode(path, isFolder: true) { Label = label };
        }

        // A piece made or planned, with its takes under it.
        FilmNode PieceNode(string path, IFilmPiece piece, string label)
        {
            shown.Add(Path.GetFullPath(path));
            var node = new FilmNode(path, isFolder: false) { Piece = piece, Label = label, IsPlanned = !File.Exists(path) };
            foreach (var take in FilmTakes.List(_board, piece.Piece))
                node.Children.Add(new FilmNode(take.File, isFolder: false) { Label = $"take {take.Number}", Take = take, TakeOf = piece });
            return node;
        }

        // A folder in References: the references made there, the user's pictures, and what else is in it.
        void ReferencePictures(FilmNode folder)
        {
            foreach (var frame in _board.Frames.Where(f => f.Reference && !shown.Contains(Path.GetFullPath(_board.FramePicture(f))) &&
                                                           string.Equals(Path.GetDirectoryName(Path.GetFullPath(_board.FramePicture(f))), Path.GetFullPath(folder.Path), StringComparison.OrdinalIgnoreCase)))
                folder.Children.Add(PieceNode(_board.FramePicture(frame), _frames[frame.Id], frame.Name is { Length: > 0 } name ? name : "a reference"));
            foreach (var file in ownPictures.Where(f => !shown.Contains(Path.GetFullPath(f)) && string.Equals(Path.GetDirectoryName(f), Path.GetFullPath(folder.Path), StringComparison.OrdinalIgnoreCase)))
            {
                shown.Add(Path.GetFullPath(file));
                folder.Children.Add(new FilmNode(file, isFolder: false) { Label = "your picture" });
            }
            Others(folder);
        }

        static IEnumerable<string> Folders(string dir)
        {
            try { return Directory.Exists(dir) ? Directory.EnumerateDirectories(dir).Where(d => !Path.GetFileName(d).StartsWith('.') && !Path.GetFileName(d).Equals(Storyboard.TakesName, StringComparison.OrdinalIgnoreCase)).Select(Path.GetFullPath).ToList() : []; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
        }

        // What else is in a folder: not a piece's own file, a meta, a workflow, a take or what goes with a shot.
        void Others(FilmNode folder)
        {
            try
            {
                if (!Directory.Exists(folder.Path))
                    return;
                foreach (var file in Directory.EnumerateFiles(folder.Path).Where(f => !shown.Contains(Path.GetFullPath(f)) && Shown(f)).Order(StringComparer.OrdinalIgnoreCase))
                    folder.Children.Add(new FilmNode(file, isFolder: false));
                foreach (var dir in Directory.EnumerateDirectories(folder.Path).Where(d => !Path.GetFileName(d).Equals(Storyboard.TakesName, StringComparison.OrdinalIgnoreCase) &&
                                                                                      !Path.GetFileName(d).StartsWith('.') && !shown.Contains(Path.GetFullPath(d))))
                {
                    var inner = Folder(dir, "");
                    Others(inner);
                    folder.Children.Add(inner);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        static string Signature(FilmNode node) =>
            $"{node.Path}|{node.Label}|{node.IsPlanned}|{(node.IsPlanned || node.IsFolder ? 0 : StampOf(node.Path))}[{string.Join(",", node.Children.Select(Signature))}]";
    }

    /// <summary>
    /// The films in the films folder by name, as Explorer lists them: a film's folder (the open
    /// one's with all in it), or a folder of films with theirs in it. The open film is there too
    /// before its folder is made.
    /// </summary>
    private List<FilmNode> FilmNodes(FilmNode open)
    {
        var placed = false;
        FilmNode Film(string dir)
        {
            if (!SamePath(dir, open.Path))
                return new FilmNode(dir, isFolder: true) { IsFilm = true };
            placed = true;
            return open;
        }
        var nodes = new List<FilmNode>();
        try
        {
            if (Directory.Exists(_filmsRoot))
                foreach (var dir in Directory.EnumerateDirectories(_filmsRoot).Where(d => !Path.GetFileName(d).StartsWith('.')).Order(ByName))
                {
                    if (Storyboard.IsFilm(dir) || SamePath(dir, open.Path))
                    {
                        nodes.Add(Film(dir));
                        continue;
                    }
                    var group = new FilmNode(dir, isFolder: true) { Label = "a folder of films" };
                    foreach (var film in Directory.EnumerateDirectories(dir).Where(d => Storyboard.IsFilm(d) || SamePath(d, open.Path)).Order(ByName))
                        group.Children.Add(Film(film));
                    if (group.Children.Count > 0)
                        nodes.Add(group);
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (!placed)
        {
            var at = nodes.FindIndex(n => ByName.Compare(n.Name, open.Name) > 0);
            nodes.Insert(at < 0 ? nodes.Count : at, open);
        }
        return nodes;
    }

    /// <summary>Names as Explorer orders them: "Film 2" before "Film 10".</summary>
    private static readonly StringComparer ByName =
        StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);

    /// <summary>The folders of the film's root and of References seen so far: one that comes (a new shot or reference) opens in the tree.</summary>
    private readonly HashSet<string> _seenFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A file of the film's folders shown in the tree: not a piece's .json or workflow (what they
    /// say shows under the viewer), nor what is made beside a piece (a lip-synced shot's sound and
    /// line, a take being made, a clip being held still) or hidden.
    /// </summary>
    private static bool Shown(string file)
    {
        var name = Path.GetFileName(file);
        return !name.StartsWith('.') && !Storyboard.IsWorkflow(file) && !Sidecar().IsMatch(name) &&
               !(name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && IsPieceJson(file));
    }

    /// <summary>
    /// A .json saying what a piece is: one beside a file of its name ("Intro.json", "Intro.mp4"), or
    /// one with a kind (a piece not made yet, the film's own).
    /// </summary>
    private static bool IsPieceJson(string file)
    {
        try
        {
            if (Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".*").Any(f => !string.Equals(f, file, StringComparison.OrdinalIgnoreCase)))
                return true;
            return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is System.Text.Json.Nodes.JsonObject { } json && json.ContainsKey("kind");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        {
            return false;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(\.(tmp|wav)$)|(\.line\.flac$)|(\.take\d+\.)|(\.held\.)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex Sidecar();

    private static long StampOf(string path)
    {
        try { return File.GetLastWriteTimeUtc(path).Ticks; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return 0; }
    }

    private static IEnumerable<FilmNode> All(FilmNode node) => node.Children.SelectMany(All).Prepend(node);

    private FilmNode? Find(string path) => Tree.SelectMany(All).FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase) && n.Take is null || n.Take is not null && string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));

    // ---- thumbnails of the pictures in the tree ------------------------------------------------------------

    private readonly Dictionary<string, (long Stamp, Bitmap Picture)> _thumbnails = new(StringComparer.OrdinalIgnoreCase);

    private void LoadThumbnails(FilmNode root)
    {
        DropThumbnails(keep: All(root).Where(n => n.Kind == "image" && !n.IsPlanned).Select(n => n.Path).ToList());
        foreach (var node in All(root).Where(n => n.Kind == "image" && !n.IsPlanned))
        {
            var stamp = StampOf(node.Path);
            if (_thumbnails.TryGetValue(node.Path, out var known) && known.Stamp == stamp)
            {
                node.Thumbnail = known.Picture;
                continue;
            }
            var path = node.Path;
            _ = Task.Run(() =>
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    var bitmap = Bitmap.DecodeToHeight(stream, 64, BitmapInterpolationMode.MediumQuality);
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (_disposing)
                        {
                            bitmap.Dispose();
                            return;
                        }
                        if (_thumbnails.Remove(path, out var old) && !ReferenceEquals(old.Picture, bitmap))
                            Later(old.Picture);
                        _thumbnails[path] = (stamp, bitmap);
                        foreach (var shown in Tree.SelectMany(All).Where(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase)))
                            shown.Thumbnail = bitmap;
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
            });
        }
    }

    /// <summary>Lets go of the thumbnails of pictures no longer in the tree.</summary>
    private void DropThumbnails(IReadOnlyCollection<string> keep)
    {
        foreach (var path in _thumbnails.Keys.Where(p => !keep.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _thumbnails.Remove(path, out var gone);
            Later(gone.Picture);
        }
    }

    /// <summary>A bitmap the tree may still draw this frame, let go of once it has moved on.</summary>
    private static void Later(Bitmap bitmap) => Dispatcher.UIThread.Post(bitmap.Dispose, DispatcherPriority.Background);

    // ---- what is picked, into the chat's box -----------------------------------------------------------------

    /// <summary>
    /// What is picked in the tree (or on the timeline): it goes into the chat's box, and the viewer
    /// shows it. Taken off the box (its ×), or sent, it is no longer picked; the viewer keeps it.
    /// </summary>
    [ObservableProperty]
    private FilmNode? _selectedNode;

    /// <summary>Set while the tree is built again or follows the box: the pick then doesn't go to the box again.</summary>
    private bool _syncing;

    partial void OnSelectedNodeChanged(FilmNode? value)
    {
        if (_syncing)
            return;
        // A film's folder: that film opens (the user's call: "opening another film should be about
        // just selecting the other folder"), and what is said is about all of it, so nothing goes in
        // the box. A folder of films is only a folder.
        if (value is { IsFilm: true } || value is { IsFolder: true } && !IsIn(value.Path, _board.Folder))
        {
            _chat.Pick([]);
            if (value.IsFilm && !SamePath(value.Path, _board.Folder))
                // Once the tree is done picking it: opened at once, the tree was built anew under its own pick.
                Dispatcher.UIThread.Post(() =>
                {
                    if (ReferenceEquals(SelectedNode, value) && !_disposing)
                        Open(value.Path);
                });
            else if (value.IsFilm)
                Viewing = null;
            return;
        }
        if (value is not null)
            Viewing = value;
        _chat.Pick(value is null ? [] : [(value.Path, value.Caption)]);
    }

    /// <summary>The open film's folder picked in the tree: nothing of it is in the box, and the viewer shows all of it.</summary>
    private void PickFilm()
    {
        _syncing = true;
        try { SelectedNode = Find(_board.Folder); }
        finally { _syncing = false; }
        Viewing = null;
    }

    /// <summary>A piece on the timeline clicked: picked, as in the tree, its folder opened there.</summary>
    public void Show(IFilmPiece? piece)
    {
        if (piece is null)
        {
            Clear(viewer: true);
            return;
        }
        var node = Tree.SelectMany(All).FirstOrDefault(n => ReferenceEquals(n.Piece, piece));
        if (node is null)
            return;
        foreach (var folder in Tree.SelectMany(All).Where(n => n.Children.Contains(node)))
            folder.IsExpanded = true;
        SelectedNode = node;
    }

    /// <summary>Nothing picked: what is said next is about the whole film. With <paramref name="viewer"/> the viewer shows the film too.</summary>
    public void Clear(bool viewer)
    {
        SelectedNode = null;
        if (viewer)
            Viewing = null;
    }

    /// <summary>The chat's box changed: a pick taken off it there is no longer picked here.</summary>
    private void OnBoxChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (SelectedNode is not { } picked || picked.IsFilm || _chat.Picked.Contains(picked.Path, StringComparer.OrdinalIgnoreCase))
            return;
        _syncing = true;
        try { SelectedNode = null; }
        finally { _syncing = false; }
    }

    /// <summary>The message went with what was picked: nothing is picked now; the viewer keeps showing it, as the chat makes it.</summary>
    public void Sent()
    {
        _syncing = true;
        try { SelectedNode = null; }
        finally { _syncing = false; }
    }

    /// <summary>
    /// Files dropped on the panel: pictures go into References (copies, under names of their own),
    /// to make frames from: into the reference picked in the tree, else each into a folder of its
    /// own named after it; the first is picked for the user to say what to do with it. Anything
    /// else goes with the next message.
    /// </summary>
    public bool Drop(IReadOnlyList<string> paths)
    {
        var pictures = paths.Where(p => File.Exists(p) && Storyboard.PictureExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)).ToList();
        if (pictures.Count == 0)
            return false;
        var added = new List<string>();
        try
        {
            SaveNow(force: true); // a new film's folder is made with it
            Directory.CreateDirectory(_board.ReferencesFolder);
            var into = PickedReference();
            foreach (var picture in pictures)
            {
                var folder = into ?? Path.Combine(_board.ReferencesFolder, Storyboard.Safe(Path.GetFileNameWithoutExtension(picture)));
                var target = Path.Combine(folder, Path.GetFileName(picture));
                if (string.Equals(Path.GetFullPath(picture), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    continue;
                for (var n = 2; File.Exists(target); n++)
                    target = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(picture)} ({n}){Path.GetExtension(picture)}");
                Directory.CreateDirectory(folder);
                File.Copy(picture, target);
                added.Add(target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowError($"The picture couldn't be put in References: {ex.Message}", ex);
        }
        _chat.Attach(paths.Except(pictures));
        Status = added.Count switch
        {
            0 => Status,
            1 => $"Put {Path.GetFileName(added[0])} in References: say in the chat what to make from it.",
            _ => $"Put {added.Count} pictures in References: say in the chat what to make from them.",
        };
        RefreshTree();
        if (added.Count > 0 && Find(added[0]) is { } first)
            SelectedNode = first;
        return true;
    }

    /// <summary>The folder of the reference picked in the tree (the folder, or a picture in it); null when none is.</summary>
    private string? PickedReference()
    {
        if (SelectedNode is not { } node)
            return null;
        var references = Path.GetFullPath(_board.ReferencesFolder);
        var folder = Path.GetFullPath(node.IsFolder ? node.Path : Path.GetDirectoryName(node.Path)!);
        return string.Equals(Path.GetDirectoryName(folder), references, StringComparison.OrdinalIgnoreCase) ? folder : null;
    }

    // ---- the viewer ---------------------------------------------------------------------------------------

    /// <summary>What the viewer shows: what was picked last (it stays after the message went), or the whole film.</summary>
    [ObservableProperty]
    private FilmNode? _viewing;

    partial void OnViewingChanged(FilmNode? value)
    {
        foreach (var piece in _frames.Values.Concat<IFilmPiece>(Shots).Append(Sound))
            piece.IsSelected = ReferenceEquals(piece, value?.Piece ?? value?.TakeOf);
        OnPropertyChanged(nameof(IsSongShown));
        OnPropertyChanged(nameof(IsTakeShown));
        RefreshViewer();
        RefreshDetails();
    }

    /// <summary>The song is shown: its row on the timeline is marked.</summary>
    public bool IsSongShown => Viewing?.Piece is SoundItem;

    /// <summary>A take is shown: it can be put back in the film.</summary>
    public bool IsTakeShown => Viewing?.Take is not null;

    /// <summary>The viewer's content: a picture, a clip or the film with its player, the song, or a card for what has none.</summary>
    [ObservableProperty]
    private object? _viewer;

    /// <summary>The player or picture in the viewer, and the file and its time it shows, so a file made again is shown again.</summary>
    private (string Path, long Stamp, IDisposable Shown)? _shown;

    private void RefreshViewer()
    {
        if (_disposing)
            return;
        var node = Viewing;
        var path = node?.Path ?? (HasFilm ? _board.JoinedFile : null);
        if (node is { IsFolder: true })
        {
            DropViewer();
            var count = node.Children.Count;
            Viewer = new PictureView(null, node.Name, (node.Label.Length > 0 ? Capital(node.Label) + ". " : "") +
                                                      (count == 0 ? "Nothing in it yet." : $"{count} in it.") + " Say in the chat what to do with it.");
            return;
        }
        if (path is not null && node is not { IsPlanned: true } && File.Exists(path))
        {
            var stamp = StampOf(path);
            if (_shown is { } shown && string.Equals(shown.Path, path, StringComparison.OrdinalIgnoreCase) && shown.Stamp == stamp)
                return;
            DropViewer();
            var kind = Core.Media.MediaPreview.KindOf(path);
            if (kind == "image")
            {
                var caption = node?.Caption ?? Path.GetFileName(path);
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
            Viewer = new PictureView(null, node?.Caption ?? Path.GetFileName(path), "Say in the chat what to do with it.");
            _shown = (path, stamp, new NoPicture());
            return;
        }
        DropViewer();
        Viewer = node switch
        {
            { Piece: ShotItem shot } => new PictureView(shot.First?.Thumbnail, $"{shot.Title} is not made yet", shot.Summary),
            { Piece: { } piece } => new PictureView(null, $"{piece.Title} is not made yet", "Say in the chat what it should be, or to make it."),
            null => new PictureView(null, "Whole film", _board.Shots.All(s => s.Prompt.Trim().Length == 0)
                ? "Say in the chat what film you want: BlackBox lays out its references, shots and sound here. Nothing is made until you ask for it."
                : "Pick a picture, clip or folder in the tree or on the timeline: it goes into the chat's box, where you say what to do with it. Or press Make the film."),
            _ => new PictureView(null, node.Caption, "It isn't there."),
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

    /// <summary>The services the film's previews play with, for the full-screen viewer.</summary>
    public PreviewServices Previews => _previews;

    /// <summary>
    /// The film's pictures and videos as the full-screen viewer goes through them, in the tree's
    /// order: the references, the frames, the shots' clips, then the joined film; only what is made.
    /// </summary>
    public List<MediaEntry> MediaInFilm() =>
        Tree.SelectMany(All).Where(n => n is { IsFolder: false, IsPlanned: false, Take: null } && MainWindowViewModel.Viewable(n.Path) && File.Exists(n.Path))
            .Select(n => new MediaEntry(n, n.Path, null, n.Caption)).ToList();

    public bool HasFilm => File.Exists(_board.JoinedFile);
}
