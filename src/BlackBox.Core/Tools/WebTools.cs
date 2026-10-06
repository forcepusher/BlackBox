using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Web;

namespace BlackBox.Core.Tools;

public sealed class WebSearchTool : Tool
{
    public override string Name => "web_search";
    public override string Description =>
        "Search the web. Returns titles, links and short snippets. Use it whenever you are not sure of something: " +
        "exact API names and signatures, error messages, versions, how a program works, current facts. " +
        "Then read the best result with fetch_page.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("query", Str("What to search for, e.g. 'Unity KeyCode left arrow' or 'site:learn.microsoft.com HttpClient timeout'"), true),
        ("max_results", Int("How many results (default 8, max 20)"), false));
    public override string Describe(JsonObject args) => $"Search the web: {GetString(args, "query")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var query = RequireString(args, "query");
        var max = Math.Clamp((int)(GetNumber(args, "max_results") ?? 8), 1, 20);
        IReadOnlyList<SearchHit> hits;
        try
        {
            hits = await WebSearch.SearchAsync(query, max, ctx.Browser, ct).ConfigureAwait(false);
        }
        catch (WebSearchException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        if (hits.Count == 0)
            return new ToolResult($"No results for \"{query}\". Try fewer or different words.");
        return new ToolResult(Format(hits));
    }

    internal static string Format(IReadOnlyList<SearchHit> hits)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.Append(i + 1).Append(". ").AppendLine(h.Title.Length > 0 ? h.Title : h.Url);
            sb.Append("   ").AppendLine(h.Url);
            if (h.Snippet.Length > 0)
                sb.Append("   ").AppendLine(Clip(h.Snippet, 300));
        }
        return sb.ToString().TrimEnd();
    }

    internal static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";
}

public sealed class FetchPageTool : Tool
{
    /// <summary>Characters of page text per call (about 1.5k tokens), so one page can't flood the context.</summary>
    internal const int PartSize = 6_000;
    /// <summary>Characters of passages a find returns.</summary>
    internal const int FindBudget = 5_000;

    public override string Name => "fetch_page";
    public override string Description =>
        "Read a web page as plain text: the main content only, with code blocks and tables kept. Long pages come in parts; " +
        "pass start to read on. Pass find to get only the passages that mention some words (e.g. find='LeftArrow'): " +
        "much shorter than reading the whole page.";
    public override ToolRisk Risk => ToolRisk.ReadOnly;
    public override JsonObject Parameters => Schema(
        ("url", Str("The page's address (from web_search or anywhere else)"), true),
        ("find", Str("Words to look for; returns only the passages that contain them"), false),
        ("start", Int("Character to start reading at, to read on in a long page (default 0)"), false));
    public override string Describe(JsonObject args) =>
        GetString(args, "find") is { Length: > 0 } find ? $"Read {GetString(args, "url")} (find \"{find}\")" : $"Read {GetString(args, "url")}";

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        var url = RequireString(args, "url");
        WebPageText page;
        try
        {
            page = await WebPage.ReadAsync(url, ctx.Browser, ct).ConfigureAwait(false);
        }
        catch (WebPageException ex)
        {
            return ToolResult.Error(ex.Message);
        }
        if (page.Text.Length == 0)
            return ToolResult.Error($"{page.Url} has no readable text (it may be built by JavaScript or need a login). Try another result.");

        var find = GetString(args, "find");
        return new ToolResult(find is { Length: > 0 }
            ? Find(page, find)
            : Part(page, Math.Max(0, (int)(GetNumber(args, "start") ?? 0))));
    }

    private static string Header(WebPageText page) =>
        (page.Title.Length > 0 ? "# " + page.Title + "\n" : "") + page.Url + "\n";

    /// <summary>The page text from <paramref name="start"/>, at most <see cref="PartSize"/> characters, ending at a line break.</summary>
    internal static string Part(WebPageText page, int start)
    {
        var text = page.Text;
        if (start >= text.Length)
            return Header(page) + $"\nThe page has only {text.Length} characters; start={start} is past its end.";
        // Begin at the start of a line, so a part never opens mid-word.
        if (start > 0)
        {
            var lineStart = text.LastIndexOf('\n', start - 1) + 1;
            if (start - lineStart < 300)
                start = lineStart;
        }
        var end = Math.Min(text.Length, start + PartSize);
        if (end < text.Length)
        {
            var lineEnd = text.LastIndexOf('\n', end - 1, end - start);
            if (lineEnd > start + PartSize / 2)
                end = lineEnd;
        }

        var sb = new StringBuilder(Header(page));
        if (start > 0 || end < text.Length)
            sb.Append($"(characters {start}-{end} of {text.Length})\n");
        sb.Append('\n').Append(text, start, end - start).Append('\n');
        if (end < text.Length)
            sb.Append($"\n[{text.Length - end} more characters: fetch_page with start={end} reads on; find=\"...\" jumps to what you need]");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// The lines that mention <paramref name="find"/>, each with a line of context before and two
    /// after and the heading it sits under. The whole phrase is looked for first, then lines with
    /// all of its words, then lines with any of them.
    /// </summary>
    internal static string Find(WebPageText page, string find)
    {
        var lines = page.Text.Split('\n');
        var offsets = new int[lines.Length];
        for (int i = 0, pos = 0; i < lines.Length; pos += lines[i].Length + 1, i++)
            offsets[i] = pos;

        var phrase = find.Trim().Trim('"', '\'');
        var words = phrase.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool Has(string line, string w) => line.Contains(w, StringComparison.OrdinalIgnoreCase);
        var matches = Matching(l => Has(l, phrase));
        if (matches.Count == 0 && words.Length > 1)
            matches = Matching(l => words.All(w => Has(l, w)));
        if (matches.Count == 0 && words.Length > 1)
            matches = Matching(l => words.Any(w => Has(l, w)));

        var sb = new StringBuilder(Header(page));
        if (matches.Count == 0)
        {
            sb.Append($"\nNothing on this page mentions \"{phrase}\" (it has {page.Text.Length} characters). Read it with start, or try other words.");
            return sb.ToString();
        }

        // Windows of context around each match, merged where they touch.
        var windows = new List<(int From, int To)>();
        foreach (var m in matches)
        {
            var (from, to) = (Math.Max(0, m - 1), Math.Min(lines.Length - 1, m + 2));
            if (windows.Count > 0 && from <= windows[^1].To + 1)
                windows[^1] = (windows[^1].From, Math.Max(windows[^1].To, to));
            else
                windows.Add((from, to));
        }

        sb.Append($"\n{matches.Count} {(matches.Count == 1 ? "line mentions" : "lines mention")} \"{phrase}\" (page: {page.Text.Length} characters):\n");
        var shown = 0;
        var headings = Headings(lines);
        string? lastHeading = null;
        foreach (var (from, to) in windows)
        {
            if (sb.Length > FindBudget)
            {
                sb.Append($"\n[{windows.Count - shown} more passages not shown: use more specific words, or read on with start={offsets[from]}]");
                break;
            }
            var heading = headings[from];
            sb.Append($"\n[start={offsets[from]}");
            if (heading is not null && heading != lastHeading)
                sb.Append(", under \"").Append(heading).Append('"');
            sb.Append("]\n");
            lastHeading = heading;
            for (var i = from; i <= to; i++)
                if (lines[i].Trim().Length > 0)
                    sb.AppendLine(Around(lines[i], words.Prepend(phrase), 600));
            shown++;
        }
        return sb.ToString().TrimEnd();

        List<int> Matching(Func<string, bool> test) =>
            Enumerable.Range(0, lines.Length).Where(i => test(lines[i])).ToList();
    }

    /// <summary>A long line cut down to <paramref name="max"/> characters around the first of <paramref name="words"/> in it.</summary>
    internal static string Around(string line, IEnumerable<string> words, int max)
    {
        if (line.Length <= max)
            return line;
        var at = words.Select(w => line.IndexOf(w, StringComparison.OrdinalIgnoreCase)).Where(i => i >= 0).DefaultIfEmpty(0).Min();
        var start = Math.Clamp(at - max / 3, 0, line.Length - max);
        return (start > 0 ? "…" : "") + line.Substring(start, max) + (start + max < line.Length ? "…" : "");
    }

    /// <summary>For each line, the heading it sits under; "#" lines inside code blocks are code, not headings.</summary>
    private static string?[] Headings(string[] lines)
    {
        var result = new string?[lines.Length];
        string? heading = null;
        var inCode = false;
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;
            else if (!inCode && lines[i].StartsWith('#'))
                heading = lines[i].TrimStart('#', ' ');
            result[i] = heading;
        }
        return result;
    }
}
