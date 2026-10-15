using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace BlackBox.Core.Tools;

/// <summary>
/// The text of the documents people hand over most, for read_file: PDF (its text layer, page by
/// page), Word (.docx, with headings, lists, **bold** and tables), Excel (.xlsx, cell by cell with
/// the formulas) and PowerPoint (.pptx, slide by slide). Without it a PDF took qwen3.8 eight
/// minutes of inflating streams and mapping glyphs by hand in PowerShell.
/// </summary>
internal static class DocumentText
{
    public static bool Handles(string path) => Kind(path) is not null;

    private static string? Kind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "pdf",
        ".docx" or ".docm" or ".dotx" => "docx",
        ".xlsx" or ".xlsm" or ".xltx" => "xlsx",
        ".pptx" or ".pptm" or ".potx" => "pptx",
        _ => null,
    };

    /// <summary>The document as lines of text; throws <see cref="InvalidDataException"/> when it can't be read.</summary>
    public static string Read(string path)
    {
        // Shared for writing too: Excel and Word keep an open file locked against plain readers.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return Kind(path) switch
            {
                "pdf" => Pdf(file),
                "docx" => Docx(file),
                "xlsx" => Xlsx(file),
                _ => Pptx(file),
            };
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException))
        {
            // A damaged file, one that is password-protected, or not what its name says.
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be read as a document: {ex.Message}", ex);
        }
    }

    // ---- PDF -------------------------------------------------------------------------------------

    /// <summary>A PDF's text layer, page by page; also what fetch_page shows of a PDF on the web.</summary>
    internal static string Pdf(Stream file)
    {
        using var pdf = PdfDocument.Open(file);
        var sb = new StringBuilder();
        var withText = 0;
        foreach (var page in pdf.GetPages())
        {
            var text = ContentOrderTextExtractor.GetText(page).Trim();
            sb.Append("--- page ").Append(page.Number).Append(" of ").Append(pdf.NumberOfPages).AppendLine(" ---");
            if (text.Length == 0)
                sb.AppendLine("(no text on this page: a scan or a picture)");
            else
            {
                sb.AppendLine(text);
                withText++;
            }
        }
        return withText > 0 ? sb.ToString()
            : $"(This PDF has no text layer: its {pdf.NumberOfPages} page(s) are pictures, such as a scan. Look at it on screen, or use an OCR tool.)";
    }

    // ---- Word ------------------------------------------------------------------------------------

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Docx(Stream file)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var body = Load(zip, "word/document.xml").Root?.Element(W + "body") ?? throw new InvalidDataException("no document body");
        var sb = new StringBuilder();
        foreach (var block in body.Elements())
        {
            if (block.Name == W + "p")
                sb.AppendLine(Paragraph(block));
            else if (block.Name == W + "tbl")
                foreach (var row in block.Elements(W + "tr"))
                    sb.Append("| ").AppendJoin(" | ", row.Elements(W + "tc").Select(c => string.Join(" ", c.Elements(W + "p").Select(Paragraph)).Trim())).AppendLine(" |");
        }
        return sb.ToString();
    }

    /// <summary>A paragraph's text: "# " for a heading, "- " for a list item, bold runs in **.</summary>
    private static string Paragraph(XElement p)
    {
        var style = (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") ?? "";
        var prefix = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) && int.TryParse(style[7..], out var level) ? new string('#', Math.Clamp(level, 1, 6)) + " "
            : style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? "# "
            : p.Element(W + "pPr")?.Element(W + "numPr") is not null ? "- " : "";
        var sb = new StringBuilder(prefix);
        var bold = false;
        foreach (var run in p.Descendants(W + "r"))
        {
            var text = string.Concat(run.Elements().Select(e => e.Name == W + "t" ? e.Value : e.Name == W + "tab" ? "\t" : e.Name == W + "br" ? "\n" : ""));
            if (text.Length == 0) continue;
            var b = run.Element(W + "rPr")?.Element(W + "b") is { } bEl && (string?)bEl.Attribute(W + "val") is null or "1" or "true";
            if (b != bold && text.Trim().Length > 0)
            {
                sb.Append("**");
                bold = b;
            }
            sb.Append(text);
        }
        if (bold) sb.Append("**");
        return sb.ToString().Replace("****", "");
    }

    // ---- Excel -----------------------------------------------------------------------------------

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Pr = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Every sheet, every filled cell by its address ("B7 =SUM(B2:B6) → 2150"), a row per line.</summary>
    private static string Xlsx(Stream file)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? []
            : Load(zip, "xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();
        var targets = Relations(zip, "xl/_rels/workbook.xml.rels", "xl/");
        var sb = new StringBuilder();
        foreach (var sheet in Load(zip, "xl/workbook.xml").Root!.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            var name = (string?)sheet.Attribute("name") ?? "?";
            if ((string?)sheet.Attribute(R + "id") is not { } id || !targets.TryGetValue(id, out var target) || zip.GetEntry(target) is null)
                continue;
            var data = Load(zip, target).Root!;
            sb.Append("# Sheet \"").Append(name).Append('"');
            if ((string?)data.Element(S + "dimension")?.Attribute("ref") is { } range)
                sb.Append(" (").Append(range).Append(')');
            sb.AppendLine();
            foreach (var row in data.Element(S + "sheetData")?.Elements(S + "row") ?? [])
            {
                var cells = row.Elements(S + "c").Select(c => Cell(c, shared)).Where(c => c.Length > 0).ToList();
                if (cells.Count > 0)
                    sb.AppendJoin(" | ", cells).AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string Cell(XElement c, List<string> shared)
    {
        var at = (string?)c.Attribute("r") ?? "";
        var value = (string?)c.Element(S + "v");
        var shown = (string?)c.Attribute("t") switch
        {
            "s" when int.TryParse(value, out var i) && i < shared.Count => shared[i],
            "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
            "b" => value == "1" ? "TRUE" : "FALSE",
            // Numbers with the 15 digits Excel shows: the file has 512.29999999999995 for 512.3.
            null or "n" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
                => n.ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
            _ => value ?? "",
        };
        if (c.Element(S + "f") is { } f)
            // A shared formula is written out only in its first cell; the others just point at it.
            shown = $"={(f.Value.Length > 0 ? f.Value : "(shared formula)")} → {shown}";
        return shown.Length == 0 ? "" : $"{at} {shown}";
    }

    // ---- PowerPoint ------------------------------------------------------------------------------

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static string Pptx(Stream file)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var targets = Relations(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
        var ids = Load(zip, "ppt/presentation.xml").Root!.Element(P + "sldIdLst")?.Elements(P + "sldId").Select(s => (string?)s.Attribute(R + "id")).ToList() ?? [];
        var sb = new StringBuilder();
        var number = 0;
        foreach (var id in ids)
        {
            number++;
            if (id is null || !targets.TryGetValue(id, out var target) || zip.GetEntry(target) is null)
                continue;
            sb.Append("--- slide ").Append(number).Append(" of ").Append(ids.Count).AppendLine(" ---");
            foreach (var para in Load(zip, target).Descendants(A + "p"))
                if (string.Concat(para.Descendants(A + "t").Select(t => t.Value)) is { Length: > 0 } text)
                    sb.AppendLine(text);
        }
        return sb.ToString();
    }

    // ---- the zip package -------------------------------------------------------------------------

    private static XDocument Load(ZipArchive zip, string entry)
    {
        using var stream = (zip.GetEntry(entry) ?? throw new InvalidDataException($"{entry} is missing")).Open();
        return XDocument.Load(stream);
    }

    /// <summary>A part's relationships: id to the entry it points at, relative to <paramref name="folder"/>.</summary>
    private static Dictionary<string, string> Relations(ZipArchive zip, string rels, string folder) =>
        zip.GetEntry(rels) is null ? []
            : Load(zip, rels).Root!.Elements(Pr + "Relationship")
                .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null)
                .ToDictionary(r => (string)r.Attribute("Id")!, r => ((string)r.Attribute("Target")!).TrimStart('/') is var t && t.StartsWith(folder, StringComparison.Ordinal) ? t : folder + t);
}
