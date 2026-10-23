using System.Text;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Projects;

/// <summary>
/// Search of a folder's index: the parts of files that best match a query, as names
/// ("SettingsStore", "save_game"), words of an error message, or what the code does in plain words
/// ("where the settings are saved"). Ranked by BM25 over the parts' terms, the usual ranking of
/// text search, with a part that defines a name the query gives, or whose file's path has the
/// query's words, put first, and parts that have more of the query's words above parts that
/// repeat one.
/// </summary>
internal static class IndexSearch
{
    /// <summary>Parts shown at most, and from one file.</summary>
    internal const int MaxHits = 8, PerFile = 2;

    /// <summary>Lines shown of a part: those with the most of the query's words.</summary>
    private const int LinesPerHit = 6;

    /// <summary>The longest answer, about 2,500 tokens.</summary>
    private const int MaxChars = 9_000;

    private const double K1 = 1.2, B = 0.75;

    internal sealed record Hit(IndexedFile File, Chunk Chunk, double Score);

    /// <summary>The ranked parts of the files at or under <paramref name="under"/> (a path under the root with '/', or "" for all).</summary>
    internal static List<Hit> Rank(FolderIndex index, string query, string under)
    {
        var words = QueryWords(index, query);
        if (words.Count == 0)
            return [];
        var files = InScope(index, under).Where(f => f.Chunks.Length > 0).ToList();
        var chunks = files.Sum(f => f.Chunks.Length);
        if (chunks == 0)
            return [];
        var average = files.Sum(f => f.Chunks.Sum(c => (double)c.Length)) / chunks;

        // Each word's terms (the word whole, the stems of its parts) and how many parts have each.
        var terms = words.SelectMany(w => w.Ids).Distinct().ToArray();
        var df = new int[terms.Length];
        foreach (var f in files)
            foreach (var c in f.Chunks)
                for (var t = 0; t < terms.Length; t++)
                    if (c.Count(terms[t]) > 0)
                        df[t]++;
        var idf = new Dictionary<int, double>();
        for (var t = 0; t < terms.Length; t++)
            idf[terms[t]] = Math.Log(1 + (chunks - df[t] + 0.5) / (df[t] + 0.5));

        var asksForTests = words.Any(w => w.Word.StartsWith("test", StringComparison.Ordinal) || w.Word.StartsWith("spec", StringComparison.Ordinal));
        var hits = new List<Hit>();
        foreach (var f in files)
        {
            var pathTerms = new Dictionary<string, int>(StringComparer.Ordinal);
            Terms.Add(f.Path.Replace('/', ' ').Replace('.', ' '), pathTerms);
            foreach (var c in f.Chunks)
            {
                double score = 0;
                var matched = 0;
                foreach (var w in words)
                {
                    var best = 0.0;
                    foreach (var id in w.Ids)
                    {
                        var tf = c.Count(id);
                        if (tf == 0)
                            continue;
                        var s = idf[id] * tf * (K1 + 1) / (tf + K1 * (1 - B + B * c.Length / average));
                        best = Math.Max(best, s);
                    }
                    // A word counts once, by its best term: "SaveSettings" whole and its "save"
                    // and "settings" are one word, not three.
                    if (best > 0)
                    {
                        score += best;
                        matched++;
                    }
                }
                if (matched == 0)
                    continue;
                // Defines a name the query gives: the place someone asking for it means.
                foreach (var s in f.Symbols)
                {
                    if (s.Line < c.Start || s.Line > c.End)
                        continue;
                    var name = s.Name.ToLowerInvariant();
                    foreach (var w in words)
                        if (name == w.Word)
                            score += 3 * w.Ids.Max(id => idf[id]);
                        else if (w.Word.Length >= 4 && name.Contains(w.Word, StringComparison.Ordinal))
                            score += 0.5 * w.Ids.Max(id => idf[id]);
                }
                foreach (var w in words)
                    if (w.Terms.Any(pathTerms.ContainsKey))
                        score += 0.3 * w.Ids.Max(id => idf[id]);
                var coverage = (double)matched / words.Count;
                score *= 0.25 + 0.75 * coverage * coverage;
                // The code itself before its tests, unless tests are asked for: a test shows how
                // something is used, the code is what a change goes into.
                if (!asksForTests && IsTest(f.Path))
                    score *= 0.8;
                hits.Add(new Hit(f, c, score));
            }
        }
        hits.Sort((a, b) => b.Score.CompareTo(a.Score));
        return hits;
    }

    /// <summary>
    /// What a search with nothing found didn't look in: "no file has these words" read as "the code
    /// isn't there" when it was in node_modules, a folder .gitignore names or a file too big to read.
    /// </summary>
    private static string NotSearched(FolderIndex index, string under)
    {
        var prefix = under.Length == 0 ? "" : under.TrimEnd('/') + "/";
        var unread = index.Files.Values.Count(f => f.Kind is FileKind.Large or FileKind.Unread or FileKind.Locked &&
                                                   f.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var parts = new List<string>();
        if (index.LeftOut.Count > 0)
            parts.Add("what it leaves out (" + string.Join(", ", index.LeftOut.Keys.Take(6)) + (index.LeftOut.Count > 6 ? "…" : "") + ")");
        if (unread > 0)
            parts.Add($"{unread:N0} file(s) it didn't read (too big, in use or past its limits)");
        return parts.Count == 0 ? "" : $" The index doesn't search {string.Join(" or ", parts)}: if it may be there, search those with a command.";
    }

    /// <summary>The answer to a search, for the model: definitions of the names asked for, then the best parts with their best lines.</summary>
    internal static async Task<string> SearchAsync(FolderIndex index, string query, string under, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var scope = under.Length == 0 ? index.Root : index.FullPath(new IndexedFile { Path = under });
        var words = QueryWords(index, query);
        var asked = Terms.Words(query);
        if (asked.Count == 0)
            return "The query has no words to search for. Give names (SettingsStore, save_game), words of an error, or what the code does.";
        var nothing = $"No file in {scope} has any of these words: {string.Join(", ", asked)}. Try other words: the names the code may use, or what it does." +
                      NotSearched(index, under);
        if (words.Count == 0)
            return nothing;

        var defined = Definitions(index, words.Where(w => words.Count == 1 || Terms.IsName(query, w.Word)).ToList(), under);
        if (defined.Count > 0)
        {
            sb.AppendLine("Defined:");
            foreach (var (file, symbol) in defined.Take(8))
                sb.Append("- ").Append(symbol.Kind).Append(' ').Append(symbol.Name).Append(": ").Append(index.FullPath(file)).Append(':').Append(symbol.Line).AppendLine();
            if (defined.Count > 8)
                sb.AppendLine($"- and {defined.Count - 8} more definitions of the same names");
            sb.AppendLine();
        }

        var hits = Rank(index, query, under);
        if (hits.Count == 0)
            return sb.Append(nothing).ToString(); // the words of files gone since are still known
        var shown = new List<Hit>();
        var perFile = new Dictionary<IndexedFile, int>();
        var more = new List<IndexedFile>();
        foreach (var hit in hits)
        {
            var n = perFile.GetValueOrDefault(hit.File);
            if (shown.Count < MaxHits && n < PerFile)
            {
                shown.Add(hit);
                perFile[hit.File] = n + 1;
            }
            else if (!perFile.ContainsKey(hit.File) && !more.Contains(hit.File))
                more.Add(hit.File);
            if (shown.Count >= MaxHits && more.Count >= 10)
                break;
        }

        sb.AppendLine($"Best matches in {scope} for \"{query.Trim()}\":");
        var number = 0;
        foreach (var hit in shown)
        {
            ct.ThrowIfCancellationRequested();
            var block = new StringBuilder();
            block.Append(++number).Append(". ").Append(index.FullPath(hit.File)).Append(", lines ").Append(hit.Chunk.Start).Append('-').Append(hit.Chunk.End);
            if (Context(hit.File, hit.Chunk) is { } context)
                block.Append(" (").Append(context).Append(')');
            block.AppendLine();
            foreach (var (line, text) in await BestLinesAsync(index, hit, words, ct).ConfigureAwait(false))
                block.Append("   ").Append(line).Append(": ").AppendLine(text);
            if (sb.Length + block.Length > MaxChars)
                break;
            sb.Append(block);
        }
        if (more.Count > 0)
            sb.AppendLine("Also in: " + string.Join(", ", more.Take(10).Select(f => f.Path.Replace('/', Path.DirectorySeparatorChar))) + (more.Count > 10 ? ", …" : ""));
        sb.Append("read_file with offset at a line shows that part in full.");
        return sb.ToString();
    }

    internal sealed record QueryWord(string Word, string[] Terms, int[] Ids);

    /// <summary>A query's words, each with its terms that some file has; words no file has are left out.</summary>
    internal static List<QueryWord> QueryWords(FolderIndex index, string query)
    {
        var words = new List<QueryWord>();
        foreach (var word in Terms.Words(query))
        {
            var terms = Terms.Of(word).Distinct().ToArray();
            var ids = terms.Select(index.TermId).Where(id => id >= 0).Distinct().ToArray();
            if (ids.Length > 0)
                words.Add(new QueryWord(word.ToLowerInvariant(), terms, ids));
        }
        return words;
    }

    /// <summary>A test's file: in a tests folder, or named as one (FooTests.cs, test_foo.py, foo.test.ts, foo_spec.rb).</summary>
    internal static bool IsTest(string path)
    {
        var p = "/" + path.ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(p);
        return p.Contains("/test/") || p.Contains("/tests/") || p.Contains("/__tests__/") || p.Contains("/spec/") ||
               name.EndsWith("tests") || name.EndsWith("test") || name.StartsWith("test_") || name.EndsWith(".spec") || name.EndsWith("_spec");
    }

    private static IEnumerable<IndexedFile> InScope(FolderIndex index, string under) =>
        index.Files.Values.Where(f => under.Length == 0 || f.Path.Equals(under, StringComparison.OrdinalIgnoreCase) ||
                                      f.Path.StartsWith(under + "/", StringComparison.OrdinalIgnoreCase));

    /// <summary>The definitions named by the query's words, types first.</summary>
    private static List<(IndexedFile File, Symbol Symbol)> Definitions(FolderIndex index, List<QueryWord> words, string under)
    {
        var names = words.Select(w => w.Word).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return InScope(index, under).SelectMany(f => f.Symbols.Where(s => s.Kind != "heading" && names.Contains(s.Name)).Select(s => (f, s)))
            .OrderBy(d => d.s.IsType ? 0 : 1).ThenBy(d => d.f.Path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>What a part is in: the definitions it starts, or the ones it is inside ("class AgentSession › RunAsync").</summary>
    internal static string? Context(IndexedFile file, Chunk chunk)
    {
        var inside = file.Symbols.Where(s => s.Line >= chunk.Start && s.Line <= chunk.End).Take(3).Select(s => s.Kind == "heading" ? s.Name : $"{s.Kind} {s.Name}").ToList();
        if (inside.Count > 0)
            return string.Join(", ", inside);
        // The nearest definition above, and the type holding it.
        var above = file.Symbols.LastOrDefault(s => s.Line < chunk.Start);
        if (above == default)
            return null;
        var holder = file.Symbols.LastOrDefault(s => s.Line < above.Line && s.Depth < above.Depth && (s.IsType || s.Kind == "heading"));
        var name = above.Kind == "heading" ? above.Name : $"{above.Kind} {above.Name}";
        return holder == default ? "in " + name : $"in {(holder.Kind == "heading" ? holder.Name : $"{holder.Kind} {holder.Name}")} › {name}";
    }

    /// <summary>The lines of a part with the most of the query's words, in file order.</summary>
    private static async Task<List<(int Line, string Text)>> BestLinesAsync(FolderIndex index, Hit hit, List<QueryWord> words, CancellationToken ct)
    {
        var lines = await LinesAsync(index.FullPath(hit.File), hit.File, ct).ConfigureAwait(false);
        var scored = new List<(int Line, int Score)>();
        for (var i = hit.Chunk.Start; i <= Math.Min(hit.Chunk.End, lines.Count); i++)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            Terms.Add(lines[i - 1], counts);
            var score = words.Count(w => w.Terms.Any(counts.ContainsKey)) * 10 + (hit.File.Symbols.Any(s => s.Line == i) ? 5 : 0);
            if (score > 0)
                scored.Add((i, score));
        }
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Line).Take(LinesPerHit).OrderBy(s => s.Line)
            .Select(s => (s.Line, Shorten(lines[s.Line - 1].Trim()))).ToList();

        static string Shorten(string line) => line.Length > 200 ? line[..200] + " …" : line;
    }

    private static readonly Dictionary<string, (long Modified, IReadOnlyList<string> Lines)> Documents = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A file's lines as the index read them; a document's are kept a while, as reading a PDF again takes seconds.</summary>
    private static async Task<IReadOnlyList<string>> LinesAsync(string path, IndexedFile file, CancellationToken ct)
    {
        try
        {
            if (file.Kind == FileKind.Document)
            {
                lock (Documents)
                    if (Documents.TryGetValue(path, out var kept) && kept.Modified == file.Modified)
                        return kept.Lines;
                var lines = await Task.Run(() => DocumentText.Read(path, ct).ReplaceLineEndings("\n").Split('\n'), ct).ConfigureAwait(false);
                lock (Documents)
                {
                    if (Documents.Count > 8)
                        Documents.Clear();
                    Documents[path] = (file.Modified, lines);
                }
                return lines;
            }
            using var reader = TextFiles.OpenText(path);
            var all = new List<string>();
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
                all.Add(line);
            return all;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return [];
        }
    }
}
