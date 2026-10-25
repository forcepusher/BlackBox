using BlackBox.Core.Chats;

namespace BlackBox.Core.Documents;

/// <summary>
/// Text selected in the File inspector panel, and where it is in the file: "lines 12-20" of a
/// Markdown file or a text file, "cells B4:C4" or "rows 3-7" of a CSV table. <paramref name="Columns"/>
/// names the table's columns the selection is in, from its header row; null outside a table.
/// </summary>
public sealed record DocumentPick(string Text, string Where, IReadOnlyList<string>? Columns = null)
{
    /// <summary>
    /// A selection from character <paramref name="start"/> to <paramref name="end"/> of a file's
    /// <paramref name="text"/> as it is shown, whole lines; null with nothing selected.
    /// </summary>
    public static DocumentPick? InText(string text, int start, int end)
    {
        (start, end) = (Math.Clamp(Math.Min(start, end), 0, text.Length), Math.Clamp(Math.Max(start, end), 0, text.Length));
        if (start == end)
            return null;
        var first = LineAt(text, start);
        // A selection that ends after a line's break ends on that line, not on the next.
        var last = LineAt(text, end - 1);
        return new DocumentPick(text[start..end], Lines(first + 1, last + 1));
    }

    /// <summary>
    /// A selection in one of the blocks <see cref="MarkdownLite.Blocks(string)"/> cuts
    /// <paramref name="markdown"/> into, from character <paramref name="start"/> to <paramref name="end"/>
    /// of the block's lines as shown, joined by line breaks. Where it is: the lines of the Markdown;
    /// with <paramref name="table"/> (a CSV shown as a table, <see cref="FileInspector.Table"/>), the
    /// table's rows and cells as a spreadsheet names them, its header row 1. Null with nothing selected.
    /// </summary>
    public static DocumentPick? InMarkdown(string markdown, int block, int start, int end, bool table)
    {
        var blocks = MarkdownLite.Blocks(markdown, out var starts);
        if (block < 0 || block >= blocks.Count)
            return null;
        var shown = blocks[block];
        List<string> lines;
        List<MdLine> map;
        if (shown.IsCode)
        {
            lines = MarkdownLite.Plain(shown.Text).Select(Shown).ToList();
            map = Enumerable.Range(0, lines.Count).Select(i => new MdLine(i)).ToList();
        }
        else
        {
            lines = MarkdownLite.Render(shown.Text, out map).Select(Shown).ToList();
        }
        var all = string.Join("\n", lines);
        (start, end) = (Math.Clamp(Math.Min(start, end), 0, all.Length), Math.Clamp(Math.Max(start, end), 0, all.Length));
        if (start == end || lines.Count == 0)
            return null;
        var (firstLine, firstColumn) = Place(lines, start);
        var (lastLine, lastColumn) = Place(lines, end - 1);
        var text = all[start..end];
        var from = starts[block] + map[firstLine].Source;
        var to = starts[block] + map[lastLine].Source;
        if (!table)
            return new DocumentPick(text, Lines(from + 1, to + 1));

        // The table's lines are its header, the |---| line, then a row each: row 1 is the header.
        int Row(int line) => line == 0 ? 1 : line;
        var h = map.FindIndex(m => m.Source == 0);
        var header = h >= 0 ? lines[h] : null;
        if (from != to || map[firstLine].Cells is not { Count: > 0 } cells)
            return new DocumentPick(text, Rows(Row(from), Row(to)), header is null || from == 0 ? null : Names(header, map[h].Cells, 0, int.MaxValue));
        var a = Column(cells, firstColumn);
        var b = Column(cells, lastColumn);
        var names = header is null ? null : Names(header, cells, a, b);
        if (a == 0 && b == cells.Count - 1)
            return new DocumentPick(text, Rows(Row(from), Row(to)), from == 0 ? null : names);
        var row = Row(from);
        return new DocumentPick(text, a == b ? $"cell {Letters(a)}{row}" : $"cells {Letters(a)}{row}:{Letters(b)}{row}", names);
    }

    /// <summary>
    /// A selection from character <paramref name="start"/> to <paramref name="end"/> of a hex dump as
    /// <see cref="FileInspector.Hex"/> writes one: the bytes it covers, in its hex or its text
    /// ("bytes 0x40-0x4F"), with <see cref="Bytes"/> the first and last; null with nothing selected.
    /// </summary>
    public static DocumentPick? InHex(string dump, int start, int end)
    {
        (start, end) = (Math.Clamp(Math.Min(start, end), 0, dump.Length), Math.Clamp(Math.Max(start, end), 0, dump.Length));
        if (start == end)
            return null;
        var first = ByteAt(dump, start, last: false);
        var last = ByteAt(dump, end - 1, last: true);
        if (first is not { } a || last is not { } b || b < a)
            return null;
        return new DocumentPick(dump[start..end], a == b ? $"byte 0x{a:X}" : $"bytes 0x{a:X}-0x{b:X}") { Bytes = (a, b) };
    }

    /// <summary>The first and last byte of a selection in a hex dump; null elsewhere.</summary>
    public (long First, long Last)? Bytes { get; init; }

    /// <summary>The byte a character of a hex dump is on: in its hex or its text; before the hex, the line's first (or the one before, for where a selection ends).</summary>
    private static long? ByteAt(string dump, int index, bool last)
    {
        // A line's break is its last character.
        var lineStart = index == 0 ? 0 : dump.LastIndexOf('\n', index - 1) + 1;
        var lineEnd = dump.IndexOf('\n', lineStart);
        var line = dump[lineStart..(lineEnd < 0 ? dump.Length : lineEnd)];
        if (line.Length < 8 || !long.TryParse(line.AsSpan(0, 8), System.Globalization.NumberStyles.HexNumber, null, out var offset))
            return null;
        var count = Math.Clamp(line.Length - TextColumn, 1, FileInspector.HexRow);
        var column = index - lineStart;
        int at;
        if (column < HexColumn)
            return last ? offset - 1 : offset;
        if (column < TextColumn - 1)
        {
            var i = column - HexColumn;
            at = (i >= 8 * 3 ? i - 1 : i) / 3;
        }
        else
            at = column - TextColumn;
        return offset + Math.Clamp(at, 0, count - 1);
    }

    /// <summary>Where a hex dump's line has its bytes in hex, and as text.</summary>
    private const int HexColumn = 10, TextColumn = HexColumn + FileInspector.HexRow * 3 + 2;

    private static string Shown(List<MdSpan> line) => string.Concat(line.Select(s => s.Text));

    private static int LineAt(string text, int index)
    {
        var line = 0;
        for (var i = 0; i < index; i++)
            if (text[i] == '\n')
                line++;
        return line;
    }

    /// <summary>The line a character of the lines joined by breaks is on, and where in it; a line's break counts as its last.</summary>
    private static (int Line, int Column) Place(List<string> lines, int index)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (index <= lines[i].Length)
                return (i, index);
            index -= lines[i].Length + 1;
        }
        return (lines.Count - 1, lines[^1].Length);
    }

    /// <summary>The column a character of a table's row is in: the gap after a cell is the cell's.</summary>
    private static int Column(IReadOnlyList<int> cells, int at)
    {
        var column = 0;
        while (column + 1 < cells.Count && cells[column + 1] <= at)
            column++;
        return column;
    }

    /// <summary>The header's names of columns <paramref name="a"/> to <paramref name="b"/>.</summary>
    private static List<string>? Names(string header, IReadOnlyList<int>? cells, int a, int b)
    {
        if (cells is not { Count: > 0 })
            return null;
        var names = new List<string>();
        for (var c = a; c <= Math.Min(b, cells.Count - 1); c++)
        {
            var from = Math.Min(cells[c], header.Length);
            var to = c + 1 < cells.Count ? Math.Min(cells[c + 1], header.Length) : header.Length;
            names.Add(header[from..to].Trim());
        }
        return names;
    }

    /// <summary>A spreadsheet's name for a column: A for the first, Z, AA, AB...</summary>
    internal static string Letters(int column)
    {
        var name = "";
        for (column++; column > 0; column = (column - 1) / 26)
            name = (char)('A' + (column - 1) % 26) + name;
        return name;
    }

    private static string Lines(int first, int last) => first == last ? $"line {first}" : $"lines {first}-{last}";

    private static string Rows(int first, int last) => first == last ? $"row {first}" : $"rows {first}-{last}";
}
