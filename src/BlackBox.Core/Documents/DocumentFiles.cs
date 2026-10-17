using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UglyToad.PdfPig;

namespace BlackBox.Core.Documents;

/// <summary>
/// write_file and edit_file for documents: Word, Excel and PowerPoint files and PDFs made from
/// Markdown, and changed in place with their formatting kept. Before, a model made them with
/// PowerShell and the Office apps' COM interfaces, which took qwen3.8 24 minutes for a budget
/// workbook, or not at all on a PC without Office.
/// </summary>
internal static partial class DocumentFiles
{
    /// <summary>"docx", "xlsx", "pptx" or "pdf" for a document write_file makes from Markdown; null for a plain file.</summary>
    public static string? WriteKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => "docx",
        ".xlsx" => "xlsx",
        ".pptx" => "pptx",
        ".pdf" => "pdf",
        _ => null,
    };

    /// <summary>"docx", "xlsx" or "pptx" for a document edit_file changes in place; "pdf" for one it can't.</summary>
    public static string? EditKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" or ".docm" => "docx",
        ".xlsx" or ".xlsm" => "xlsx",
        ".pptx" or ".pptm" => "pptx",
        ".pdf" => "pdf",
        _ => null,
    };

    /// <summary>
    /// The Markdown each document made here came from, by path, to add to it with append; and what
    /// its file was. The last ones made: kept for the process's life, every document's whole text
    /// only added up.
    /// </summary>
    private static readonly RecentMap<string, (string Hash, string Markdown)> Made = new(64, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes the document at <paramref name="path"/> from Markdown; returns what to tell the model.
    /// Throws <see cref="InvalidOperationException"/> with the reason when it won't.
    /// </summary>
    /// <param name="resolve">Turns a picture's path as written into a full one.</param>
    public static string Write(string path, string markdown, bool append, Func<string, string> resolve)
    {
        var kind = WriteKind(path) ?? throw new ArgumentException("not a document", nameof(path));
        var name = Path.GetFileName(path);
        var existed = File.Exists(path);
        (string Hash, string Markdown) made = ("", "");
        var ours = existed && Made.TryGet(path, out made) && made.Hash == Hash(File.ReadAllBytes(path));
        if (append)
        {
            if (!ours)
                throw new InvalidOperationException($"append adds to a document write_file made in this task, unchanged since. To add to {name}, " +
                    (kind == "pdf" ? "write it again whole." : "use edit_file: old_text its last paragraph, new_text that paragraph and what comes after it."));
            markdown = made.Markdown.TrimEnd() + "\n\n" + markdown;
        }
        else if (existed && !ours)
            throw new InvalidOperationException($"{name} already exists, and writing it would replace it whole, its formatting with it. " +
                (kind == "pdf" ? "" : "To change something in it, use edit_file, which keeps the rest as it is. ") +
                "To replace it anyway, delete it first, or write to a new name.");

        var blocks = MarkdownDocument.Parse(markdown);
        var notes = new List<string>();
        var bytes = kind switch
        {
            "docx" => DocxWriter.Write(blocks, resolve, notes),
            "xlsx" => XlsxWriter.Write(blocks, notes),
            "pptx" => PptxWriter.Write(blocks, resolve, notes),
            _ => PdfWriter.Write(blocks, resolve, notes),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Save(path, bytes);
        Made.Set(path, (Hash(bytes), markdown));
        var what = $"{(append ? "Added to" : existed ? "Overwrote" : "Created")} {path}: {Describe(kind, blocks, bytes)}.";
        return notes.Count == 0 ? what : what + " " + string.Join(" ", notes);
    }

    /// <summary>
    /// Replaces text in a Word, PowerPoint or Excel file, the rest of it as it was; returns what
    /// changed. Throws <see cref="InvalidOperationException"/> with what to tell the model when it can't.
    /// </summary>
    public static string Edit(string path, string oldText, string newText, bool all)
    {
        var kind = EditKind(path);
        if (kind is "docx" or "pptx" && oldText.Length == 0)
            throw new InvalidOperationException("old_text must not be empty. To add text, give the paragraph it goes after as old_text, and that paragraph followed by the new text as new_text.");
        if (kind == "pdf")
            throw new InvalidOperationException("A PDF can't be changed in place. Read it with read_file and write a new one with write_file " +
                "(Markdown), or change the document it was made from and make the PDF again.");
        byte[] original;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            original = new byte[file.Length];
            file.ReadExactly(original);
        }
        using var buffer = new MemoryStream();
        buffer.Write(original);
        buffer.Position = 0;
        string message;
        try
        {
            using var zip = new ZipArchive(buffer, ZipArchiveMode.Update, leaveOpen: true);
            var parts = new PartSet(zip);
            if (kind == "xlsx")
                message = SheetEdit.Edit(parts, oldText, newText, all);
            else
            {
                // A deck's speaker notes too: read_file shows them.
                var names = kind == "docx"
                    ? parts.Names.Where(n => n == "word/document.xml" || WordPart().IsMatch(n)).OrderBy(n => n == "word/document.xml" ? 0 : 1).ThenBy(n => n)
                    : parts.Names.Where(n => SlidePart().IsMatch(n)).OrderBy(n => int.Parse(SlidePart().Match(n).Groups[1].Value))
                        .Concat(parts.Names.Where(n => NotesPart().IsMatch(n)).OrderBy(n => int.Parse(NotesPart().Match(n).Groups[1].Value)));
                var documents = names.ToDictionary(n => n, parts.Get);
                if (documents.Count == 0)
                    throw new InvalidOperationException($"{Path.GetFileName(path)} has no text to change.");
                var (count, changed) = TextEdit.Replace(documents, slides: kind == "pptx", oldText, newText, all);
                foreach (var part in changed) parts.Changed(part);
                message = count > 1 ? $"Replaced {count} occurrences." : "Replaced the text; the formatting around it is as it was.";
            }
            parts.Save();
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} could not be read as a document: {ex.Message}", ex);
        }
        Save(path, buffer.ToArray());
        Made.Remove(path);
        return $"Edited {path}: {message}";
    }

    /// <summary>Writes the file through a temporary one, so a failure leaves the old one whole.</summary>
    private static void Save(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (ex is IOException && File.Exists(path))
                throw new InvalidOperationException($"{Path.GetFileName(path)} is open in another program, which keeps it locked: close it there (save your changes first), then try again.", ex);
            throw;
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    /// <summary>What was made: "a Word document: 3 headings, 12 paragraphs, 1 table".</summary>
    private static string Describe(string kind, List<Block> blocks, byte[] bytes)
    {
        static string Count(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
        switch (kind)
        {
            case "pdf":
                using (var pdf = PdfDocument.Open(bytes))
                    return $"a PDF of {Count(pdf.NumberOfPages, "page")}";
            case "pptx":
                var slides = PptxWriter.Slides(blocks);
                return $"a presentation of {Count(slides.Count, "slide")}: " + string.Join("; ", slides.Select((s, i) =>
                    s.OfType<HeadingBlock>().FirstOrDefault() is { } h ? MarkdownDocument.Plain(h.Text) : $"slide {i + 1} (no title)"));
            case "xlsx":
                using (var zip = new ZipArchive(new MemoryStream(bytes)))
                {
                    var parts = new PartSet(zip);
                    var sheets = parts.Get("xl/workbook.xml").Root!.Descendants(XlsxWriter.S + "sheet").Select((s, i) =>
                    {
                        var range = (string?)parts.Get($"xl/worksheets/sheet{i + 1}.xml").Root!.Element(XlsxWriter.S + "dimension")?.Attribute("ref");
                        return $"\"{(string?)s.Attribute("name")}\" ({range})";
                    }).ToList();
                    return $"a workbook with {Count(sheets.Count, "sheet")}: {string.Join(", ", sheets)}";
                }
            default:
                var counts = new[]
                {
                    (blocks.OfType<HeadingBlock>().Count(), "heading"), (blocks.OfType<ParagraphBlock>().Count(), "paragraph"),
                    (blocks.OfType<ListItemBlock>().Count(), "list item"), (blocks.OfType<TableBlock>().Count(), "table"),
                    (blocks.OfType<ImageBlock>().Count(), "picture"),
                }.Where(c => c.Item1 > 0).Select(c => Count(c.Item1, c.Item2)).ToList();
                return "a Word document" + (counts.Count > 0 ? ": " + string.Join(", ", counts) : " with nothing in it");
        }
    }

    [GeneratedRegex(@"^word/(header\d*|footer\d*|footnotes|endnotes)\.xml$")]
    private static partial Regex WordPart();

    [GeneratedRegex(@"^ppt/slides/slide(\d+)\.xml$")]
    private static partial Regex SlidePart();

    [GeneratedRegex(@"^ppt/notesSlides/notesSlide(\d+)\.xml$")]
    private static partial Regex NotesPart();
}

/// <summary>The XML parts of an Office file open for changing; the changed ones are written back on <see cref="Save"/>.</summary>
internal sealed class PartSet(ZipArchive zip)
{
    private readonly Dictionary<string, XDocument> _loaded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deleted = new(StringComparer.Ordinal);

    public IEnumerable<string> Names => zip.Entries.Select(e => e.FullName).Where(n => !_deleted.Contains(n)).ToList();

    public bool Exists(string name) => !_deleted.Contains(name) && (_loaded.ContainsKey(name) || zip.GetEntry(name) is not null);

    public XDocument Get(string name)
    {
        if (_loaded.TryGetValue(name, out var loaded)) return loaded;
        using var stream = (zip.GetEntry(name) ?? throw new InvalidDataException($"{name} is missing")).Open();
        return _loaded[name] = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
    }

    public void Changed(string name) => _changed.Add(name);

    public void Delete(string name) => _deleted.Add(name);

    public void Save()
    {
        foreach (var name in _deleted)
            zip.GetEntry(name)?.Delete();
        foreach (var name in _changed.Where(n => !_deleted.Contains(n)))
        {
            zip.GetEntry(name)?.Delete();
            using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
            var xml = _loaded[name];
            xml.Declaration ??= new XDeclaration("1.0", "UTF-8", "yes");
            xml.Save(stream, SaveOptions.DisableFormatting);
        }
    }

    /// <summary>A relationship's target as a part name: "worksheets/sheet1.xml" from "xl" is "xl/worksheets/sheet1.xml".</summary>
    public static string Resolve(string folder, string target)
    {
        if (target.StartsWith('/')) return target[1..];
        var parts = (folder.Length > 0 ? folder + "/" + target : target).Split('/').ToList();
        for (var i = 0; i < parts.Count; i++)
        {
            if (parts[i] == "..") { parts.RemoveAt(i); if (i > 0) parts.RemoveAt(--i); i--; }
            else if (parts[i] == ".") parts.RemoveAt(i--);
        }
        return string.Join('/', parts);
    }
}
