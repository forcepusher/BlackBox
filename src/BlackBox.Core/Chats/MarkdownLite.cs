using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>A run of text in one style.</summary>
public readonly record struct MdSpan(string Text, MdStyle Style);

/// <summary>
/// The Markdown models write in their answers (bold, headings, lists, code, tables, quotes),
/// turned into lines of styled runs for a chat bubble, so "**Total:** $3,000" doesn't show its
/// asterisks. Deliberately small: what it doesn't know stays as it was written.
/// </summary>
public static partial class MarkdownLite
{
    public static List<List<MdSpan>> Render(string markdown)
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
                output.Add([new MdSpan(line, MdStyle.Code)]);
                continue;
            }
            if (IsTableRow(line))
            {
                var table = new List<string>();
                while (i < lines.Length && IsTableRow(lines[i]))
                    table.Add(lines[i++]);
                i--;
                output.AddRange(Table(table));
                continue;
            }
            output.Add(Line(line));
        }
        return output;
    }

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
            if (c == '*' && OpensItalic(text, i) && FindItalicEnd(text, i + 1) is var close and > 0)
            {
                Flush(style);
                spans.AddRange(Inline(text[(i + 1)..close], style | MdStyle.Italic));
                i = close;
                continue;
            }
            if (c == '[' && Link().Match(text, i) is { Success: true } link && link.Index == i)
            {
                plain.Append(link.Groups[1].Value);
                var target = link.Groups[2].Value;
                if (target != link.Groups[1].Value)
                    plain.Append(" (").Append(target).Append(')');
                i += link.Length - 1;
                continue;
            }
            plain.Append(c);
        }
        Flush(style);
        return spans;
    }

    private static char At(string s, int i) => i < s.Length ? s[i] : ' ';

    /// <summary>A single * that starts a word ("*very*"), not one in "5 * 3" or "*.txt" or "a*b".</summary>
    private static bool OpensItalic(string s, int i) =>
        (i == 0 || char.IsWhiteSpace(s[i - 1]) || "([\"'".Contains(s[i - 1])) && At(s, i + 1) is var next && char.IsLetterOrDigit(next);

    private static int FindItalicEnd(string s, int from)
    {
        for (var j = from + 1; j < s.Length; j++)
        {
            if (s[j] != '*' || (j + 1 < s.Length && s[j + 1] == '*')) continue;
            if (!char.IsWhiteSpace(s[j - 1]) && (j + 1 == s.Length || !char.IsLetterOrDigit(s[j + 1])))
                return j;
        }
        return -1;
    }

    // ---- tables ------------------------------------------------------------------------------

    private static bool IsTableRow(string line) => line.TrimStart().StartsWith('|') && line.Trim().Length > 1;

    /// <summary>Rows with their columns lined up in monospace; the header bold, the |---| line left out.</summary>
    private static IEnumerable<List<MdSpan>> Table(List<string> rows)
    {
        var cells = rows.Select(Cells).ToList();
        var separator = cells.FindIndex(r => r.Count > 0 && r.All(c => Separator().IsMatch(c)));
        var align = separator >= 0
            ? cells[separator].Select(c => c.EndsWith(':') ? (c.StartsWith(':') ? 0 : 1) : -1).ToList()
            : [];
        var body = cells.Where((_, i) => i != separator).ToList();
        var header = separator > 0 ? 1 : 0;
        var parsed = body.Select(r => r.Select(c => Inline(c, MdStyle.Code)).ToList()).ToList();
        var columns = parsed.Max(r => r.Count);
        var widths = Enumerable.Range(0, columns)
            .Select(col => parsed.Max(r => col < r.Count ? r[col].Sum(s => s.Text.Length) : 0)).ToList();

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
            yield return line;
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

    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();
}
