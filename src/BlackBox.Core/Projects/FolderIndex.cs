using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Projects;

/// <summary>What became of a file in the index.</summary>
internal enum FileKind : byte
{
    /// <summary>Read: its lines, words and definitions are in the index.</summary>
    Text,
    /// <summary>A PDF or Office document, read as read_file reads it.</summary>
    Document,
    /// <summary>A picture, a sound, a program…: counted, not read.</summary>
    Binary,
    /// <summary>Text too big to be worth reading whole.</summary>
    Large,
    /// <summary>Made by a tool (a lock file, minified code, a Unity scene's YAML): noise in a search.</summary>
    Generated,
    /// <summary>Past the most files one index reads.</summary>
    Unread,
    /// <summary>Couldn't be opened (in use, not allowed): read at the next refresh.</summary>
    Locked,
}

/// <summary>A part of a file, about a screen of lines, with the terms in it (see <see cref="Terms"/>).</summary>
/// <param name="Ids">Term ids, ascending.</param>
/// <param name="Counts">How often each of <paramref name="Ids"/> occurs.</param>
/// <param name="Length">All the terms in it, counted.</param>
internal sealed record Chunk(int Start, int End, int[] Ids, ushort[] Counts, int Length)
{
    /// <summary>How often the term occurs here; 0 when it doesn't.</summary>
    public int Count(int id)
    {
        var at = Array.BinarySearch(Ids, id);
        return at >= 0 ? Counts[at] : 0;
    }
}

internal sealed class IndexedFile
{
    /// <summary>Under the root, with '/'.</summary>
    public required string Path { get; init; }
    public long Size { get; init; }
    public long Modified { get; init; }
    public FileKind Kind { get; init; }
    public string? Language { get; init; }
    public int Lines { get; init; }
    public Symbol[] Symbols { get; init; } = [];
    public Chunk[] Chunks { get; init; } = [];
}

/// <summary>What a refresh found changed since the index was last brought up to date.</summary>
internal sealed record IndexChanges(int Added, int Changed, int Removed, TimeSpan Took, bool Built)
{
    public int Total => Added + Changed + Removed;
}

/// <summary>
/// A folder's index, as a code editor keeps one: every file of the project (what .gitignore and
/// the tools' own folders leave out is left out), the definitions in each, and the words of each
/// part of a file, for search (see <see cref="IndexSearch"/>) and the overview (see
/// <see cref="ProjectOverview"/>). It lives in BlackBox's Data/index, a file per folder, and is
/// brought up to date on every use: a file whose size and time are unchanged isn't read again.
/// <para>
/// Words, not embeddings: Cursor ranks a project's parts by embeddings a model of its own makes,
/// but here the model's server may have no embedding model (Kilo's free models, Anthropic's API),
/// and on the user's own GPU it would load a second model next to the one doing the work. Names
/// split into their words ("SaveSettings" is "save settings") and words without their endings find
/// what plain-language questions ask about; see docs/notes.md for how well.
/// </para>
/// </summary>
internal sealed class FolderIndex
{
    /// <summary>The most files one index reads; the rest are counted, and the overview says to index the part worked on.</summary>
    internal const int MaxFiles = 20_000;

    /// <summary>The most text one index reads, in all.</summary>
    internal const long MaxText = 150_000_000;

    /// <summary>The biggest text file read; bigger ones are logs, data or generated code.</summary>
    internal const long MaxTextFile = 1_000_000;

    /// <summary>The biggest PDF or Office document read.</summary>
    internal const long MaxDocument = 30_000_000;

    /// <summary>Lines in a part of a file at most, and before it may end at a definition.</summary>
    internal const int MaxChunk = 60, MinChunk = 20;

    /// <summary>Bump when the file's layout or what goes in it changes: an index of another version is built again.</summary>
    private const int Version = 1;

    public string Root { get; }

    /// <summary>When the folder was last walked.</summary>
    public DateTime Updated { get; private set; }

    internal Dictionary<string, IndexedFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the last walk left out, by why (see <see cref="FolderWalk.LeftOut"/>).</summary>
    internal SortedDictionary<string, int> LeftOut { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the last walk stopped at <see cref="FolderWalk.MaxEntries"/>.</summary>
    internal bool WalkStopped { get; private set; }

    private readonly List<string> _terms = [];
    private readonly Dictionary<string, int> _termIds = new(StringComparer.Ordinal);

    private FolderIndex(string root) => Root = root;

    /// <summary>The id of a term, or -1 when no file has it.</summary>
    internal int TermId(string term)
    {
        lock (_termIds)
            return _termIds.GetValueOrDefault(term, -1);
    }

    internal string TermName(int id) => _terms[id];

    private int Intern(string term)
    {
        if (_termIds.TryGetValue(term, out var id))
            return id;
        _termIds[term] = id = _terms.Count;
        _terms.Add(term);
        return id;
    }

    /// <summary>The full path of a file the index has.</summary>
    internal string FullPath(IndexedFile file) => System.IO.Path.Combine(Root, file.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    // --- keeping it up to date ---------------------------------------------------------------

    /// <summary>Walks the folder and reads what is new or changed since the last time; says what changed.</summary>
    /// <param name="store">The folder the index files are kept in, left out when it is inside this one: each save would change it.</param>
    internal async Task<IndexChanges> RefreshAsync(Action<string>? progress, CancellationToken ct, string? store = null)
    {
        var watch = Stopwatch.StartNew();
        var built = Files.Count == 0;
        var (walked, walk) = await Task.Run(() => FolderWalk.Run(Root, ct, store), ct).ConfigureAwait(false);
        LeftOut = walk.LeftOut;
        WalkStopped = walk.Stopped;

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toRead = new List<WalkedFile>();
        foreach (var f in walked)
        {
            present.Add(f.Path);
            // A file left unread at the limits, or that couldn't be opened, is tried again.
            if (!Files.TryGetValue(f.Path, out var known) || known.Size != f.Size || known.Modified != f.Modified || known.Kind is FileKind.Unread or FileKind.Locked)
                toRead.Add(f);
        }
        var removed = Files.Keys.Where(p => !present.Contains(p)).ToList();
        foreach (var p in removed)
            Files.Remove(p);

        int added = 0, changed = 0, done = 0;
        // What is read again doesn't count twice against the limits: a project at the limit left
        // every file it changed unread.
        var again = toRead.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var kept = Files.Values.Where(f => f.Kind is FileKind.Text or FileKind.Document && !again.Contains(f.Path)).ToList();
        long text = kept.Sum(f => f.Size);
        var read = kept.Count;
        var lastSaid = Stopwatch.StartNew();
        // Smaller first: past the limits, the files left unread are the biggest.
        toRead.Sort((a, b) => a.Size.CompareTo(b.Size));
        await Parallel.ForEachAsync(toRead, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 6), CancellationToken = ct },
            (f, token) =>
            {
                var full = System.IO.Path.Combine(Root, f.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                bool overLimit;
                lock (Files)
                    overLimit = read >= MaxFiles || text + f.Size > MaxText;
                var file = overLimit ? Plain(f, FileKind.Unread) : Read(f, full, token);
                lock (Files)
                {
                    if (!Files.TryGetValue(f.Path, out var before)) added++;
                    else if (before.Kind != file.Kind || file.Kind is not (FileKind.Unread or FileKind.Locked) || before.Size != f.Size || before.Modified != f.Modified) changed++;
                    Files[f.Path] = file;
                    if (file.Kind is FileKind.Text or FileKind.Document)
                    {
                        read++;
                        text += f.Size;
                    }
                    done++;
                    if (progress is not null && lastSaid.ElapsedMilliseconds > 1000)
                    {
                        lastSaid.Restart();
                        progress($"Reading files: {done:N0} of {toRead.Count:N0}");
                    }
                }
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);
        Updated = DateTime.UtcNow;
        return new IndexChanges(added, changed, removed.Count, watch.Elapsed, built);
    }

    private static IndexedFile Plain(WalkedFile f, FileKind kind, string? language = null) =>
        new() { Path = f.Path, Size = f.Size, Modified = f.Modified, Kind = kind, Language = language };

    /// <summary>A file read into the index: its definitions and the terms of each of its parts.</summary>
    private IndexedFile Read(WalkedFile f, string full, CancellationToken ct)
    {
        var language = Symbols.Language(f.Path);
        if (IsGenerated(f.Path))
            return Plain(f, FileKind.Generated, language);
        IReadOnlyList<string> lines;
        var kind = FileKind.Text;
        try
        {
            if (DocumentText.Handles(full))
            {
                if (f.Size > MaxDocument)
                    return Plain(f, FileKind.Large, language);
                // As read_file reads it, so the line numbers search gives are read_file's.
                lines = DocumentText.Read(full, ct).ReplaceLineEndings("\n").Split('\n');
                kind = FileKind.Document;
            }
            else if (IsBinaryName(f.Path))
                return Plain(f, FileKind.Binary, language);
            else if (f.Size > MaxTextFile)
                return Plain(f, FileKind.Large, language);
            else
            {
                if (LooksBinary(full))
                    return Plain(f, FileKind.Binary, language);
                using var reader = TextFiles.OpenText(full);
                var all = new List<string>();
                while (reader.ReadLine() is { } line)
                    all.Add(line);
                lines = all;
                // Minified: a few lines of thousands of characters.
                if (lines.Count > 0 && f.Size / lines.Count > 1_000 && f.Size > 20_000)
                    return Plain(f, FileKind.Generated, language);
            }
        }
        catch (OperationCanceledException) { throw; }
        // In use by a program that shares it with no one, or not allowed: tried again next time.
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Plain(f, FileKind.Locked, language);
        }
        catch (Exception) // not what its name says (a broken PDF): counted, not read
        {
            return Plain(f, FileKind.Binary, language);
        }

        var symbols = Projects.Symbols.Find(language, lines);
        var chunks = new List<Chunk>();
        foreach (var (start, end) in Spans(lines, symbols))
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = start; i <= end; i++)
                Terms.Add(lines[i - 1], counts);
            if (counts.Count == 0)
                continue;
            var ids = new int[counts.Count];
            var n = new ushort[counts.Count];
            var k = 0;
            lock (_termIds)
                foreach (var (term, count) in counts)
                {
                    ids[k] = Intern(term);
                    n[k++] = (ushort)Math.Min(count, ushort.MaxValue);
                }
            Array.Sort(ids, n);
            chunks.Add(new Chunk(start, end, ids, n, counts.Values.Sum()));
        }
        return new IndexedFile
        {
            Path = f.Path, Size = f.Size, Modified = f.Modified, Kind = kind, Language = language,
            Lines = lines.Count, Symbols = symbols.ToArray(), Chunks = chunks.ToArray(),
        };
    }

    /// <summary>
    /// The parts a file is cut into: about a screen each, ending before a definition once one has
    /// <see cref="MinChunk"/> lines, so a part is mostly one function and the comment above it
    /// goes with it.
    /// </summary>
    internal static List<(int Start, int End)> Spans(IReadOnlyList<string> lines, IReadOnlyList<Symbol> symbols)
    {
        var starts = new HashSet<int>();
        foreach (var s in symbols)
        {
            // The comments and attributes above a definition belong with it.
            var line = s.Line;
            while (line > 1 && s.Line - line < 12 && IsPreamble(lines[line - 2]))
                line--;
            starts.Add(line);
        }
        var spans = new List<(int, int)>();
        var start = 1;
        for (var line = 2; line <= lines.Count; line++)
        {
            var size = line - start;
            if (size >= MaxChunk || size >= MinChunk && starts.Contains(line) || size >= MinChunk * 2 && lines[line - 2].Trim().Length == 0)
            {
                spans.Add((start, line - 1));
                start = line;
            }
        }
        if (lines.Count > 0)
            spans.Add((start, lines.Count));
        return spans;

        static bool IsPreamble(string line)
        {
            var t = line.TrimStart();
            return t.StartsWith("//") || t.StartsWith("/*") || t.StartsWith('*') || t.StartsWith('#') && !t.StartsWith("#region") ||
                   t.StartsWith('[') || t.StartsWith('@') || t.StartsWith("--") || t.StartsWith("'''") || t.StartsWith("\"\"\"");
        }
    }

    /// <summary>Files a tool makes and nobody reads: lock files, minified code and source maps, a Unity scene's or prefab's YAML.</summary>
    internal static bool IsGenerated(string path)
    {
        var name = System.IO.Path.GetFileName(path).ToLowerInvariant();
        if (name is "package-lock.json" or "yarn.lock" or "pnpm-lock.yaml" or "cargo.lock" or "poetry.lock" or "composer.lock" or "gemfile.lock"
            or "pipfile.lock" or "packages.lock.json" or "go.sum" or "bun.lockb" or "flake.lock" or "uv.lock")
            return true;
        if (name.EndsWith(".min.js") || name.EndsWith(".min.css") || name.EndsWith(".map"))
            return true;
        return System.IO.Path.GetExtension(name) is ".meta" or ".unity" or ".prefab" or ".asset" or ".mat" or ".controller" or ".anim" or ".physicmaterial"
            or ".mask" or ".overridecontroller" or ".playable" or ".lighting" or ".terrainlayer" or ".spriteatlas" or ".shadervariants" or ".mixer"
            or ".preset" or ".signal" or ".brush" or ".guiskin" or ".flare" or ".rendertexture" or ".cubemap" or ".fontsettings" or ".giparams"
            or ".uasset" or ".umap";
    }

    /// <summary>Extensions that are never text: pictures, sound, video, archives, programs, fonts, 3D models.</summary>
    internal static bool IsBinaryName(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() is
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".ico" or ".tga" or ".tif" or ".tiff" or ".psd" or ".exr" or ".hdr" or ".dds" or ".ktx"
        or ".wav" or ".mp3" or ".ogg" or ".flac" or ".aac" or ".m4a" or ".wma" or ".mp4" or ".mov" or ".avi" or ".mkv" or ".webm" or ".wmv"
        or ".zip" or ".7z" or ".rar" or ".gz" or ".tgz" or ".bz2" or ".xz" or ".tar" or ".nupkg" or ".unitypackage" or ".pak" or ".jar" or ".war"
        or ".exe" or ".dll" or ".pdb" or ".so" or ".dylib" or ".lib" or ".a" or ".o" or ".obj" or ".class" or ".pyc" or ".wasm" or ".bin" or ".dat"
        or ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" or ".fbx" or ".blend" or ".glb" or ".gltf" or ".stl" or ".ply" or ".3ds" or ".max" or ".ma" or ".mb"
        or ".safetensors" or ".ckpt" or ".pt" or ".pth" or ".onnx" or ".gguf" or ".db" or ".sqlite" or ".mdb" or ".accdb" or ".doc" or ".xls" or ".ppt"
        or ".msi" or ".iso" or ".img" or ".cab" or ".apk" or ".ipa" or ".pck" or ".res" or ".resources";

    /// <summary>A zero byte in the first 8 KB, outside UTF-16 text.</summary>
    private static bool LooksBinary(string path)
    {
        Span<byte> head = stackalloc byte[8000];
        int n;
        using (var stream = TextFiles.OpenRead(path))
            n = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        head = head[..n];
        return !head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) && !head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) && head.Contains((byte)0);
    }

    // --- the indexes in use ------------------------------------------------------------------

    private static readonly Dictionary<string, FolderIndex> Loaded = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<string> Recent = new();

    /// <summary>Indexes kept in memory at once; older ones are read from disk again when used.</summary>
    private const int KeptLoaded = 6;

    /// <summary>
    /// The index of <paramref name="root"/>, brought up to date: from memory, from its file in
    /// <paramref name="store"/> (Data/index), or built now. With <paramref name="store"/> null it
    /// lives in memory only. Only for one user of it at a time (tests): BlackBox's windows share
    /// an index, so they go through <see cref="UseAsync{T}"/>.
    /// </summary>
    internal static Task<(FolderIndex Index, IndexChanges Changes)> OpenAsync(string root, string? store, Action<string>? progress, CancellationToken ct) =>
        UseAsync(root, store, progress, (index, changes) => Task.FromResult((index, changes)), ct);

    /// <summary>
    /// <paramref name="use"/> with the index of <paramref name="root"/>, brought up to date (see
    /// <see cref="OpenAsync"/>). Several windows' agents share an index: while one brings it up to
    /// date or reads it, another waits, as a search going through its files while another window
    /// changed them would fail.
    /// </summary>
    internal static async Task<T> UseAsync<T>(string root, string? store, Action<string>? progress, Func<FolderIndex, IndexChanges, Task<T>> use, CancellationToken ct)
    {
        root = Normalize(root);
        SemaphoreSlim gate;
        lock (Locks)
        {
            if (!Locks.TryGetValue(root, out gate!))
                Locks[root] = gate = new SemaphoreSlim(1, 1);
        }
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            FolderIndex? index;
            lock (Loaded)
                Loaded.TryGetValue(root, out index);
            var file = store is null ? null : StorePath(store, root);
            if (index is null && file is not null && File.Exists(file))
                index = await Task.Run(() => Load(file, root), ct).ConfigureAwait(false);
            index ??= new FolderIndex(root);
            var changes = await index.RefreshAsync(progress, ct, store).ConfigureAwait(false);
            if (file is not null && (changes.Total > 0 || !File.Exists(file)))
            {
                try { await Task.Run(() => index.Save(file), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Remember(root, index);
            return await use(index, changes).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The folder at or above <paramref name="path"/> that has an index, in memory or in
    /// <paramref name="store"/>; null when none does. No higher than the project the path is in,
    /// and never the home folder or a drive: an index of the whole home folder, made once for a
    /// file in no project, stops short at its limits, and searches of every project under it
    /// would have gone to it.
    /// </summary>
    internal static string? IndexedAbove(string path, string? store)
    {
        var project = ProjectRoot.Find(path)?.Root;
        var home = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        for (var dir = Normalize(path); dir is not null; dir = System.IO.Path.GetDirectoryName(dir))
        {
            if (System.IO.Path.GetPathRoot(dir) == dir || dir.Equals(home, StringComparison.OrdinalIgnoreCase))
                break;
            lock (Loaded)
                if (Loaded.ContainsKey(dir))
                    return dir;
            if (store is not null && File.Exists(StorePath(store, dir)))
                return dir;
            if (dir.Equals(project, StringComparison.OrdinalIgnoreCase))
                break;
        }
        return null;
    }

    private static void Remember(string root, FolderIndex index)
    {
        lock (Loaded)
        {
            Loaded[root] = index;
            // The same folder written in other letter case is the same index.
            for (var node = Recent.First; node is not null; node = node.Next)
                if (node.Value.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    Recent.Remove(node);
                    break;
                }
            Recent.AddFirst(root);
            while (Recent.Count > KeptLoaded)
            {
                Loaded.Remove(Recent.Last!.Value);
                Recent.RemoveLast();
            }
        }
    }

    /// <summary>Forgets the indexes in memory (tests: an index read back from its file).</summary>
    internal static void ForgetLoaded()
    {
        lock (Loaded)
        {
            Loaded.Clear();
            Recent.Clear();
        }
    }

    internal static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(full);
        return full.Length > (root?.Length ?? 0) ? full.TrimEnd('\\', '/') : full;
    }

    /// <summary>The index file of a folder: its name, for a person looking in Data/index, and a hash of its path.</summary>
    internal static string StorePath(string store, string root)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToLowerInvariant())))[..12].ToLowerInvariant();
        var name = new string(System.IO.Path.GetFileName(root).Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(40).ToArray());
        return System.IO.Path.Combine(store, (name.Length > 0 ? name : "drive") + "-" + hash + ".index");
    }

    // --- on disk -----------------------------------------------------------------------------

    private void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        // Only the terms some file still has, renumbered: a refresh leaves the dropped ones behind.
        var used = new SortedSet<int>(Files.Values.SelectMany(f => f.Chunks).SelectMany(c => c.Ids));
        var renumber = new Dictionary<int, int>(used.Count);
        foreach (var id in used)
            renumber[id] = renumber.Count;

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new GZipStream(stream, CompressionLevel.Fastest))
        using (var w = new BinaryWriter(zip, Encoding.UTF8))
        {
            w.Write("BBIX");
            w.Write(Version);
            w.Write(Root);
            w.Write(Updated.Ticks);
            w.Write(used.Count);
            foreach (var id in used)
                w.Write(_terms[id]);
            w.Write(LeftOut.Count);
            foreach (var (why, count) in LeftOut)
            {
                w.Write(why);
                w.Write(count);
            }
            w.Write(WalkStopped);
            w.Write(Files.Count);
            foreach (var f in Files.Values)
            {
                w.Write(f.Path);
                w.Write(f.Size);
                w.Write(f.Modified);
                w.Write((byte)f.Kind);
                w.Write(f.Language ?? "");
                w.Write7BitEncodedInt(f.Lines);
                w.Write7BitEncodedInt(f.Symbols.Length);
                foreach (var s in f.Symbols)
                {
                    w.Write(s.Name);
                    w.Write(s.Kind);
                    w.Write7BitEncodedInt(s.Line);
                    w.Write7BitEncodedInt(s.Depth);
                }
                w.Write7BitEncodedInt(f.Chunks.Length);
                foreach (var c in f.Chunks)
                {
                    w.Write7BitEncodedInt(c.Start);
                    w.Write7BitEncodedInt(c.End);
                    w.Write7BitEncodedInt(c.Length);
                    w.Write7BitEncodedInt(c.Ids.Length);
                    // Ascending, so the gaps are small numbers.
                    var previous = 0;
                    for (var i = 0; i < c.Ids.Length; i++)
                    {
                        var id = renumber[c.Ids[i]];
                        w.Write7BitEncodedInt(id - previous);
                        previous = id;
                        w.Write7BitEncodedInt(c.Counts[i]);
                    }
                }
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>The index in <paramref name="path"/>, or a new one when it can't be read or is of another version.</summary>
    private static FolderIndex Load(string path, string root)
    {
        var index = new FolderIndex(root);
        try
        {
            using var stream = TextFiles.OpenRead(path);
            using var zip = new GZipStream(stream, CompressionMode.Decompress);
            using var r = new BinaryReader(zip, Encoding.UTF8);
            if (r.ReadString() != "BBIX" || r.ReadInt32() != Version || !string.Equals(r.ReadString(), root, StringComparison.OrdinalIgnoreCase))
                return new FolderIndex(root);
            index.Updated = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
            var terms = r.ReadInt32();
            for (var i = 0; i < terms; i++)
                index.Intern(r.ReadString());
            var leftOut = r.ReadInt32();
            for (var i = 0; i < leftOut; i++)
                index.LeftOut[r.ReadString()] = r.ReadInt32();
            index.WalkStopped = r.ReadBoolean();
            var files = r.ReadInt32();
            for (var i = 0; i < files; i++)
            {
                var (p, size, modified, kind, language, lines) = (r.ReadString(), r.ReadInt64(), r.ReadInt64(), (FileKind)r.ReadByte(), r.ReadString(), r.Read7BitEncodedInt());
                var symbols = new Symbol[r.Read7BitEncodedInt()];
                for (var s = 0; s < symbols.Length; s++)
                    symbols[s] = new Symbol(r.ReadString(), r.ReadString(), r.Read7BitEncodedInt(), r.Read7BitEncodedInt());
                var chunks = new Chunk[r.Read7BitEncodedInt()];
                for (var c = 0; c < chunks.Length; c++)
                {
                    var (start, end, length) = (r.Read7BitEncodedInt(), r.Read7BitEncodedInt(), r.Read7BitEncodedInt());
                    var ids = new int[r.Read7BitEncodedInt()];
                    var counts = new ushort[ids.Length];
                    var previous = 0;
                    for (var k = 0; k < ids.Length; k++)
                    {
                        ids[k] = previous += r.Read7BitEncodedInt();
                        counts[k] = (ushort)r.Read7BitEncodedInt();
                    }
                    chunks[c] = new Chunk(start, end, ids, counts, length);
                }
                index.Files[p] = new IndexedFile
                {
                    Path = p, Size = size, Modified = modified, Kind = kind, Language = language.Length > 0 ? language : null,
                    Lines = lines, Symbols = symbols, Chunks = chunks,
                };
            }
            return index;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or UnauthorizedAccessException or ArgumentException or FormatException)
        {
            return new FolderIndex(root);
        }
    }
}
