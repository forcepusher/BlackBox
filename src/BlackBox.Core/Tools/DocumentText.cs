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
    /// <param name="ct">Stop: looked at page by page (a PDF of a thousand pages takes minutes) and row by row.</param>
    public static string Read(string path, CancellationToken ct = default)
    {
        // Shared for writing too: Excel and Word keep an open file locked against plain readers.
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            return Kind(path) switch
            {
                "pdf" => Pdf(file, ct),
                "docx" => Docx(file, ct),
                "xlsx" => Xlsx(file, ct),
                _ => Pptx(file, ct),
            };
        }
        catch (Exception ex) when (ex is not (IOException or UnauthorizedAccessException or OperationCanceledException))
        {
            // A damaged file, one that is password-protected, or not what its name says.
            throw new InvalidDataException($"{Path.GetFileName(path)} could not be read as a document: {ex.Message}", ex);
        }
    }

    // ---- PDF -------------------------------------------------------------------------------------

    /// <summary>A PDF's text layer, page by page; also what fetch_page shows of a PDF on the web.</summary>
    internal static string Pdf(Stream file, CancellationToken ct = default)
    {
        using var pdf = PdfDocument.Open(file);
        var sb = new StringBuilder();
        var withText = 0;
        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();
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

    private static string Docx(Stream file, CancellationToken ct)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var body = Load(zip, "word/document.xml").Root?.Element(W + "body") ?? throw new InvalidDataException("no document body");
        var sb = new StringBuilder();
        foreach (var block in body.Elements())
        {
            ct.ThrowIfCancellationRequested();
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

    /// <summary>
    /// Every sheet, every filled cell by its address ("B7 =SUM(B2:B6) → $2,150.00"), a row per
    /// line, numbers as Excel shows them (in dollars, as percentages, dates as 2026-11-01), and
    /// which cells are bold. Without formats a model asked for amounts in dollars couldn't see
    /// whether they were, and unzipped the workbook to look; a date read 46327.
    /// </summary>
    private static string Xlsx(Stream file, CancellationToken ct)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var shared = zip.GetEntry("xl/sharedStrings.xml") is null ? []
            : Load(zip, "xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList();
        var styles = CellFormats(zip);
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
            var bold = new List<string>();
            foreach (var row in data.Element(S + "sheetData")?.Elements(S + "row") ?? [])
            {
                ct.ThrowIfCancellationRequested();
                var cells = new List<string>();
                foreach (var c in row.Elements(S + "c"))
                {
                    var style = int.TryParse((string?)c.Attribute("s"), out var s) && s >= 0 && s < styles.Count ? styles[s] : default;
                    if (Cell(c, shared, style.Format) is not { Length: > 0 } cell)
                        continue;
                    cells.Add(cell);
                    if (style.Bold)
                        bold.Add((string?)c.Attribute("r") ?? "");
                }
                if (cells.Count > 0)
                    sb.AppendJoin(" | ", cells).AppendLine();
            }
            if (bold.Count > 0)
                sb.Append("(bold: ").AppendJoin(", ", bold).AppendLine(")");
            // What else a person sees on the sheet: a title across merged cells, its charts.
            if (data.Element(S + "mergeCells")?.Elements(S + "mergeCell").Select(m => (string?)m.Attribute("ref")).OfType<string>().ToList() is { Count: > 0 } merged)
                sb.Append("(merged: ").AppendJoin(", ", merged).AppendLine(")");
            if (Charts(zip, target) is > 0 and var charts)
                sb.AppendLine(charts == 1 ? "(1 chart)" : $"({charts} charts)");
        }
        return sb.ToString();
    }

    /// <summary>The charts drawn on a sheet.</summary>
    private static int Charts(ZipArchive zip, string sheet)
    {
        var folder = sheet[..(sheet.LastIndexOf('/') + 1)];
        var rels = $"{folder}_rels/{sheet[folder.Length..]}.rels";
        if (zip.GetEntry(rels) is null)
            return 0;
        XNamespace c = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        return Load(zip, rels).Root!.Elements(Pr + "Relationship")
            .Where(r => ((string?)r.Attribute("Type"))?.EndsWith("/drawing", StringComparison.Ordinal) == true && r.Attribute("Target") is not null)
            .Select(r => Resolve(folder, (string)r.Attribute("Target")!))
            .Where(d => zip.GetEntry(d) is not null)
            .Sum(d => Load(zip, d).Descendants(c + "chart").Count());
    }

    private static string Cell(XElement c, List<string> shared, string? format)
    {
        var at = (string?)c.Attribute("r") ?? "";
        var value = (string?)c.Element(S + "v");
        var shown = (string?)c.Attribute("t") switch
        {
            "s" when int.TryParse(value, out var i) && i < shared.Count => shared[i],
            "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
            "b" => value == "1" ? "TRUE" : "FALSE",
            // Numbers as the format shows them, else with the 15 digits Excel shows: the file has
            // 512.29999999999995 for 512.3.
            null or "n" when double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
                => Formatted(n, format) ?? n.ToString("G15", System.Globalization.CultureInfo.InvariantCulture),
            _ => value ?? "",
        };
        if (c.Element(S + "f") is { } f)
            // A shared formula is written out only in its first cell; the others just point at it.
            shown = $"={(f.Value.Length > 0 ? f.Value : "(shared formula)")} → {shown}";
        return shown.Length == 0 ? "" : $"{at} {shown}";
    }

    /// <summary>Each cell style's number format code (null for General) and whether its font is bold, by style index.</summary>
    private static List<(string? Format, bool Bold)> CellFormats(ZipArchive zip)
    {
        if (zip.GetEntry("xl/styles.xml") is null)
            return [];
        var styles = Load(zip, "xl/styles.xml").Root!;
        var codes = styles.Element(S + "numFmts")?.Elements(S + "numFmt")
            .Where(f => f.Attribute("numFmtId") is not null)
            .GroupBy(f => (int)f.Attribute("numFmtId")!).ToDictionary(g => g.Key, g => (string?)g.First().Attribute("formatCode")) ?? [];
        var bold = styles.Element(S + "fonts")?.Elements(S + "font").Select(f => f.Element(S + "b") is { } b && (string?)b.Attribute("val") is null or "1" or "true").ToList() ?? [];
        return styles.Element(S + "cellXfs")?.Elements(S + "xf").Select(xf =>
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            var font = (int?)xf.Attribute("fontId") ?? 0;
            return (codes.GetValueOrDefault(id) ?? BuiltInFormat(id), font >= 0 && font < bold.Count && bold[font]);
        }).ToList() ?? [];
    }

    /// <summary>The number formats Excel has without writing them down, the ones that change how a number reads.</summary>
    private static string? BuiltInFormat(int id) => id switch
    {
        3 => "#,##0",
        4 => "#,##0.00",
        9 => "0%",
        10 => "0.00%",
        >= 14 and <= 17 => "yyyy-mm-dd",
        _ => null,
    };

    /// <summary>
    /// A number as its format shows it, written so that edit_file reads it back as the same number
    /// in the same format: $1,200.00, 1,200, 40%, 2026-11-01. Null for formats it doesn't know
    /// (times, fractions, custom text), which show the number as it is.
    /// </summary>
    internal static string? Formatted(double n, string? format)
    {
        if (format is null || format.Contains('h') || format.Contains('s') && !format.Contains('#'))
            return null;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var decimals = format.IndexOf('.') is var dot and >= 0 ? format[(dot + 1)..].TakeWhile(ch => ch == '0').Count() : 0;
        if (format.Contains('%'))
            return (n * 100).ToString("F" + decimals, inv) + "%";
        if (format.Contains('y') && format.Contains('d'))
            return n is >= 1 and < 2958466 ? DateTime.FromOADate(n).ToString("yyyy-MM-dd", inv) : null;
        if (!format.Contains('0'))
            return null;
        var number = Math.Abs(n).ToString((format.Contains(",") ? "N" : "F") + decimals, inv);
        var symbol = format.FirstOrDefault(ch => ch is '$' or '€' or '£' or '¥' or '₹');
        var sign = n < 0 ? "-" : "";
        if (symbol == default)
            return sign + number;
        return format.IndexOf(symbol) < format.IndexOf('0') ? $"{sign}{symbol}{number}" : $"{sign}{number} {symbol}";
    }

    // ---- PowerPoint ------------------------------------------------------------------------------

    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static string Pptx(Stream file, CancellationToken ct)
    {
        using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        var targets = Relations(zip, "ppt/_rels/presentation.xml.rels", "ppt/");
        var ids = Load(zip, "ppt/presentation.xml").Root!.Element(P + "sldIdLst")?.Elements(P + "sldId").Select(s => (string?)s.Attribute(R + "id")).ToList() ?? [];
        var sb = new StringBuilder();
        var number = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            number++;
            if (id is null || !targets.TryGetValue(id, out var target) || zip.GetEntry(target) is null)
                continue;
            sb.Append("--- slide ").Append(number).Append(" of ").Append(ids.Count).AppendLine(" ---");
            foreach (var para in Load(zip, target).Descendants(A + "p"))
                if (string.Concat(para.Descendants(A + "t").Select(t => t.Value)) is { Length: > 0 } text)
                    sb.AppendLine(text);
            // The speaker notes, which went unseen: asked to change them, the model had nothing to go on.
            if (Notes(zip, target) is { Count: > 0 } notes)
            {
                sb.AppendLine("Speaker notes:");
                foreach (var line in notes)
                    sb.AppendLine(line);
            }
        }
        return sb.ToString();
    }

    /// <summary>The paragraphs of a slide's speaker notes (their body, not the slide number or picture); empty when it has none.</summary>
    private static List<string> Notes(ZipArchive zip, string slide)
    {
        var folder = slide[..(slide.LastIndexOf('/') + 1)];
        var rels = $"{folder}_rels/{slide[folder.Length..]}.rels";
        if (zip.GetEntry(rels) is null)
            return [];
        var target = Load(zip, rels).Root!.Elements(Pr + "Relationship")
            .FirstOrDefault(r => ((string?)r.Attribute("Type"))?.EndsWith("/notesSlide", StringComparison.Ordinal) == true)?.Attribute("Target")?.Value;
        if (target is null || zip.GetEntry(Resolve(folder, target)) is null)
            return [];
        var body = Load(zip, Resolve(folder, target)).Descendants(P + "sp")
            .FirstOrDefault(sp => sp.Descendants(P + "ph").Any(ph => (string?)ph.Attribute("type") == "body"));
        return body?.Descendants(A + "p").Select(p => string.Concat(p.Descendants(A + "t").Select(t => t.Value))).Where(t => t.Length > 0).ToList() ?? [];
    }

    /// <summary>A relationship's target ("../notesSlides/notesSlide2.xml") as an entry of the package, from the folder of the part it belongs to.</summary>
    internal static string Resolve(string folder, string target)
    {
        if (target.StartsWith('/'))
            return target.TrimStart('/');
        var path = new List<string>(folder.Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (var piece in target.Split('/'))
        {
            if (piece == "..") { if (path.Count > 0) path.RemoveAt(path.Count - 1); }
            else if (piece is not ("." or "")) path.Add(piece);
        }
        return string.Join('/', path);
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
