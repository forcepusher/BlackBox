using System.Text.RegularExpressions;

namespace BlackBox.Core.Media;

/// <summary>The files a reply names by their full path, so the chat can preview them under it.</summary>
public static partial class FileMentions
{
    /// <summary>
    /// Existing files named in <paramref name="text"/> by a full path (C:\…, \\server\…, file:///…),
    /// in order, each once. A path may contain spaces: the longest stretch that names a file wins,
    /// without the punctuation or Markdown around it.
    /// </summary>
    public static List<string> Find(string text, int max = 8)
    {
        var found = new List<string>();
        if (string.IsNullOrEmpty(text) || !OperatingSystem.IsWindows())
            return found;
        foreach (Match m in PathStart().Matches(text))
        {
            if (found.Count >= max)
                break;
            var start = m.Index;
            var lineEnd = text.IndexOfAny(['\n', '\r'], start);
            var rest = text[start..(lineEnd < 0 ? text.Length : lineEnd)];
            if (rest.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                rest = Uri.UnescapeDataString(rest[(rest.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) ? 8 : 5)..]).Replace('/', '\\');
            // From the whole rest of the line down to the shortest stretch that ends a word.
            for (var end = rest.Length; end > 3; end--)
            {
                if (end < rest.Length && !IsBoundary(rest[end]))
                    continue;
                var candidate = rest[..end].TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '`', '*', '"', '\'', '>', ' ');
                if (candidate.Length < 4 || candidate.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
                    continue;
                try
                {
                    if (Exists(candidate))
                    {
                        var full = Path.GetFullPath(candidate);
                        if (!found.Contains(full, StringComparer.OrdinalIgnoreCase))
                            found.Add(full);
                        break;
                    }
                    if (Unanswered(candidate))
                        break;
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        return found;

        static bool IsBoundary(char c) => char.IsWhiteSpace(c) || c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '`' or '*' or '"' or '\'' or '<' or '>' or '|';
    }

    private static readonly TimeSpan SlowAnswer = TimeSpan.FromSeconds(2), SkipFor = TimeSpan.FromMinutes(2);

    /// <summary>Network roots (\\server\share, a mapped drive) that were slow to say a file isn't there, and when.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> SlowRoots = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a file is there, as File.Exists, but false at once on a network share that was slow
    /// to answer in the last two minutes: a server that is off (\\nas\…, a mapped drive whose
    /// server is gone) holds File.Exists up for half a minute each time, and a reply naming a path
    /// there was looked up once for every word its line had.
    /// </summary>
    public static bool Exists(string path)
    {
        if (Unanswered(path))
            return false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var exists = File.Exists(path);
        if (!exists && clock.Elapsed > SlowAnswer && NetworkRoot(path) is { } root)
            SlowRoots[root] = DateTime.UtcNow;
        return exists;
    }

    private static bool Unanswered(string path) =>
        !SlowRoots.IsEmpty && NetworkRoot(path) is { } root && SlowRoots.TryGetValue(root, out var since) && DateTime.UtcNow - since < SkipFor;

    private static string? NetworkRoot(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root))
                return null;
            return root.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(root).DriveType == DriveType.Network ? root : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?<![\w\\])(?:[A-Za-z]:\\|\\\\[\w.$-]+\\|file:///?[A-Za-z]:/)", RegexOptions.IgnoreCase)]
    private static partial Regex PathStart();
}
