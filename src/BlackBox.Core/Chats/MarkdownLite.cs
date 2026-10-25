using System.Text;
using System.Text.RegularExpressions;
using static BlackBox.Core.Chats.MarkdownSyntax;

namespace BlackBox.Core.Chats;

[Flags]
public enum MdStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    /// <summary>Monospace: inline code, code blocks and table rows.</summary>
    Code = 4,
    Heading = 8,
    /// <summary>Quotes and the bullet in front of them.</summary>
    Muted = 16,
}

/// <summary>A run of text in one style; <paramref name="Url"/> when a click on it opens a web page.</summary>
public readonly record struct MdSpan(string Text, MdStyle Style, string? Url = null);

/// <summary>
/// A part of an answer: Markdown text, or a fenced code block's code (<paramref name="Language"/>
/// from its fence, "" when it names none).
/// </summary>
public readonly record struct MdBlock(string Text, bool IsCode, string Language = "");

/// <summary>
/// Where a line shown comes from: its line in the Markdown (from 0), and for a row of a table where
/// each of its cells starts in the line shown, by character (<paramref name="Cells"/>).
/// </summary>
public readonly record struct MdLine(int Source, IReadOnlyList<int>? Cells = null);

/// <summary>
/// The Markdown models write in their answers (bold, headings, lists, code, tables, quotes),
/// turned into lines of styled runs for a chat bubble, so "**Total:** $3,000" doesn't show its
/// asterisks. Deliberately small: what it doesn't know stays as it was written.
/// </summary>
public static partial class MarkdownLite
{
    public static List<List<MdSpan>> Render(string markdown) => Render(markdown, null);

    /// <summary>
    /// <see cref="Render(string)"/>, with where each line shown comes from: its line in
    /// <paramref name="markdown"/>, and for a table's row where each cell starts in it.
    /// </summary>
    public static List<List<MdSpan>> Render(string markdown, out List<MdLine> lines)
    {
        lines = [];
        return Render(markdown, lines);
    }

    private static List<List<MdSpan>> Render(string markdown, List<MdLine>? map)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var output = new List<List<MdSpan>>();
        var inCode = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                output.Add(Linked([new MdSpan(line, MdStyle.Code)]));
                map?.Add(new MdLine(i));
                continue;
            }
            if (IsTableRow(line))
            {
                var first = i;
                var table = new List<string>();
                while (i < lines.Length && IsTableRow(lines[i]))
                    table.Add(lines[i++]);
                i--;
                foreach (var (row, from, cells) in Table(table))
                {
                    output.Add(Linked(row));
                    map?.Add(new MdLine(first + from, cells));
                }
                continue;
            }
            output.Add(Linked(Line(line)));
            map?.Add(new MdLine(i));
        }
        return output;
    }

    /// <summary>
    /// An answer cut at its fenced code blocks, so that each shows in a box of its own. The blank
    /// lines around a block go; a block still streaming in (no closing fence yet) runs to the end.
    /// A fence indented in a list takes its indent off the code's lines, and only a fence of at
    /// least as many backticks and no language closes it, so "```" lines in the code stay code.
    /// </summary>
    public static List<MdBlock> Blocks(string markdown) => Blocks(markdown, out _);

    /// <summary><see cref="Blocks(string)"/>, with the line of <paramref name="markdown"/> (from 0) each block starts on.</summary>
    public static List<MdBlock> Blocks(string markdown, out List<int> starts)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var blocks = new List<MdBlock>();
        var firsts = starts = [];
        var text = new List<string>();
        var textStart = 0;
        void FlushText()
        {
            var first = text.FindIndex(l => l.Trim().Length > 0);
            var last = text.FindLastIndex(l => l.Trim().Length > 0);
            if (first >= 0)
            {
                blocks.Add(new MdBlock(string.Join("\n", text.Skip(first).Take(last - first + 1)), false));
                firsts.Add(textStart + first);
            }
            text.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            if (Fence().Match(lines[i]) is not { Success: true } open)
            {
                if (text.Count == 0)
                    textStart = i;
                text.Add(lines[i]);
                continue;
            }
            FlushText();
            firsts.Add(i + 1);
            var indent = open.Groups[1].Length;
            var ticks = open.Groups[2].Length;
            var code = new List<string>();
            for (i++; i < lines.Length; i++)
            {
                if (Fence().Match(lines[i]) is { Success: true } close && close.Groups[2].Length >= ticks && close.Groups[3].Length == 0)
                    break;
                var line = lines[i];
                var strip = 0;
                while (strip < indent && strip < line.Length && line[strip] == ' ') strip++;
                code.Add(line[strip..]);
            }
            blocks.Add(new MdBlock(string.Join("\n", code), true, open.Groups[3].Value));
        }
        FlushText();
        return blocks;
    }

    /// <summary>
    /// Text that isn't Markdown (the user's messages, notices, the model's thinking) as lines, with
    /// its web addresses made links.
    /// </summary>
    public static List<List<MdSpan>> Plain(string text, MdStyle style = MdStyle.None) =>
        text.Replace("\r\n", "\n").Split('\n').Select(l => Linked([new MdSpan(l, style)])).ToList();

    /// <summary>
    /// The web addresses written out in a line's spans (https://..., www....) split off as links of
    /// their own. Punctuation after an address ("see https://x.dev.") stays text, and so does a
    /// closing bracket the address didn't open ("(https://x.dev)").
    /// </summary>
    private static List<MdSpan> Linked(List<MdSpan> spans)
    {
        if (!spans.Any(s => s.Url == null && (s.Text.Contains("://", StringComparison.Ordinal) || s.Text.Contains("www.", StringComparison.OrdinalIgnoreCase))))
            return spans;
        var output = new List<MdSpan>();
        foreach (var span in spans)
        {
            if (span.Url != null)
            {
                output.Add(span);
                continue;
            }
            var at = 0;
            foreach (Match m in BareUrl().Matches(span.Text))
            {
                var url = TrimUrl(m.Value);
                if (url.Length < 8) continue;
                if (m.Index > at) output.Add(span with { Text = span.Text[at..m.Index] });
                var target = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
                output.Add(span with { Text = url, Url = target });
                at = m.Index + url.Length;
            }
            if (at < span.Text.Length) output.Add(span with { Text = span.Text[at..] });
        }
        return output;
    }

    private static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (".,;:!?'\"*_>".Contains(last))
                url = url[..^1];
            else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
                url = url[..^1];
            else if (last == ']' && url.Count(c => c == '[') < url.Count(c => c == ']'))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    /// <summary>A link a click may open: a web page or an e-mail address, nothing that runs on the PC.</summary>
    public static bool IsWebLink(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    private static List<MdSpan> Line(string line)
    {
        if (Heading().Match(line) is { Success: true } h)
            return Inline(h.Groups[1].Value, MdStyle.Heading | MdStyle.Bold);
        if (Rule().IsMatch(line))
            return [];
        if (Quote().Match(line) is { Success: true } q)
            return [new MdSpan("▍ ", MdStyle.Muted), .. Inline(q.Groups[1].Value, MdStyle.Muted)];
        if (Bullet().Match(line) is { Success: true } b)
            return [new MdSpan(Indent(b.Groups[1].Value) + "• ", MdStyle.None), .. Inline(b.Groups[2].Value, MdStyle.None)];
        if (Numbered().Match(line) is { Success: true } n)
            return [new MdSpan(Indent(n.Groups[1].Value) + n.Groups[2].Value + ". ", MdStyle.None), .. Inline(n.Groups[3].Value, MdStyle.None)];
        return Inline(line, MdStyle.None);
    }

    /// <summary>Two spaces per level of a nested list (models indent by two to four).</summary>
    private static string Indent(string leading) => new(' ', Math.Min(leading.Replace("\t", "    ").Length / 2, 6) * 2);

    // ---- inline: **bold**, *italic*, `code`, [text](link) -------------------------------------

    public static List<MdSpan> Inline(string text, MdStyle style)
    {
        var spans = new List<MdSpan>();
        var plain = new StringBuilder();
        void Flush(MdStyle s)
        {
            if (plain.Length == 0) return;
            spans.Add(new MdSpan(plain.ToString(), s));
            plain.Clear();
        }

        // Backslash escapes are left as written: answers are full of Windows paths, where
        // "C:\Repos\*.txt" or "\.dev" must keep every backslash.
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var fence = new string('`', ticks);
                var end = text.IndexOf(fence, i + ticks, StringComparison.Ordinal);
                if (end > i + ticks)
                {
                    Flush(style);
                    spans.Add(new MdSpan(text[(i + ticks)..end].Trim(), style | MdStyle.Code));
                    i = end + ticks - 1;
                    continue;
                }
            }
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' && !char.IsWhiteSpace(At(text, i + 2)))
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2 && !char.IsWhiteSpace(text[end - 1]))
                {
                    Flush(style);
                    spans.AddRange(Inline(text[(i + 2)..end], style | MdStyle.Bold));
                    i = end + 1;
                    continue;
                }
            }
            if (c == '*' && OpensItalic(text, i) && FindItalicEnd(text, i + 1, '*') is var close and > 0)
            {
                Flush(style);
                spans.AddRange(Inline(text[(i + 1)..close], style | MdStyle.Italic));
                i = close;
                continue;
            }
            if (c == '[' && Link().Match(text, i) is { Success: true } link && link.Index == i)
            {
                // A link to a web page shows its text, a click on it opening the page; any other
                // target (a file, a relative path) stays readable after the text, as it can't be opened.
                var target = link.Groups[2].Value;
                if (IsWebLink(target))
                {
                    Flush(style);
                    spans.AddRange(Inline(link.Groups[1].Value, style).Select(s => s with { Url = target }));
                }
                else
                {
                    plain.Append(link.Groups[1].Value);
                    if (target != link.Groups[1].Value)
                        plain.Append(" (").Append(target).Append(')');
                }
                i += link.Length - 1;
                continue;
            }
            plain.Append(c);
        }
        Flush(style);
        return spans;
    }

    // ---- tables ------------------------------------------------------------------------------

    /// <summary>
    /// Rows with their columns lined up in monospace; the header bold, the |---| line left out. Each
    /// with the row of <paramref name="rows"/> it is and where its cells start in it.
    /// </summary>
    private static IEnumerable<(List<MdSpan> Line, int Row, IReadOnlyList<int>? Cells)> Table(List<string> rows)
    {
        var cells = rows.Select(Cells).ToList();
        var separator = cells.FindIndex(r => r.Count > 0 && r.All(c => Separator().IsMatch(c)));
        var align = separator >= 0
            ? cells[separator].Select(c => c.EndsWith(':') ? (c.StartsWith(':') ? 0 : 1) : -1).ToList()
            : [];
        var body = Enumerable.Range(0, cells.Count).Where(i => i != separator).ToList();
        if (body.Count == 0)
        {
            // Nothing but a |---| line: a scale drawn in text, or a table whose first line is still
            // streaming in. It shows as written; measuring a table of no rows threw.
            for (var i = 0; i < rows.Count; i++)
                yield return (Inline(rows[i], MdStyle.Code), i, null);
            yield break;
        }
        var header = separator > 0 ? 1 : 0;
        var parsed = body.Select(r => cells[r].Select(c => Inline(c, MdStyle.Code)).ToList()).ToList();
        var columns = parsed.Max(r => r.Count);
        var widths = Enumerable.Range(0, columns)
            .Select(col => parsed.Max(r => col < r.Count ? r[col].Sum(s => s.Text.Length) : 0)).ToList();
        // Every row's cells start in the same places: each column as wide as its widest cell, 3 apart.
        var starts = new int[columns];
        for (var col = 1; col < columns; col++)
            starts[col] = starts[col - 1] + widths[col - 1] + 3;

        for (var r = 0; r < parsed.Count; r++)
        {
            var line = new List<MdSpan>();
            for (var col = 0; col < columns; col++)
            {
                var spans = col < parsed[r].Count ? parsed[r][col] : [];
                var pad = widths[col] - spans.Sum(s => s.Text.Length);
                var right = col < align.Count ? align[col] : -1;
                var (before, after) = right switch { 1 => (pad, 0), 0 => (pad / 2, pad - pad / 2), _ => (0, pad) };
                if (before > 0) line.Add(new MdSpan(new string(' ', before), MdStyle.Code));
                line.AddRange(r < header ? spans.Select(s => s with { Style = s.Style | MdStyle.Bold }) : spans);
                if (col < columns - 1)
                    line.Add(new MdSpan(new string(' ', after + 3), MdStyle.Code));
            }
            yield return (line, body[r], starts);
        }
    }

    private static List<string> Cells(string row)
    {
        var t = row.Trim();
        if (t.StartsWith('|')) t = t[1..];
        if (t.EndsWith('|') && !t.EndsWith("\\|")) t = t[..^1];
        // Split on | that isn't escaped or inside `code`.
        var cells = new List<string>();
        var cell = new StringBuilder();
        var inCode = false;
        for (var i = 0; i < t.Length; i++)
        {
            if (t[i] == '\\' && i + 1 < t.Length && t[i + 1] == '|') { cell.Append('|'); i++; continue; }
            if (t[i] == '`') inCode = !inCode;
            if (t[i] == '|' && !inCode) { cells.Add(cell.ToString().Trim()); cell.Clear(); continue; }
            cell.Append(t[i]);
        }
        cells.Add(cell.ToString().Trim());
        return cells;
    }

    // An opening or closing fence: its indent, its backticks, and the language after them.
    [GeneratedRegex(@"^( *)(`{3,})\s*([^`\s]*)[^`]*$")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^(\s*)(\d{1,3})[.)]\s+(.*)$")]
    private static partial Regex Numbered();

    [GeneratedRegex(@"^:?-{2,}:?$")]
    private static partial Regex Separator();

    // A target may hold parentheses in pairs: https://en.wikipedia.org/wiki/Python_(programming_language).
    [GeneratedRegex(@"\[([^\]\n]+)\]\(((?:[^()\s]|\([^()\s]*\))+)\)")]
    private static partial Regex Link();

    // Not after a Latin letter or digit, but right after Chinese or Japanese text it may be ("访问https://…");
    // Chinese and Japanese punctuation, curly quotes and an ellipsis end it ("详见 https://example.com/docs。").
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:https?://|www\.)[^\s<>""`。，、；：！？（）「」『』【】《》“”‘’…]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
