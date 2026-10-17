using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BlackBox.Core.Documents;

/// <summary>
/// Markdown into an Excel workbook: each heading starts a sheet named after it; tables fill it
/// from the top, their header row bold and frozen; other lines go in column A. A cell starting
/// with = is a formula, and numbers, percentages, amounts of money, dates and TRUE/FALSE become
/// real values with a matching format, so they add up. Excel works the formulas out on opening.
/// </summary>
internal static partial class XlsxWriter
{
    internal static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private sealed class Sheet(string name)
    {
        public string Name = name;
        public readonly SortedDictionary<int, SortedDictionary<int, XElement>> Rows = [];
        public readonly Dictionary<int, int> Widths = [];
        /// <summary>Per column, the format of the last number in it: a total under amounts of money is money too.</summary>
        public readonly Dictionary<int, string?> Formats = [];
        public int Next = 1;
        public bool FreezeHeader;
        /// <summary>Whether a cell holds a number written without $, % or as a date (see <see cref="Write"/>).</summary>
        public bool PlainNumbers;
    }

    public static byte[] Write(IReadOnlyList<Block> blocks, List<string> notes)
    {
        var styles = new CellStyles();
        var sheets = new List<Sheet>();
        Sheet Current() => sheets.Count > 0 ? sheets[^1] : Add("Sheet1");
        Sheet Add(string name)
        {
            // A heading with nothing under it yet (a title above a sub-heading) gives way to the next.
            if (sheets.Count > 0 && sheets[^1].Rows.Count == 0)
                sheets.RemoveAt(sheets.Count - 1);
            var sheet = new Sheet(UniqueName(name, sheets));
            sheets.Add(sheet);
            return sheet;
        }

        foreach (var block in blocks)
        {
            switch (block)
            {
                case HeadingBlock h:
                    Add(MarkdownDocument.Plain(h.Text));
                    break;
                case TableBlock t when t.Columns > 0:
                    var sheet = Current();
                    if (sheet.Rows.Count > 0) sheet.Next++;
                    if (t.Header && sheet.Rows.Count == 0) sheet.FreezeHeader = true;
                    for (var r = 0; r < t.Rows.Count; r++)
                    {
                        for (var c = 0; c < t.Rows[r].Count; c++)
                            Set(sheet, sheet.Next, c + 1, t.Rows[r][c], styles, header: t.Header && r == 0);
                        sheet.Next++;
                    }
                    break;
                case ParagraphBlock or ListItemBlock or QuoteBlock or CodeBlock:
                    sheet = Current();
                    var text = block switch
                    {
                        ParagraphBlock p => p.Text,
                        ListItemBlock li => li.Text,
                        QuoteBlock q => q.Text,
                        CodeBlock c => [new Span(c.Text)],
                        _ => [],
                    };
                    foreach (var line in SplitLines(text))
                        Set(sheet, sheet.Next++, 1, line, styles, header: false);
                    break;
                case ImageBlock image:
                    notes.Add($"The picture {image.Source} was left out: a workbook made here holds text and numbers only.");
                    break;
            }
        }
        if (sheets.Count == 0)
            sheets.Add(new Sheet("Sheet1"));
        // Asked for amounts in dollars, Kilo's free Nemotron wrote plain numbers and then took the
        // workbook apart to add the format by hand, twice, though the tool said how; said here, it
        // comes when it is needed.
        if (sheets.Any(s => s.PlainNumbers))
            notes.Add("Its numbers are shown as they are; written as $1,200.00, 12% or 2026-10-16 they show in dollars, as percentages or as dates " +
                      "(edit_file sets cells that way too, such as \"B2 $1,200.00\").");

        var package = new OfficePackage();
        package.AddRelationships("", [("rId1", OfficePackage.OfficeDocument, "xl/workbook.xml", false), ("rId2", OfficePackage.CoreProperties, "docProps/core.xml", false)]);
        var rels = new List<(string, string, string, bool)>();
        var workbookSheets = new XElement(S + "sheets");
        for (var i = 0; i < sheets.Count; i++)
        {
            var id = $"rId{i + 1}";
            rels.Add((id, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet", $"worksheets/sheet{i + 1}.xml", false));
            workbookSheets.Add(new XElement(S + "sheet", new XAttribute("name", sheets[i].Name), new XAttribute("sheetId", i + 1), new XAttribute(OfficePackage.R + "id", id)));
            package.Add($"xl/worksheets/sheet{i + 1}.xml", Worksheet(sheets[i]), "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml");
        }
        rels.Add(($"rId{sheets.Count + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml", false));
        package.Add("xl/workbook.xml", new XDocument(new XElement(S + "workbook",
            new XAttribute(XNamespace.Xmlns + "r", OfficePackage.R),
            new XElement(S + "bookViews", new XElement(S + "workbookView")),
            workbookSheets,
            // Formulas have no values yet: Excel works them all out when it opens the file.
            new XElement(S + "calcPr", new XAttribute("calcId", 191029), new XAttribute("fullCalcOnLoad", 1)))),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        package.AddRelationships("xl/workbook.xml", rels);
        package.Add("xl/styles.xml", styles.Document(), "application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml");
        package.AddCoreProperties(sheets[0].Name);
        return package.Build();
    }

    /// <summary>A paragraph's lines, each with its spans.</summary>
    private static IEnumerable<IReadOnlyList<Span>> SplitLines(IReadOnlyList<Span> text)
    {
        var line = new List<Span>();
        foreach (var span in text)
        {
            var parts = span.Text.Split('\n');
            for (var i = 0; i < parts.Length; i++)
            {
                if (i > 0)
                {
                    yield return line;
                    line = [];
                }
                if (parts[i].Length > 0) line.Add(span with { Text = parts[i] });
            }
        }
        if (line.Count > 0) yield return line;
    }

    private static void Set(Sheet sheet, int row, int column, IReadOnlyList<Span> spans, CellStyles styles, bool header)
    {
        var text = MarkdownDocument.Plain(spans).Trim();
        if (text.Length == 0) return;
        var bold = header || spans.Where(s => s.Text.Trim().Length > 0).All(s => s.Bold);
        var (value, format) = Value(text);
        if (!header && !text.StartsWith('=')) sheet.Formats[column] = format;
        if (value is double && format is null) sheet.PlainNumbers = true;
        var cell = Cell(Reference(row, column), text, styles, bold, header, text.StartsWith('=') ? sheet.Formats.GetValueOrDefault(column) : null);
        if (!sheet.Rows.TryGetValue(row, out var cells))
            sheet.Rows[row] = cells = [];
        cells[column] = cell;
        var width = text.StartsWith('=') ? 12 : text.Split('\n').Max(l => l.Length);
        sheet.Widths[column] = Math.Max(sheet.Widths.GetValueOrDefault(column), width);
    }

    /// <summary>A cell holding <paramref name="text"/> as Excel would take it when typed in.</summary>
    /// <param name="formulaFormat">For a formula, the number format its result is shown in.</param>
    internal static XElement Cell(string reference, string text, CellStyles styles, bool bold, bool header = false, string? formulaFormat = null)
    {
        var cell = new XElement(S + "c", new XAttribute("r", reference));
        var (value, format) = Value(text);
        format ??= formulaFormat;
        var style = styles.Get(bold, header, format);
        if (style > 0) cell.Add(new XAttribute("s", style));
        switch (value)
        {
            case string formula when text.StartsWith('=') && text.Length > 1:
                cell.Add(new XElement(S + "f", formula));
                break;
            case double number:
                cell.Add(new XElement(S + "v", number.ToString("R", CultureInfo.InvariantCulture)));
                break;
            case bool flag:
                cell.Add(new XAttribute("t", "b"), new XElement(S + "v", flag ? 1 : 0));
                break;
            default:
                cell.Add(new XAttribute("t", "inlineStr"), new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
                break;
        }
        return cell;
    }

    /// <summary>
    /// What a cell's text is: a formula (its text after =), a number with the format it was written
    /// in (1,200.50; 12%; $1,200; 2026-10-16), TRUE or FALSE, or text. "007" and phone numbers stay
    /// text, as do numbers too long for Excel's 15 digits.
    /// </summary>
    internal static (object Value, string? Format) Value(string text)
    {
        var t = text.Trim();
        if (t.StartsWith('=') && t.Length > 1)
            return (t[1..], null);
        if (t.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || t.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return (t.Equals("TRUE", StringComparison.OrdinalIgnoreCase), null);
        if (Percent().Match(t) is { Success: true } percent)
            return (double.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture) / 100, percent.Groups[2].Success ? "0.0%" : "0%");
        if (Money().Match(t) is { Success: true } money)
        {
            var amount = double.Parse(money.Groups["n"].Value.Replace(",", ""), CultureInfo.InvariantCulture) * (money.Groups["neg"].Success ? -1 : 1);
            var symbol = money.Groups["pre"].Success ? money.Groups["pre"].Value : money.Groups["post"].Value;
            var number = money.Groups["n"].Value.Contains('.') ? "#,##0.00" : "#,##0";
            return (amount, money.Groups["pre"].Success ? $"\"{symbol}\"{number}" : $"{number} \"{symbol}\"");
        }
        if (Grouped().IsMatch(t))
            return (double.Parse(t.Replace(",", ""), CultureInfo.InvariantCulture), t.Contains('.') ? "#,##0.00" : "#,##0");
        if (Plain().IsMatch(t) && !(t.TrimStart('-').StartsWith('0') && t.TrimStart('-').Length > 1 && !t.TrimStart('-').StartsWith("0.")) && t.Count(char.IsDigit) <= 15)
            return (double.Parse(t, CultureInfo.InvariantCulture), null);
        if (IsoDate().IsMatch(t) && DateTime.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return ((date - new DateTime(1899, 12, 30)).TotalDays, "yyyy-mm-dd");
        return (text, null);
    }

    private static XDocument Worksheet(Sheet sheet)
    {
        var rows = sheet.Rows.Select(r => new XElement(S + "row", new XAttribute("r", r.Key), r.Value.Values)).ToList();
        var lastColumn = sheet.Rows.Count == 0 ? 1 : sheet.Rows.Values.Max(r => r.Keys.Max());
        var lastRow = sheet.Rows.Count == 0 ? 1 : sheet.Rows.Keys.Max();
        var view = new XElement(S + "sheetView", new XAttribute("workbookViewId", 0));
        if (sheet.FreezeHeader)
            view.Add(new XElement(S + "pane", new XAttribute("ySplit", 1), new XAttribute("topLeftCell", "A2"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")));
        return new XDocument(new XElement(S + "worksheet",
            new XAttribute(XNamespace.Xmlns + "r", OfficePackage.R),
            new XElement(S + "dimension", new XAttribute("ref", $"A1:{Reference(lastRow, lastColumn)}")),
            new XElement(S + "sheetViews", view),
            new XElement(S + "sheetFormatPr", new XAttribute("defaultRowHeight", 15)),
            sheet.Widths.Count == 0 ? null : new XElement(S + "cols", sheet.Widths.OrderBy(w => w.Key).Select(w => new XElement(S + "col",
                new XAttribute("min", w.Key), new XAttribute("max", w.Key),
                new XAttribute("width", Math.Clamp(w.Value * 1.1 + 2, 8, 60).ToString("0.##", CultureInfo.InvariantCulture)), new XAttribute("customWidth", 1)))),
            new XElement(S + "sheetData", rows)));
    }

    /// <summary>"C7" for row 7, column 3.</summary>
    internal static string Reference(int row, int column)
    {
        var letters = "";
        for (var c = column; c > 0; c = (c - 1) / 26)
            letters = (char)('A' + (c - 1) % 26) + letters;
        return letters + row;
    }

    /// <summary>A sheet name Excel accepts: at most 31 characters, none of []:*?/\, not taken yet.</summary>
    private static string UniqueName(string wanted, List<Sheet> sheets)
    {
        var name = BadInName().Replace(wanted, " ").Trim().Trim('\'');
        if (name.Length == 0) name = "Sheet";
        if (name.Length > 31) name = name[..31].Trim();
        var unique = name;
        for (var n = 2; sheets.Any(s => s.Name.Equals(unique, StringComparison.OrdinalIgnoreCase)); n++)
            unique = (name.Length > 26 ? name[..26] : name) + $" ({n})";
        return unique;
    }

    /// <summary>The cell formats a workbook uses, each a cellXfs entry.</summary>
    internal sealed class CellStyles
    {
        private readonly List<(bool Bold, bool Header, string? Format)> _used = [(false, false, null)];
        private readonly List<string> _formats = [];

        public int Get(bool bold, bool header, string? format)
        {
            var key = (bold || header, header, format);
            var i = _used.IndexOf(key);
            if (i >= 0) return i;
            _used.Add(key);
            if (format is not null && BuiltIn(format) is null && !_formats.Contains(format))
                _formats.Add(format);
            return _used.Count - 1;
        }

        private static int? BuiltIn(string format) => format switch
        {
            "0%" => 9,
            "0.00%" => 10,
            "#,##0" => 3,
            "#,##0.00" => 4,
            _ => null,
        };

        private int FormatId(string? format) => format is null ? 0 : BuiltIn(format) ?? 164 + _formats.IndexOf(format);

        public XDocument Document() => new(new XElement(S + "styleSheet",
            _formats.Count == 0 ? null : new XElement(S + "numFmts", new XAttribute("count", _formats.Count),
                _formats.Select((f, i) => new XElement(S + "numFmt", new XAttribute("numFmtId", 164 + i), new XAttribute("formatCode", f)))),
            new XElement(S + "fonts", new XAttribute("count", 2),
                new XElement(S + "font", new XElement(S + "sz", new XAttribute("val", 11)), new XElement(S + "name", new XAttribute("val", "Calibri")), new XElement(S + "family", new XAttribute("val", 2))),
                new XElement(S + "font", new XElement(S + "b"), new XElement(S + "sz", new XAttribute("val", 11)), new XElement(S + "name", new XAttribute("val", "Calibri")), new XElement(S + "family", new XAttribute("val", 2)))),
            new XElement(S + "fills", new XAttribute("count", 3),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "none"))),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "gray125"))),
                new XElement(S + "fill", new XElement(S + "patternFill", new XAttribute("patternType", "solid"),
                    new XElement(S + "fgColor", new XAttribute("rgb", "FFD9E2F3")), new XElement(S + "bgColor", new XAttribute("indexed", 64))))),
            new XElement(S + "borders", new XAttribute("count", 1),
                new XElement(S + "border", new XElement(S + "left"), new XElement(S + "right"), new XElement(S + "top"), new XElement(S + "bottom"), new XElement(S + "diagonal"))),
            new XElement(S + "cellStyleXfs", new XAttribute("count", 1),
                new XElement(S + "xf", new XAttribute("numFmtId", 0), new XAttribute("fontId", 0), new XAttribute("fillId", 0), new XAttribute("borderId", 0))),
            new XElement(S + "cellXfs", new XAttribute("count", _used.Count),
                _used.Select(u => new XElement(S + "xf",
                    new XAttribute("numFmtId", FormatId(u.Format)), new XAttribute("fontId", u.Bold ? 1 : 0), new XAttribute("fillId", u.Header ? 2 : 0),
                    new XAttribute("borderId", 0), new XAttribute("xfId", 0),
                    u.Format is null ? null : new XAttribute("applyNumberFormat", 1),
                    u.Bold ? new XAttribute("applyFont", 1) : null,
                    u.Header ? new XAttribute("applyFill", 1) : null))),
            new XElement(S + "cellStyles", new XAttribute("count", 1),
                new XElement(S + "cellStyle", new XAttribute("name", "Normal"), new XAttribute("xfId", 0), new XAttribute("builtinId", 0)))));
    }

    [GeneratedRegex(@"^(-?\d+(\.(\d+))?)\s?%$")]
    private static partial Regex Percent();

    // $1,200.50, -€12, (no parentheses), 12 €, 1.200 kr is left as text.
    [GeneratedRegex(@"^(?<neg>-)?(?:(?<pre>[$€£¥₹])\s?(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)|(?<n>\d{1,3}(?:,\d{3})+(?:\.\d+)?|\d+(?:\.\d+)?)\s?(?<post>[€£¥₹]))$")]
    private static partial Regex Money();

    [GeneratedRegex(@"^-?\d{1,3}(,\d{3})+(\.\d+)?$")]
    private static partial Regex Grouped();

    [GeneratedRegex(@"^-?\d+(\.\d+)?([eE][-+]?\d+)?$")]
    private static partial Regex Plain();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"[\[\]:*?/\\]")]
    private static partial Regex BadInName();
}
