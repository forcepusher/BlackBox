using System.Text;
using Sloperator.Core.Media;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Documents;

/// <summary>How the File inspector panel shows a file.</summary>
public enum InspectorKind
{
    /// <summary>Formatted as a reply is (.md).</summary>
    Markdown,

    /// <summary>A table (.csv, .tsv), its first rows.</summary>
    Table,

    /// <summary>A picture, fitted to the panel.</summary>
    Picture,

    /// <summary>An animated GIF, WebP or PNG, frame by frame (one with a single frame is a picture).</summary>
    Animation,

    /// <summary>A video, frame by frame.</summary>
    Video,

    /// <summary>A sound, as a waveform to zoom into and pick a stretch of.</summary>
    Sound,

    /// <summary>A 3D model, to turn, zoom and move about.</summary>
    Model,

    /// <summary>
    /// A file whose look only its own program knows (PDF, Word, Excel, PowerPoint, HTML, RTF): shown
    /// by the program's previewer that Windows has for it, as Explorer's preview pane shows it; without
    /// one, as its text.
    /// </summary>
    Document,

    /// <summary>Any other file that is text (code, .txt, .json, a log), in a fixed-width font.</summary>
    Text,

    /// <summary>Any other file: its bytes, in hex (<see cref="FileInspector.Hex"/>).</summary>
    None,
}

/// <summary>
/// What the File inspector panel reads of a file: which kind it is, and its text for the kinds
/// Sloperator draws itself.
/// </summary>
public static partial class FileInspector
{
    /// <summary>The most of a text file the panel shows: a long one shows its start.</summary>
    public const int MaxChars = 200_000;

    /// <summary>The most rows of a table the panel shows.</summary>
    public const int MaxRows = 500;

    /// <summary>The most columns of a table the panel shows.</summary>
    public const int MaxColumns = 40;

    public static InspectorKind KindOf(string path)
    {
        if (MarkdownFiles.Handles(path))
            return InspectorKind.Markdown;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".csv" or ".tsv")
            return InspectorKind.Table;
        if (DocumentText.Handles(path) || ext is ".html" or ".htm" or ".xhtml" or ".mht" or ".mhtml" or ".rtf" or ".doc" or ".xls" or ".ppt" or ".odt" or ".ods" or ".odp")
            return InspectorKind.Document;
        switch (MediaPreview.KindOf(path))
        {
            case "image": return InspectorKind.Picture;
            case "animation": return InspectorKind.Animation;
            case "video": return InspectorKind.Video;
            case "audio": return InspectorKind.Sound;
            case "3d": return InspectorKind.Model;
        }
        return LooksLikeText(path) ? InspectorKind.Text : InspectorKind.None;
    }

    /// <summary>
    /// The kinds of file the panel's Open file picker lists, each with its extensions (as
    /// <see cref="KindOf"/> sorts them; text is open-ended, so the common ones). Any other file opens too, in hex.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, InspectorKind Kind, string[] Extensions)> PickerTypes =
    [
        ("Documents", InspectorKind.Document, [".pdf", ".docx", ".docm", ".dotx", ".doc", ".xlsx", ".xlsm", ".xltx", ".xls", ".pptx", ".pptm", ".potx", ".ppt",
            ".odt", ".ods", ".odp", ".rtf", ".html", ".htm", ".xhtml", ".mht", ".mhtml"]),
        ("Markdown", InspectorKind.Markdown, [".md", ".markdown", ".mdown", ".mkd"]),
        ("Tables", InspectorKind.Table, [".csv", ".tsv"]),
        ("Pictures", InspectorKind.Picture, [".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".exr"]),
        ("Animations", InspectorKind.Animation, [".gif", ".webp", ".apng"]),
        ("Videos", InspectorKind.Video, [".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v"]),
        ("Sounds", InspectorKind.Sound, [".wav", ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac"]),
        ("3D models", InspectorKind.Model, [".glb", ".gltf", ".obj", ".ply", ".stl", ".fbx", ".usdz", ".3mf", ".dae"]),
        ("Text and code", InspectorKind.Text, [".txt", ".log", ".json", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".csproj", ".sln",
            ".cs", ".axaml", ".xaml", ".py", ".js", ".ts", ".tsx", ".jsx", ".css", ".c", ".h", ".cpp", ".hpp", ".java", ".kt", ".go", ".rs",
            ".rb", ".php", ".lua", ".gd", ".shader", ".hlsl", ".glsl", ".sql", ".ps1", ".bat", ".cmd", ".sh"]),
    ];

    /// <summary>
    /// A file in a format that is text, to edit as such (the Code editor's, on a file's right-click
    /// menu): Markdown, a table, a web page or an SVG, and the code and text of <see cref="PickerTypes"/>,
    /// by their extension. A picture, video, sound, 3D model, PDF or Office file never is. A file of no
    /// kind its name tells (README, .gitignore, a .props) is when its start reads as text, looked at only
    /// on this PC's own drives: on a share whose server is off, the menu would wait for it.
    /// </summary>
    public static bool IsText(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".html" or ".htm" or ".xhtml" or ".svg" || PickerTypes.Any(t => t.Kind is InspectorKind.Markdown or InspectorKind.Table or InspectorKind.Text && t.Extensions.Contains(ext)))
            return true;
        if (PickerTypes.Any(t => t.Extensions.Contains(ext)) || MediaPreview.KindOf(path) != "other")
            return false;
        return OnOwnDrive(path) && LooksLikeText(path);
    }

    private static bool OnOwnDrive(string path)
    {
        try
        {
            return !path.StartsWith(@"\\", StringComparison.Ordinal) && Path.GetPathRoot(path) is { Length: > 0 } root
                   && new DriveInfo(root).DriveType is DriveType.Fixed or DriveType.Removable;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>The most of a file <see cref="Hex"/> shows: its start.</summary>
    public const int MaxHexBytes = 64 * 1024;

    /// <summary>Bytes shown on each line of <see cref="Hex"/>.</summary>
    public const int HexRow = 16;

    /// <summary>
    /// A file's first <paramref name="max"/> bytes as a hex dump, a line each 16: "00000040  4D 5A 90 00
    /// …  MZ..", the offset first and the bytes as text (a dot for those that aren't letters) after;
    /// with how many bytes the file has.
    /// </summary>
    public static (string Text, long Length) Hex(string path, int max = MaxHexBytes)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(max, file.Length)];
        var read = 0;
        for (int n; read < bytes.Length && (n = file.Read(bytes, read, bytes.Length - read)) > 0;)
            read += n;
        var sb = new StringBuilder(read / HexRow * 80);
        for (var at = 0; at < read; at += HexRow)
        {
            if (at > 0)
                sb.Append('\n');
            sb.Append(at.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)).Append("  ");
            for (var i = 0; i < HexRow; i++)
            {
                if (i == 8)
                    sb.Append(' ');
                sb.Append(at + i < read ? bytes[at + i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture) + " " : "   ");
            }
            sb.Append(' ');
            for (var i = at; i < Math.Min(read, at + HexRow); i++)
                sb.Append(bytes[i] is >= 0x20 and < 0x7F ? (char)bytes[i] : '.');
        }
        return (sb.ToString(), file.Length);
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
    /// <param name="maxRows">Rows shown at most: fewer for the peek on a file's card in the chat.</param>
    /// <param name="read">Characters of the file read: a peek needs only its start.</param>
    public static (string Markdown, int Rows, int Columns) Table(string path, int maxRows = MaxRows, int maxColumns = MaxColumns, int read = 4_000_000)
    {
        var (text, _) = MarkdownFiles.ReadStart(path, read);
        var rows = new List<List<string>>();
        var separator = Path.GetExtension(path).Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : Separator(text);
        var total = 0;
        foreach (var row in Rows(text, separator))
        {
            total++;
            if (rows.Count <= maxRows)
                rows.Add(row);
        }
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        if (columns == 0)
            return ("", 0, 0);
        return (MarkdownTable(rows, maxRows, Math.Min(columns, maxColumns)), Math.Max(0, total - 1), columns);
    }

    /// <summary>Rows as a Markdown table, the first the header, then up to <paramref name="maxRows"/> more, <paramref name="shown"/> columns wide.</summary>
    private static string MarkdownTable(List<List<string>> rows, int maxRows, int shown)
    {
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
        foreach (var row in rows.Skip(1).Take(maxRows))
            Line(row);
        return sb.ToString();
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

    // ---- the peek on a file's card in the chat ---------------------------------------------------

    /// <summary>Lines of text a card's peek shows at most (it is cut off below anyway).</summary>
    public const int PeekLines = 14;

    /// <summary>
    /// The start of a file for its card in the chat, as the panel draws it: Markdown (a .md, the first
    /// rows of a CSV or of an Excel file's first sheet as a table, a Word or PowerPoint file's text, a
    /// web page's text) or plain text (code, a PDF's text); both null when there is nothing to show.
    /// The card looks for a PDF's first page and an Office file's own picture of itself
    /// (<see cref="SavedThumbnail"/>) first.
    /// </summary>
    public static (string? Markdown, string? Text) Peek(string path, InspectorKind kind)
    {
        switch (kind)
        {
            case InspectorKind.Markdown:
                return (Start(MarkdownFiles.ReadStart(path, 4000).Text), null);
            case InspectorKind.Table:
                var table = Table(path, maxRows: 8, maxColumns: 8, read: 64_000).Markdown;
                return (table.Length > 0 ? table : null, null);
            case InspectorKind.Text:
                return (null, Start(MarkdownFiles.ReadStart(path, 4000).Text));
            case InspectorKind.Document:
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".html" or ".htm" or ".xhtml")
                    return (Start(PageText(path)), null);
                if (DocumentAsText(path) is not { } text)
                    return (null, null);
                if (ext is ".xlsx" or ".xlsm" or ".xltx")
                    return SheetTable(text) is { } sheet ? (sheet, null) : (null, Start(text));
                return ext is ".docx" or ".docm" or ".dotx" or ".pptx" or ".pptm" or ".potx" ? (Start(text), null) : (null, Start(text));
            default:
                return (null, null);
        }
    }

    /// <summary>The first <see cref="PeekLines"/> lines from the first that isn't blank, each at most 200 characters; null when all are blank.</summary>
    internal static string? Start(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').SkipWhile(l => l.Trim().Length == 0).Take(PeekLines)
            .Select(l => l.Length > 200 ? l[..200] : l);
        var start = string.Join('\n', lines).TrimEnd();
        return start.Length > 0 ? start : null;
    }

    /// <summary>A web page's text as fetch_page reads one (its headings marked as Markdown's); its title when it has none.</summary>
    private static string PageText(string path)
    {
        var (html, _) = MarkdownFiles.ReadStart(path, 400_000);
        var (title, text) = Web.PageText.Extract(new AngleSharp.Html.Parser.HtmlParser().ParseDocument(html));
        return text.Trim().Length > 0 ? text : title;
    }

    /// <summary>
    /// The first sheet's first rows as a table, the cells where they are in it, from an Excel file's
    /// text as read_file gives it ("A2 Rent | B2 $1,200.00 | D2 =B2-C2 → $0.00": a formula's cell
    /// shows its value); null when the text has no cells.
    /// </summary>
    internal static string? SheetTable(string text)
    {
        var cells = new Dictionary<(int Row, int Column), string>();
        var sheets = 0;
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("# Sheet ", StringComparison.Ordinal) && ++sheets > 1)
                break;
            if (sheets == 0 || line.StartsWith('#') || line.StartsWith('('))
                continue;
            foreach (var piece in line.Split(" | "))
            {
                if (CellAt().Match(piece) is not { Success: true } m)
                    continue;
                var column = m.Groups[1].Value.Aggregate(0, (n, c) => n * 26 + c - 'A' + 1);
                var value = m.Groups[3].Value;
                if (value.StartsWith('=') && value.LastIndexOf(" → ", StringComparison.Ordinal) is var arrow and >= 0)
                    value = value[(arrow + 3)..];
                cells[(int.Parse(m.Groups[2].Value), column)] = value;
            }
        }
        if (cells.Count == 0)
            return null;
        var rows = cells.Keys.Select(k => k.Row).Distinct().Order().Take(9).ToList();
        var first = cells.Keys.Min(k => k.Column);
        var last = Math.Min(cells.Keys.Max(k => k.Column), first + 7);
        var table = rows.Select(r => Enumerable.Range(first, last - first + 1).Select(c => cells.GetValueOrDefault((r, c), "")).ToList()).ToList();
        return MarkdownTable(table, 8, last - first + 1);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^([A-Z]{1,3})(\d+) (.*)$", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex CellAt();

    /// <summary>
    /// The picture of its first page or slide that an Office file keeps in itself (PowerPoint saves
    /// one unless told not to; Word and Excel when "Save thumbnail" is ticked) or an OpenDocument file
    /// (always): JPEG or PNG bytes, null without one. It is what Explorer shows as the file's thumbnail.
    /// </summary>
    public static byte[]? SavedThumbnail(string path)
    {
        if (Path.GetExtension(path).ToLowerInvariant() is not (".docx" or ".docm" or ".dotx" or ".xlsx" or ".xlsm" or ".xltx" or ".pptx" or ".pptm" or ".potx" or ".odt" or ".ods" or ".odp"))
            return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e =>
                (e.FullName.StartsWith("docProps/thumbnail.", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("Thumbnails/thumbnail.png", StringComparison.OrdinalIgnoreCase))
                && Path.GetExtension(e.FullName).ToLowerInvariant() is ".jpeg" or ".jpg" or ".png");
            if (entry is null || entry.Length is 0 or > 8_000_000)
                return null;
            using var stream = entry.Open();
            using var bytes = new MemoryStream();
            stream.CopyTo(bytes);
            return bytes.ToArray();
        }
        catch (InvalidDataException) { return null; } // not a zip: damaged, or an old .doc named .docx
    }
}
