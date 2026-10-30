using System.Text;
using System.Text.RegularExpressions;
using static Sloperator.Core.Chats.MarkdownSyntax;

namespace Sloperator.Core.Documents;

/// <summary>A piece of text in one style.</summary>
internal sealed record Span(string Text, bool Bold = false, bool Italic = false, bool Code = false, bool Strike = false, string? Link = null)
{
    public static readonly Span LineBreak = new("\n");
}

internal abstract record Block;

internal sealed record HeadingBlock(int Level, IReadOnlyList<Span> Text) : Block;

/// <summary>A paragraph; a line break written in it stays one ("\n" spans), as in an address.</summary>
internal sealed record ParagraphBlock(IReadOnlyList<Span> Text) : Block;

/// <param name="Number">For a numbered item, the number it was written with.</param>
internal sealed record ListItemBlock(int Level, bool Ordered, int Number, IReadOnlyList<Span> Text) : Block;

/// <param name="Header">The first row is the header (a |---| line followed it).</param>
/// <param name="Align">Per column: 'l', 'c' or 'r'.</param>
internal sealed record TableBlock(IReadOnlyList<IReadOnlyList<IReadOnlyList<Span>>> Rows, bool Header, IReadOnlyList<char> Align) : Block
{
    public int Columns => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

internal sealed record CodeBlock(string Text) : Block;

internal sealed record QuoteBlock(IReadOnlyList<Span> Text) : Block;

internal sealed record ImageBlock(string Source, string Alt) : Block;

/// <summary>A line of ---: a horizontal line in a document, the start of the next slide in a presentation.</summary>
internal sealed record RuleBlock : Block;

internal sealed record PageBreakBlock : Block;

/// <summary>
/// Markdown as models write it, read into blocks for the document writers: headings, paragraphs,
/// lists (nested, numbered, task lists), tables, code, quotes, pictures on a line of their own,
/// lines of ---, and \newpage for a page break. Inline: **bold**, *italic*, `code`, ~~struck~~ and
/// [links](url). A single line break in a paragraph is kept, so an address stays on its lines.
/// </summary>
internal static partial class MarkdownDocument
{
    public static List<Block> Parse(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var blocks = new List<Block>();
        var paragraph = new List<string>();
        var listIndents = new List<int>();

        void EndParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new ParagraphBlock(InlineLines(paragraph)));
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                EndParagraph();
                continue;
            }
            if (Fence().Match(line) is { Success: true } fence)
            {
                EndParagraph();
                var code = new List<string>();
                var marker = fence.Groups[1].Value;
                for (i++; i < lines.Length && !lines[i].TrimStart().StartsWith(marker, StringComparison.Ordinal); i++)
                    code.Add(lines[i]);
                blocks.Add(new CodeBlock(string.Join("\n", code)));
                continue;
            }
            if (PageBreak().IsMatch(trimmed))
            {
                EndParagraph();
                blocks.Add(new PageBreakBlock());
                continue;
            }
            if (Heading().Match(line) is { Success: true } h)
            {
                EndParagraph();
                blocks.Add(new HeadingBlock(h.Groups[1].Length, Inline(h.Groups[2].Value)));
                continue;
            }
            if (Rule().IsMatch(line))
            {
                EndParagraph();
                blocks.Add(new RuleBlock());
                continue;
            }
            if (IsTableRow(line))
            {
                EndParagraph();
                var rows = new List<string>();
                while (i < lines.Length && IsTableRow(lines[i]))
                    rows.Add(lines[i++]);
                i--;
                blocks.Add(Table(rows));
                continue;
            }
            if (Image().Match(trimmed) is { Success: true } image)
            {
                EndParagraph();
                blocks.Add(new ImageBlock(image.Groups[2].Value.Trim('<', '>'), image.Groups[1].Value));
                continue;
            }
            if (ListItem().Match(line) is { Success: true } item)
            {
                EndParagraph();
                var indent = item.Groups[1].Value.Replace("\t", "    ").Length;
                // The level is how many open lists are indented less than this item.
                while (listIndents.Count > 0 && listIndents[^1] > indent)
                    listIndents.RemoveAt(listIndents.Count - 1);
                if (listIndents.Count == 0 || listIndents[^1] < indent)
                    listIndents.Add(indent);
                var marker = item.Groups[2].Value;
                var ordered = char.IsDigit(marker[0]);
                var text = item.Groups[3].Value;
                // Task lists: "- [ ] call Anna", "- [x] book the room".
                if (TaskBox().Match(text) is { Success: true } box)
                    text = (box.Groups[1].Value == " " ? "☐ " : "☑ ") + text[box.Length..];
                // A line indented under an item continues it.
                var more = new List<string> { text };
                while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !ListItem().IsMatch(lines[i + 1])
                       && lines[i + 1].Length - lines[i + 1].TrimStart().Length > indent && !IsTableRow(lines[i + 1]))
                    more.Add(lines[++i].Trim());
                blocks.Add(new ListItemBlock(listIndents.Count - 1, ordered, ordered ? int.Parse(marker[..^1]) : 0, InlineLines(more)));
                continue;
            }
            listIndents.Clear();
            if (Quote().Match(line) is { Success: true } quote)
            {
                EndParagraph();
                var quoted = new List<string> { quote.Groups[1].Value };
                while (i + 1 < lines.Length && Quote().Match(lines[i + 1]) is { Success: true } next)
                {
                    quoted.Add(next.Groups[1].Value);
                    i++;
                }
                blocks.Add(new QuoteBlock(InlineLines(quoted)));
                continue;
            }
            paragraph.Add(trimmed);
        }
        EndParagraph();
        return blocks;
    }

    /// <summary>The blocks' text without styles: what a heading or a table cell says.</summary>
    public static string Plain(IEnumerable<Span> spans) => string.Concat(spans.Select(s => s.Text));

    /// <summary>Lines of one block, a line break between them.</summary>
    private static List<Span> InlineLines(List<string> lines)
    {
        var spans = new List<Span>();
        foreach (var line in lines)
        {
            if (spans.Count > 0)
                spans.Add(Span.LineBreak);
            // A trailing backslash or two spaces mark a line break in Markdown; the break is kept anyway.
            spans.AddRange(Inline(line.TrimEnd('\\').TrimEnd()));
        }
        return spans;
    }

    // ---- inline ------------------------------------------------------------------------------------

    public static List<Span> Inline(string text) => Inline(text, new Span(""));

    private static List<Span> Inline(string text, Span style)
    {
        var spans = new List<Span>();
        var plain = new StringBuilder();
        void Flush()
        {
            if (plain.Length == 0) return;
            spans.Add(style with { Text = plain.ToString() });
            plain.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            // \* \_ \` \| \[ \] \# and \\ are the characters themselves; any other backslash stays (paths).
            if (c == '\\' && i + 1 < text.Length && @"*_`|[]#\~<>!".Contains(text[i + 1]))
            {
                plain.Append(text[++i]);
                continue;
            }
            if (c == '`')
            {
                var ticks = 1;
                while (i + ticks < text.Length && text[i + ticks] == '`') ticks++;
                var end = text.IndexOf(new string('`', ticks), i + ticks, StringComparison.Ordinal);
                if (end > i + ticks)
                {
                    Flush();
                    spans.Add(style with { Text = text[(i + ticks)..end], Code = true });
                    i = end + ticks - 1;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && At(text, i + 1) == c && !char.IsWhiteSpace(At(text, i + 2))
                && (c == '*' || i == 0 || !char.IsLetterOrDigit(text[i - 1])))
            {
                var end = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                if (end > i + 2 && !char.IsWhiteSpace(text[end - 1]))
                {
                    Flush();
                    spans.AddRange(Inline(text[(i + 2)..end], style with { Bold = true }));
                    i = end + 1;
                    continue;
                }
            }
            if (c == '~' && At(text, i + 1) == '~')
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    Flush();
                    spans.AddRange(Inline(text[(i + 2)..end], style with { Strike = true }));
                    i = end + 1;
                    continue;
                }
            }
            if ((c == '*' || c == '_') && OpensItalic(text, i) && FindItalicEnd(text, i + 1, c) is var close and > 0)
            {
                Flush();
                spans.AddRange(Inline(text[(i + 1)..close], style with { Italic = true }));
                i = close;
                continue;
            }
            if (c == '[' && Link().Match(text, i) is { Success: true } link && link.Index == i)
            {
                Flush();
                spans.AddRange(Inline(link.Groups[1].Value, style with { Link = link.Groups[2].Value.Trim('<', '>') }));
                i += link.Length - 1;
                continue;
            }
            if (c == '<' && AutoLink().Match(text, i) is { Success: true } url && url.Index == i)
            {
                Flush();
                spans.Add(style with { Text = url.Groups[1].Value, Link = url.Groups[1].Value });
                i += url.Length - 1;
                continue;
            }
            if (c == '<' && Tag().Match(text, i) is { Success: true } tag && tag.Index == i)
            {
                // <br> is a line break; <u>, <b>, <i> and the like are dropped, their text kept.
                if (tag.Groups[1].Value.Equals("br", StringComparison.OrdinalIgnoreCase))
                {
                    Flush();
                    spans.Add(Span.LineBreak);
                }
                i += tag.Length - 1;
                continue;
            }
            plain.Append(c);
        }
        Flush();
        return spans;
    }

    // ---- tables ------------------------------------------------------------------------------------

    private static TableBlock Table(List<string> lines)
    {
        var cells = lines.Select(l => Cells(l, keepEscapes: true)).ToList();
        var separator = SeparatorRow(cells);
        var align = separator >= 0
            ? cells[separator].Select(c => c.EndsWith(':') ? (c.StartsWith(':') ? 'c' : 'r') : 'l').ToList()
            : [];
        var rows = cells.Where((_, i) => i != separator)
            .Select(r => (IReadOnlyList<IReadOnlyList<Span>>)r.Select(c => (IReadOnlyList<Span>)Inline(c.Replace("<br>", "\n").Replace("<br/>", "\n"))).ToList())
            .ToList();
        return new TableBlock(rows, separator == 1, align);
    }

    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^(\\newpage|\\pagebreak|<!--\s*page\s*break\s*-->|<div[^>]*page-break[^>]*>\s*</div>)$", RegexOptions.IgnoreCase)]
    private static partial Regex PageBreak();

    [GeneratedRegex(@"^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^\s{0,3}([-*_])(\s*\1){2,}\s*$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^\s{0,3}>\s?(.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(\s*)([-*+•]|[0-9]{1,4}[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"^\[([ xX])\]\s+")]
    private static partial Regex TaskBox();

    [GeneratedRegex(@"^!\[([^\]]*)\]\(\s*(<[^>]+>|[^)""]+?)\s*(?:""[^""]*"")?\s*\)$")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"</?(br|u|b|i|em|strong|span|sup|sub|small|mark|ins|del|s|font|p|div|center)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"<(https?://[^>\s]+)>")]
    private static partial Regex AutoLink();
}
