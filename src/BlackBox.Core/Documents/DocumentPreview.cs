using System.Text;
using BlackBox.Core.Media;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Documents;

/// <summary>How the Document preview panel shows a file.</summary>
public enum DocumentKind
{
    /// <summary>Formatted as a reply is (.md).</summary>
    Markdown,

    /// <summary>A table (.csv, .tsv), its first rows.</summary>
    Table,

    /// <summary>A picture, fitted to the panel.</summary>
    Picture,

    /// <summary>An animation, a video, a sound or a 3D model, with the chat's player for it.</summary>
    Media,

    /// <summary>
    /// A file whose look only its own program knows (PDF, Word, Excel, PowerPoint, HTML, RTF): shown
    /// by the program's previewer that Windows has for it, as Explorer's preview pane shows it; without
    /// one, as its text.
    /// </summary>
    Document,

    /// <summary>Any other file that is text (code, .txt, .json, a log), in a fixed-width font.</summary>
    Text,

    /// <summary>Nothing to show of it: its name and size.</summary>
    None,
}

/// <summary>
/// What the Document preview panel reads of a file: which kind it is, and its text for the kinds
/// BlackBox draws itself.
/// </summary>
public static class DocumentPreview
{
    /// <summary>The most of a text file the panel shows: a long one shows its start.</summary>
    public const int MaxChars = 200_000;

    /// <summary>The most rows of a table the panel shows.</summary>
    public const int MaxRows = 500;

    /// <summary>The most columns of a table the panel shows.</summary>
    public const int MaxColumns = 40;

    public static DocumentKind KindOf(string path)
    {
        if (MarkdownFiles.Handles(path))
            return DocumentKind.Markdown;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".csv" or ".tsv")
            return DocumentKind.Table;
        if (DocumentText.Handles(path) || ext is ".html" or ".htm" or ".xhtml" or ".mht" or ".mhtml" or ".rtf" or ".doc" or ".xls" or ".ppt" or ".odt" or ".ods" or ".odp")
            return DocumentKind.Document;
        switch (MediaPreview.KindOf(path))
        {
            case "image": return DocumentKind.Picture;
            case "animation" or "video" or "audio" or "3d": return DocumentKind.Media;
        }
        return LooksLikeText(path) ? DocumentKind.Text : DocumentKind.None;
    }

    /// <summary>
    /// Text, by its start: no NUL characters in the first 8 KB once read in the file's own encoding
    /// (a UTF-16 file has them between its letters as bytes). False for a file that can't be read.
    /// </summary>
    public static bool LooksLikeText(string path)
    {
        try
        {
            using var reader = TextFiles.OpenText(path);
            var buffer = new char[8192];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return buffer.AsSpan(0, read).IndexOf('\0') < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The file's text in its own encoding, up to <see cref="MaxChars"/> characters ending on a whole line; <c>Cut</c> when there is more.</summary>
    public static (string Text, bool Cut) ReadText(string path) => MarkdownFiles.ReadStart(path, MaxChars);

    /// <summary>
    /// A .csv or .tsv as a Markdown table, its first row the header: up to <see cref="MaxRows"/> rows
    /// and <see cref="MaxColumns"/> columns, with how many there are in all. A .csv whose lines have
    /// more semicolons than commas (Excel's in much of Europe) is split at the semicolons.
    /// </summary>
    public static (string Markdown, int Rows, int Columns) Table(string path)
    {
        var (text, _) = MarkdownFiles.ReadStart(path, 4_000_000);
        var rows = new List<List<string>>();
        var separator = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : Separator(text);
        var total = 0;
        foreach (var row in Rows(text, separator))
        {
            total++;
            if (rows.Count <= MaxRows)
                rows.Add(row);
        }
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columns == 0)
            return ("", 0, 0);
        var shown = Math.Min(columns, MaxColumns);
        var sb = new StringBuilder();
        void Line(List<string> cells)
        {
            sb.Append('|');
            for (var i = 0; i < shown; i++)
                sb.Append(' ').Append(Cell(i < cells.Count ? cells[i] : "")).Append(" |");
            sb.Append('\n');
        }
        Line(rows[0]);
        sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", shown))).Append('\n');
        foreach (var row in rows.Skip(1).Take(MaxRows))
            Line(row);
        return (sb.ToString(), Math.Max(0, total - 1), columns);
    }

    /// <summary>A cell as one line of a Markdown table: its bars escaped, its line breaks as spaces.</summary>
    private static string Cell(string text) => text.Replace("\r\n", " ").Replace('\n', ' ').Replace("|", "\\|").Trim();

    private static char Separator(string text)
    {
        var end = text.IndexOf('\n');
        var first = end < 0 ? text : text[..end];
        return first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
    }

    /// <summary>The rows of a CSV: fields in double quotes may hold the separator, line breaks and doubled quotes.</summary>
    internal static IEnumerable<List<string>> Rows(string text, char separator)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                    field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                    quoted = false;
            }
            else if (c == '"' && field.Length == 0)
                quoted = true;
            else if (c == separator)
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0)
                    yield return row;
                row = [];
            }
            else
                field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    /// <summary>
    /// A document's text, for one with no previewer on this PC: what read_file gives the agent of a
    /// PDF, Word, Excel or PowerPoint file; null for the other kinds (an HTML page shows its source).
    /// Throws <see cref="InvalidDataException"/> for a file that can't be read as one.
    /// </summary>
    public static string? DocumentAsText(string path, CancellationToken ct = default) =>
        DocumentText.Handles(path) ? DocumentText.Read(path, ct) : null;
}
