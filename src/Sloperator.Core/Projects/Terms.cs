using System.Text.RegularExpressions;

namespace Sloperator.Core.Projects;

/// <summary>
/// The words search goes by. A name is a word whole and in its parts, so "SaveSettings" is found
/// by "save settings" and by "SaveSettings", and settings_path by "settings". Words lose their
/// endings ("saved", "saving" and "saves" are "sav"), so a question in plain words finds the
/// code it is about.
/// </summary>
internal static partial class Terms
{
    /// <summary>The longest part of a line that is read for words: a minified line is mostly noise.</summary>
    private const int MaxLine = 2_000;

    /// <summary>The terms of one line, each as often as it occurs.</summary>
    public static void Add(string line, Dictionary<string, int> counts)
    {
        if (line.Length > MaxLine)
            line = line[..MaxLine];
        // Every word, "the" and "find" too: a method named Find is searched for by its name.
        foreach (Match m in Word().Matches(line))
            foreach (var term in Of(m.Value))
                counts[term] = counts.GetValueOrDefault(term) + 1;
    }

    /// <summary>The distinct terms of a query's words (see <see cref="Words"/>), in the order they come.</summary>
    public static List<string> OfQuery(string query) => Words(query).SelectMany(Of).Distinct().ToList();

    /// <summary>
    /// The words of a query as written, the ones that say nothing left out ("where", "the"), unless
    /// the query is little else or one is written as a name (a capital past the first word): a
    /// query of "Find" is for the method Find. As written, as "MouseClick" is two words of a name.
    /// </summary>
    public static List<string> Words(string query)
    {
        var all = Word().Matches(query).Select(m => m.Value).Where(w => w.Length >= 2).ToList();
        return all.Where((w, i) => all.Count <= 2 || !Stopwords.Contains(w.ToLowerInvariant()) || i > 0 && w.Any(char.IsUpper))
            .DistinctBy(w => w.ToLowerInvariant()).ToList();
    }

    /// <summary>
    /// Whether <paramref name="word"/> (lower-cased) is written in <paramref name="query"/> as code
    /// writes a name (SettingsStore, save_game, md5): a plain "settings" in a question isn't the
    /// property Settings.
    /// </summary>
    public static bool IsName(string query, string word)
    {
        foreach (Match m in Word().Matches(query))
            if (m.Value.Equals(word, StringComparison.OrdinalIgnoreCase) &&
                (Parts(m.Value).Count > 1 || m.Value.Contains('_') || m.Value.Skip(1).Any(char.IsUpper)))
                return true;
        return false;
    }

    /// <summary>A word's terms: itself, lower-cased, and the stems of its parts.</summary>
    internal static IEnumerable<string> Of(string word)
    {
        var whole = word.ToLowerInvariant();
        if (whole.Length < 2 || whole.Length > 64)
            yield break;
        yield return whole;
        var parts = Parts(word);
        if (parts.Count == 1)
        {
            var stem = Stem(whole);
            if (stem != whole)
                yield return stem;
            yield break;
        }
        foreach (var part in parts)
        {
            var p = part.ToLowerInvariant();
            if (p.Length < 2 || Stopwords.Contains(p))
                continue;
            yield return Stem(p);
        }
    }

    /// <summary>The parts of a name: SaveSettings, save_settings and HTTPServer2 split where a person reads a new word.</summary>
    internal static List<string> Parts(string word)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 1; i <= word.Length; i++)
        {
            var split = i == word.Length || word[i] == '_' ||
                char.IsLower(word[i - 1]) && char.IsUpper(word[i]) ||
                char.IsUpper(word[i - 1]) && char.IsUpper(word[i]) && i + 1 < word.Length && char.IsLower(word[i + 1]) ||
                char.IsLetter(word[i - 1]) != char.IsLetter(word[i]) && word[i - 1] != '_';
            if (!split)
                continue;
            if (i > start && word[start] != '_')
                parts.Add(word[start..i]);
            start = i < word.Length && word[i] == '_' ? i + 1 : i;
        }
        return parts;
    }

    /// <summary>
    /// A word without its ending, enough for "settings", "setting" and "set", or "saved", "saving"
    /// and "save", to be one word: not a real stemmer, which a search over names doesn't need.
    /// </summary>
    internal static string Stem(string w)
    {
        if (w.Length <= 3 || !w.All(char.IsAsciiLetterLower))
            return w;
        if (w.EndsWith("ies") && w.Length > 4) w = w[..^3] + "y";
        else if (w.EndsWith("sses")) w = w[..^2];
        else if (w.EndsWith("es") && (w.EndsWith("xes") || w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("zes"))) w = w[..^2];
        else if (w.EndsWith('s') && !w.EndsWith("ss") && !w.EndsWith("us") && !w.EndsWith("is")) w = w[..^1];

        if (w.EndsWith("ing") && w.Length >= 6) w = Undouble(w[..^3]);
        else if (w.EndsWith("ed") && w.Length >= 5) w = Undouble(w[..^2]);

        if (w.EndsWith('e') && w.Length >= 4) w = w[..^1];
        return w;

        // "setting" is "sett", which is "set"; "fall" stays.
        static string Undouble(string s) =>
            s.Length >= 3 && s[^1] == s[^2] && s[^1] is not ('l' or 's' or 'z') && !"aeiou".Contains(s[^1]) ? s[..^1] : s;
    }

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "to", "in", "on", "for", "is", "are", "was", "be", "it", "its", "this", "that",
        "with", "as", "by", "at", "from", "where", "what", "how", "does", "do", "which", "who", "when", "why", "i", "we",
        "you", "my", "our", "into", "if", "then", "there", "here", "find", "code", "me", "any",
    };

    [GeneratedRegex(@"\p{L}[\p{L}\p{Nd}_]*|\d+[\p{L}_][\p{L}\p{Nd}_]*")]
    private static partial Regex Word();
}
