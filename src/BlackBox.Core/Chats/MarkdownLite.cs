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
    /// <summary>~~Struck through~~.</summary>
    Strike = 32,
}

/// <summary>A run of text in one style; <paramref name="Url"/> when a click on it opens a web page.</summary>
public readonly record struct MdSpan(string Text, MdStyle Style, string? Url = null);

/// <summary>
/// A part of an answer: Markdown text, or a fenced code block's code (<paramref name="Language"/>
/// from its fence, "" when it names none; <paramref name="Indent"/> the spaces before its fence, in a list item).
/// </summary>
public readonly record struct MdBlock(string Text, bool IsCode, string Language = "", int Indent = 0);

/// <summary>
/// Where a line shown comes from: its line in the Markdown (from 0), and for a row of a table where
/// each of its cells starts in the line shown, by character (<paramref name="Cells"/>).
/// </summary>
public readonly record struct MdLine(int Source, IReadOnlyList<int>? Cells = null);

/// <summary>
/// A table for a grid of cells: each row's cells as styled runs, every row as long as the longest
/// (a short row gets empty cells), the first <paramref name="HeaderRows"/> its header (bold), and each
/// column's alignment from the |:--:| line: -1 left, 0 centre, 1 right.
/// </summary>
public sealed record MdTable(IReadOnlyList<IReadOnlyList<List<MdSpan>>> Rows, int HeaderRows, IReadOnlyList<int> Align)
{
    public int Columns => Rows.Count == 0 ? 0 : Rows[0].Count;
}

public enum MdKind
{
    /// <summary>A paragraph: its lines, Markdown inside them.</summary>
    Text,
    /// <summary>A heading, its level 1 to 6 the part's level.</summary>
    Heading,
    /// <summary>A list's item: its marker ("•", "3.", "☐", "☑"), its text, and how deep the list is nested (from 0).</summary>
    Item,
    /// <summary>A quote's line, how many quotes deep (from 1); a list item in it has its marker.</summary>
    Quote,
    /// <summary>A --- line across.</summary>
    Rule,
    /// <summary>A table, nested as deep as a list item when it is in one.</summary>
    Table,
    /// <summary>A footnote's text at the end ("[^1]: …"), shown as "[1] …".</summary>
    Note,
}

/// <summary>
/// A part of a block of Markdown text, as a page shows it: a paragraph, a heading, a list item, a
/// quote's line, a rule or a <paramref name="Table"/> (its lines as written in <paramref name="Text"/>).
/// <paramref name="Text"/> is the Markdown in it, without its marker (#, -, 1., &gt;). <paramref name="Spaced"/>: a blank line
/// was over it, which sets a loose list's items or two quotes apart.
/// </summary>
public readonly record struct MdPart(string Text, MdTable? Table = null, MdKind Kind = MdKind.Text, int Level = 0, string Marker = "", bool Spaced = false);

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
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
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
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
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
            blocks.Add(new MdBlock(string.Join("\n", code), true, open.Groups[3].Value, indent));
        }
        FlushText();
        return blocks;
    }

    /// <summary>
    /// Text that isn't Markdown (the user's messages, notices, the model's thinking) as lines, with
    /// its web addresses made links.
    /// </summary>
    public static List<List<MdSpan>> Plain(string text, MdStyle style = MdStyle.None) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => Linked([new MdSpan(l, style)])).ToList();

    /// <summary>
    /// Markdown text (a block of <see cref="Blocks(string)"/> that isn't code) cut into the parts a
    /// page shows one under another: paragraphs (blank lines between them go), headings, list items,
    /// quotes' lines, rules and tables. A line indented under a list item goes on with the item, so
    /// its text wraps under the item's text rather than under its bullet. Pipe lines that make no
    /// table (nothing but a |---| line) stay text.
    /// </summary>
    public static List<MdPart> Parts(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var parts = new List<MdPart>();
        var text = new List<string>();
        var blank = false;
        var textSpaced = false;
        void FlushText()
        {
            if (text.Count > 0)
            {
                // A paragraph indented under a list item (after a code block in it) goes in with the item's text.
                var lead = text.Where(l => l.Trim().Length > 0).Select(Leading).DefaultIfEmpty(0).Min();
                var level = lead >= 2 ? Math.Max(1, lead / 3) : 0;
                parts.Add(new MdPart(string.Join("\n", text.Select(l => l.Length >= lead ? l[lead..] : l.TrimStart())), Level: level, Spaced: textSpaced));
            }
            text.Clear();
        }
        void Add(MdPart part)
        {
            FlushText();
            parts.Add(part with { Spaced = blank });
            blank = false;
        }
        void AddText(string line)
        {
            if (text.Count == 0)
                (textSpaced, blank) = (blank, false);
            text.Add(line);
        }

        // The columns of the markers of the list items a line is in: an item indented past one is nested in it.
        var open = new List<int>();
        int Nest(int leading)
        {
            while (open.Count > 0 && leading <= open[^1])
                open.RemoveAt(open.Count - 1);
            return open.Count;
        }

        var inCode = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                inCode = !inCode;
            if (inCode)
            {
                AddText(line);
                continue;
            }
            if (line.Trim().Length == 0)
            {
                FlushText();
                blank = true;
                continue;
            }
            if (Leading(line) == 0 && Item(line) is null)
                open.Clear();
            if (IsTableRow(line))
            {
                var rows = new List<string>();
                while (i < lines.Length && IsTableRow(lines[i]))
                    rows.Add(lines[i++]);
                i--;
                if (ParseTable(rows) is { } table)
                    Add(new MdPart(string.Join("\n", rows), table, MdKind.Table, Nest(Leading(line))));
                else
                    rows.ForEach(AddText);
                continue;
            }
            if (HeadingLevel().Match(line) is { Success: true } h)
                Add(new MdPart(h.Groups[2].Value, Kind: MdKind.Heading, Level: h.Groups[1].Length));
            else if (Rule().IsMatch(line))
                Add(new MdPart("", Kind: MdKind.Rule));
            else if (Quote().IsMatch(line))
            {
                var depth = 0;
                var inner = line;
                while (Quote().Match(inner) is { Success: true } q)
                {
                    depth++;
                    inner = q.Groups[1].Value;
                }
                var (marker, rest) = Item(inner) is { } item ? (item.Marker, item.Text) : ("", inner);
                // GitHub's alerts: "> [!WARNING]" over the quote's text is its title.
                if (Alert().Match(rest) is { Success: true } alert)
                    rest = "**" + char.ToUpperInvariant(alert.Groups[1].Value[0]) + alert.Groups[1].Value[1..].ToLowerInvariant() + "**";
                // A quote's lines run on in one part, as a paragraph's do; a list item in it is a part of its own.
                if (marker.Length == 0 && text.Count == 0 && parts.Count > 0 && parts[^1] is { Kind: MdKind.Quote, Marker: "" } last
                    && last.Level == depth && lines[i - 1].Trim().Length > 0)
                    parts[^1] = last with { Text = last.Text + "\n" + rest };
                else
                    Add(new MdPart(rest, Kind: MdKind.Quote, Level: depth, Marker: marker));
            }
            else if (Item(line) is { } item)
            {
                var itemText = new List<string> { item.Text };
                // Lines indented under it are its own: a wrapped line, or a paragraph of it after a blank one.
                while (i + 1 < lines.Length && Continues(lines, i + 1, item.Indent))
                    itemText.Add(lines[++i].Trim());
                Add(new MdPart(string.Join("\n", itemText), Kind: MdKind.Item, Level: Nest(item.Leading), Marker: item.Marker));
                open.Add(item.Leading);
            }
            else if (FootnoteText().Match(line) is { Success: true } note)
                Add(new MdPart(note.Groups[2].Value, Kind: MdKind.Note, Marker: $"[{note.Groups[1].Value}]"));
            else
                AddText(line);
        }
        FlushText();
        return parts;
    }

    private static int Leading(string line) => line.Length - line.TrimStart().Length;

    /// <summary>A list item's line: its marker as shown, its text, the column of its marker and of its text.</summary>
    private static (string Marker, string Text, int Leading, int Indent)? Item(string line)
    {
        string marker, rest;
        if (Bullet().Match(line) is { Success: true } b)
            (marker, rest) = ("•", b.Groups[2].Value);
        else if (Numbered().Match(line) is { Success: true } n)
            (marker, rest) = (n.Groups[2].Value + ".", n.Groups[3].Value);
        else
            return null;
        var indent = line.Length - rest.Length;
        if (TaskBox().Match(rest) is { Success: true } task)
            (marker, rest) = (task.Groups[1].Value == " " ? "☐" : "☑", task.Groups[2].Value);
        return (marker, rest, Leading(line), indent);
    }

    /// <summary>
    /// Whether line <paramref name="at"/> goes on with the list item above it, whose text starts at
    /// column <paramref name="indent"/>: an indented line right under it, or after blank lines one
    /// indented as far as its text; never an item, a heading, a quote, a table or a fence of its own.
    /// </summary>
    private static bool Continues(string[] lines, int at, int indent)
    {
        var j = at;
        while (j < lines.Length && lines[j].Trim().Length == 0)
            j++;
        if (j >= lines.Length)
            return false;
        var line = lines[j];
        var leading = line.Length - line.TrimStart().Length;
        if (Item(line) is not null || IsTableRow(line) || Quote().IsMatch(line) || HeadingLevel().IsMatch(line)
            || line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            return false;
        return j == at ? leading > 0 : leading >= indent;
    }

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
        if (Item(line) is { } item)
            return [new MdSpan(Indent(line[..item.Leading]) + item.Marker + " ", MdStyle.None), .. Inline(item.Text, MdStyle.None)];
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
            if (c == '*' && text.AsSpan(i).StartsWith("***") && !char.IsWhiteSpace(At(text, i + 3)) && At(text, i + 3) != '*')
            {
                var end = text.IndexOf("***", i + 3, StringComparison.Ordinal);
                if (end > i + 3 && !char.IsWhiteSpace(text[end - 1]))
                {
                    Flush(style);
                    spans.AddRange(Inline(text[(i + 3)..end], style | MdStyle.Bold | MdStyle.Italic));
                    i = end + 2;
                    continue;
                }
            }
            if (c == '~' && At(text, i + 1) == '~' && !char.IsWhiteSpace(At(text, i + 2)))
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2 && !char.IsWhiteSpace(text[end - 1]))
                {
                    Flush(style);
                    spans.AddRange(Inline(text[(i + 2)..end], style | MdStyle.Strike));
                    i = end + 1;
                    continue;
                }
            }
            // __bold__ and _italic_ only around whole words: snake_case and file_names stay as written.
            if (c == '_' && At(text, i + 1) == '_' && (i == 0 || char.IsWhiteSpace(text[i - 1]) || "([\"'".Contains(text[i - 1])) && char.IsLetterOrDigit(At(text, i + 2)))
            {
                var end = text.IndexOf("__", i + 2, StringComparison.Ordinal);
                if (end > i + 2 && !char.IsWhiteSpace(text[end - 1]) && !char.IsLetterOrDigit(At(text, end + 2)))
                {
                    Flush(style);
                    spans.AddRange(Inline(text[(i + 2)..end], style | MdStyle.Bold));
                    i = end + 1;
                    continue;
                }
            }
            if (c == '_' && OpensItalic(text, i) && FindItalicEnd(text, i + 1, '_') is var underEnd and > 0)
            {
                Flush(style);
                spans.AddRange(Inline(text[(i + 1)..underEnd], style | MdStyle.Italic));
                i = underEnd;
                continue;
            }
            if (c == '<' && Autolink().Match(text, i) is { Success: true } auto && auto.Index == i)
            {
                Flush(style);
                spans.Add(new MdSpan(auto.Groups[1].Value, style, auto.Groups[1].Value));
                i += auto.Length - 1;
                continue;
            }
            if (c == '<' && Html().Match(text, i) is { Success: true } tag && tag.Index == i && HtmlTag(text, tag, style, spans, Flush) is var after and > 0)
            {
                i = after - 1;
                continue;
            }
            if (c == '[' && Footnote().Match(text, i) is { Success: true } note && note.Index == i)
            {
                Flush(style);
                spans.Add(new MdSpan($"[{note.Groups[1].Value}]", style | MdStyle.Muted));
                i += note.Length - 1;
                continue;
            }
            if (c == '!' && At(text, i + 1) == '[' && Link().Match(text, i + 1) is { Success: true } image && image.Index == i + 1)
            {
                // A picture shows as a link to it, by its description ("alt text"), or "picture".
                var target = image.Groups[2].Value;
                var alt = image.Groups[1].Value.Trim();
                Flush(style);
                spans.Add(new MdSpan(alt.Length > 0 ? alt : "picture", style, IsWebLink(target) ? target : null));
                i += image.Length;
                continue;
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
            if (c == '&' && Entity().Match(text, i) is { Success: true } entity && entity.Index == i)
            {
                plain.Append(System.Net.WebUtility.HtmlDecode(entity.Value));
                i += entity.Length - 1;
                continue;
            }
            plain.Append(c);
        }
        Flush(style);
        return spans;
    }

    /// <summary>
    /// The HTML models put in Markdown: &lt;br&gt; a line break, &lt;b&gt;, &lt;i&gt;, &lt;code&gt; and the
    /// like their style around their text, &lt;details&gt; and other wrappers left out. Where the text
    /// goes on after it, or -1 when it stays as written (a &lt;b&gt; never closed).
    /// </summary>
    private static int HtmlTag(string text, Match tag, MdStyle style, List<MdSpan> spans, Action<MdStyle> flush)
    {
        var name = tag.Groups[2].Value.ToLowerInvariant();
        var end = tag.Index + tag.Length;
        if (name == "br")
        {
            flush(style);
            spans.Add(new MdSpan("\n", style));
            return end;
        }
        if (name is "details" or "p" or "div" or "span")
            return end;
        if (tag.Groups[1].Length > 0)
            return -1;
        var close = text.IndexOf($"</{name}>", end, StringComparison.OrdinalIgnoreCase);
        if (close < 0)
            return -1;
        var inner = text[end..close];
        flush(style);
        if (name is "code" or "kbd")
            spans.Add(new MdSpan(System.Net.WebUtility.HtmlDecode(inner), style | MdStyle.Code));
        else
            spans.AddRange(Inline(inner, style | name switch
            {
                "b" or "strong" or "summary" => MdStyle.Bold,
                "i" or "em" => MdStyle.Italic,
                "s" or "del" or "strike" => MdStyle.Strike,
                _ => MdStyle.None,
            }));
        // A <details> block's <summary> is its title: bold, on a line of its own.
        if (name == "summary")
            spans.Add(new MdSpan("\n", style));
        return close + name.Length + 3;
    }

    // ---- tables ------------------------------------------------------------------------------

    /// <summary>
    /// A table's rows as a grid: the |---| line gives the alignment and makes the row over it the
    /// header. Null when there are no rows but |---| lines.
    /// </summary>
    private static MdTable? ParseTable(List<string> rows)
    {
        var cells = rows.Select(Cells).ToList();
        var separator = cells.FindIndex(r => r.Count > 0 && r.All(c => Separator().IsMatch(c)));
        var align = separator >= 0
            ? cells[separator].Select(c => c.EndsWith(':') ? (c.StartsWith(':') ? 0 : 1) : -1).ToList()
            : [];
        var body = Enumerable.Range(0, cells.Count).Where(i => i != separator).ToList();
        if (body.Count == 0)
            return null;
        var header = separator > 0 ? 1 : 0;
        var columns = body.Max(r => cells[r].Count);
        var parsed = body.Select((r, at) => (IReadOnlyList<List<MdSpan>>)Enumerable.Range(0, columns)
            .Select(col => col < cells[r].Count ? Linked(Inline(cells[r][col], at < header ? MdStyle.Bold : MdStyle.None)) : [])
            .ToList()).ToList();
        return new MdTable(parsed, header, Enumerable.Range(0, columns).Select(col => col < align.Count ? align[col] : -1).ToList());
    }

    /// <summary>
    /// Rows with their columns lined up in monospace; the header bold, the |---| line left out. Each
    /// with the row of <paramref name="rows"/> it is and where its cells start in it. The chat shows
    /// tables as grids (<see cref="Parts"/>); File inspector picks cells by these lines.
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

    // A |---| line's cell: dashes, a colon at either end for the alignment (":-:" is one too); a lone "-" is a cell.
    [GeneratedRegex(@"^(:-+:?|-+:|-{2,})$")]
    private static partial Regex Separator();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)\s*#*\s*$")]
    private static partial Regex HeadingLevel();

    // "[ ] to do" and "[x] done" at a list item's start.
    [GeneratedRegex(@"^\[([ xX])\]\s+(.*)$")]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"^\s{0,3}\[\^([^\]\s]+)\]:\s*(.*)$")]
    private static partial Regex FootnoteText();

    [GeneratedRegex(@"^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Alert();

    [GeneratedRegex(@"<(https?://[^\s<>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex Autolink();

    [GeneratedRegex(@"<(/?)(br|b|strong|i|em|code|kbd|s|del|strike|u|sup|sub|mark|summary|details|p|div|span)\b[^<>]*?/?>", RegexOptions.IgnoreCase)]
    private static partial Regex Html();

    [GeneratedRegex(@"&(?:amp|lt|gt|quot|apos|nbsp|#\d{1,6}|#x[0-9a-fA-F]{1,6});")]
    private static partial Regex Entity();

    [GeneratedRegex(@"\[\^([^\]\s]+)\]")]
    private static partial Regex Footnote();

    // A target may hold parentheses in pairs: https://en.wikipedia.org/wiki/Python_(programming_language).
    [GeneratedRegex(@"\[([^\]\n]+)\]\(((?:[^()\s]|\([^()\s]*\))+)\)")]
    private static partial Regex Link();

    // Not after a Latin letter or digit, but right after Chinese or Japanese text it may be ("访问https://…");
    // Chinese and Japanese punctuation, curly quotes and an ellipsis end it ("详见 https://example.com/docs。").
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:https?://|www\.)[^\s<>""`。，、；：！？（）「」『』【】《》“”‘’…]+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
