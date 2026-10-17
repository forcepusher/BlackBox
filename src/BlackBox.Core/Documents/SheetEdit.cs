using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BlackBox.Core.Documents;

/// <summary>
/// edit_file on an Excel workbook. Cells are written the way read_file shows them, one per line or
/// several joined by " | ": "B7 =SUM(B2:B6)", "A8 Total", "Budget!C9 12.5", a line # Sheet "Name"
/// for the cells after it. old_text names the cells being replaced (they must still hold what it
/// says, so nothing is overwritten from a stale read) or is empty; cells in it but not in new_text
/// are emptied. Without cell addresses, old_text is text to find in the cells and replace. Values
/// are typed as in Excel: formulas, numbers, percentages, money and dates keep their meaning, and a
/// cell's own formatting stays. Lines "insert row 7" and "delete rows 3-4" in new_text change the
/// rows first, as Excel does; asked to add a budget line above its total without them, qwen3.8
/// wrote the new line over the total and the total below it, the new line in the total's bold.
/// Excel works the formulas out again when it opens the file.
/// </summary>
internal static partial class SheetEdit
{
    private static readonly XNamespace S = XlsxWriter.S;
    private static readonly XNamespace R = OfficePackage.R;
    private static readonly XNamespace Rel = OfficePackage.Rel;

    private sealed record CellLine(string? Sheet, string Reference, string? Value);

    /// <summary>Rows inserted (made like the row above them, as Excel's Insert does) or deleted, before cells are set.</summary>
    private sealed record RowChange(string? Sheet, bool Insert, int From, int Count);

    public static string Edit(PartSet parts, string oldText, string newText, bool all)
    {
        var sheets = Sheets(parts);
        var old = Lines(oldText);
        var now = Lines(newText);
        if (old?.Rows.Count > 0)
            throw new InvalidOperationException("Rows are inserted or deleted with a line in new_text (\"insert row 7\", \"delete rows 3-4\"), not in old_text.");
        var cellMode = now is not null && (oldText.Trim().Length == 0 || old is not null)
            && (now.Value.Rows.Count > 0 || now.Value.Cells.Concat(old?.Cells ?? []).Any(c => c.Value is not null));
        return cellMode ? SetCells(parts, sheets, old?.Cells ?? [], now!.Value.Cells, now.Value.Rows) : ReplaceText(parts, sheets, oldText, newText, all);
    }

    // ---- cells -----------------------------------------------------------------------------------------

    private static string SetCells(PartSet parts, List<(string Name, string Part)> sheets, List<CellLine> oldCells, List<CellLine> newCells, List<RowChange> rows)
    {
        (string Name, string Part) SheetOf(string? name) =>
            name is null ? sheets[0]
            : sheets.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { Part: not null } found ? found
            : throw new InvalidOperationException($"There is no sheet \"{name}\"; the sheets are {string.Join(", ", sheets.Select(s => $"\"{s.Name}\""))}.");

        if (rows.Count > 0 && oldCells.Count > 0)
            throw new InvalidOperationException("When inserting or deleting rows, leave old_text empty and give the cells to set, at their places after the change, in new_text.");
        var done = new List<string>();
        foreach (var change in rows)
        {
            var sheet = SheetOf(change.Sheet);
            ChangeRows(parts, sheets, sheet, change.Insert, change.From, change.Count);
            done.Add($"{(change.Insert ? "inserted" : "deleted")} {(change.Count == 1 ? $"row {change.From}" : $"rows {change.From}-{change.From + change.Count - 1}")} on sheet \"{sheet.Name}\"");
        }

        var styles = parts.Exists("xl/styles.xml") ? parts.Get("xl/styles.xml") : null;
        var shared = parts.Exists("xl/sharedStrings.xml") ? parts.Get("xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList() : [];

        // The old cells must still say what old_text says they do.
        foreach (var old in oldCells.Where(c => c.Value is not null))
        {
            var (name, part) = SheetOf(old.Sheet);
            var cell = Find(parts.Get(part), old.Reference);
            var now = cell is null ? "" : Shown(cell, shared);
            if (!SameValue(now, old.Value!))
                throw new InvalidOperationException($"{old.Reference} on sheet \"{name}\" holds {(now.Length == 0 ? "nothing" : now)}, not {old.Value}. Read the file again before changing it.");
        }

        var changes = newCells.Select(c => (Sheet: SheetOf(c.Sheet), Cell: c.Reference.Replace("$", "").ToUpperInvariant(), c.Value)).ToList();
        foreach (var old in oldCells)
        {
            var sheet = SheetOf(old.Sheet);
            var reference = old.Reference.Replace("$", "").ToUpperInvariant();
            if (!changes.Any(c => c.Sheet == sheet && c.Cell == reference))
                changes.Add((sheet, reference, null));
        }

        var formulas = false;
        var formats = new Dictionary<(int, string), int>();
        foreach (var group in changes.GroupBy(c => c.Sheet))
        {
            var xml = parts.Get(group.Key.Part);
            var data = xml.Root!.Element(S + "sheetData") ?? throw new InvalidOperationException($"Sheet \"{group.Key.Name}\" has no cells part.");
            foreach (var (_, reference, value) in group)
            {
                Unshare(data, reference);
                var cell = Find(xml, reference) ?? Create(data, reference);
                cell.Elements().Where(e => e.Name == S + "f" || e.Name == S + "v" || e.Name == S + "is").Remove();
                cell.Attribute("t")?.Remove();
                if (string.IsNullOrEmpty(value?.Trim()))
                    continue;
                var (parsed, format) = XlsxWriter.Value(value);
                switch (parsed)
                {
                    case string formula when value.TrimStart().StartsWith('=') && value.Trim().Length > 1:
                        cell.Add(new XElement(S + "f", formula));
                        formulas = true;
                        break;
                    case double number:
                        cell.Add(new XElement(S + "v", number.ToString("R", CultureInfo.InvariantCulture)));
                        break;
                    case bool flag:
                        cell.SetAttributeValue("t", "b");
                        cell.Add(new XElement(S + "v", flag ? 1 : 0));
                        break;
                    default:
                        cell.SetAttributeValue("t", "inlineStr");
                        cell.Add(new XElement(S + "is", new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), value.Trim())));
                        break;
                }
                if (format is not null && styles is not null)
                    Format(cell, format, styles, formats);
            }
            Dimension(xml);
            Tables(parts, group.Key.Part, xml, shared);
            parts.Changed(group.Key.Part);
        }
        if (formats.Count > 0)
            parts.Changed("xl/styles.xml");
        if (formulas || rows.Count > 0)
            Recalculate(parts);
        if (changes.Count > 0)
            done.Add("set " + string.Join("; ", changes.GroupBy(c => c.Sheet.Name).Select(g => $"{string.Join(", ", g.Select(c => c.Cell))} on sheet \"{g.Key}\"")));
        var said = string.Join(", then ", done);
        return char.ToUpperInvariant(said[0]) + said[1..] + (formulas || rows.Count > 0 ? ". Excel works the formulas out when it opens the file." : ".");
    }

    /// <summary>Excel's last row, and the most rows one line may insert.</summary>
    private const int MaxRow = 1_048_576, MaxInsert = 100_000;

    /// <summary>
    /// Cell lines as read_file shows them and row insertions and deletions ("insert row 7",
    /// "delete rows 3-4"), or null when the text isn't made of them. A line # Sheet "Name" sets the
    /// sheet of what follows it.
    /// </summary>
    private static (List<CellLine> Cells, List<RowChange> Rows)? Lines(string text)
    {
        var cells = new List<CellLine>();
        var rows = new List<RowChange>();
        string? sheet = null;
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (RowLine().Match(line) is { Success: true } row)
            {
                var from = int.Parse(row.Groups["from"].Value, CultureInfo.InvariantCulture);
                var to = row.Groups["to"].Success ? int.Parse(row.Groups["to"].Value, CultureInfo.InvariantCulture) : from;
                if (from < 1 || to < from)
                    return null;
                var insert = row.Groups["verb"].Value.Equals("insert", StringComparison.OrdinalIgnoreCase);
                // Inserting copies the styled cells of the row above into each new row: "insert rows
                // 2-9999999" ran for minutes and out of memory, past where Excel's sheets end anyway.
                if (to > MaxRow || insert && to - from + 1 > MaxInsert)
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"{line}: a sheet ends at row {MaxRow:N0}, and one line inserts at most {MaxInsert:N0} rows."));
                var on = row.Groups["sheet"].Success ? row.Groups["sheet"].Value.Trim().Trim('"', '\'') : sheet;
                rows.Add(new RowChange(on, insert, from, to - from + 1));
                continue;
            }
            if (SheetHeader().Match(line) is { Success: true } header)
            {
                sheet = header.Groups[1].Value.Trim();
                continue;
            }
            foreach (var part in RowSplit().Split(line))
            {
                if (CellPattern().Match(part.Trim()) is not { Success: true } m)
                    return null;
                var value = m.Groups["value"].Success ? m.Groups["value"].Value : null;
                // read_file shows a formula with its value after an arrow: "=SUM(B2:B6) → 2150", or
                // nothing after it when Excel hasn't worked it out yet.
                if (value is not null && value.StartsWith('=') && value.LastIndexOf('→') is var arrow and > 0)
                    value = value[..arrow].TrimEnd();
                var on = m.Groups["sheet"].Success ? m.Groups["sheet"].Value.Trim('\'').Replace("''", "'") : sheet;
                cells.Add(new CellLine(on, m.Groups["ref"].Value, value));
            }
        }
        return cells.Count > 0 || rows.Count > 0 ? (cells, rows) : null;
    }

    /// <summary>What read_file shows of a cell: =formula, or the value.</summary>
    private static string Shown(XElement c, List<string> shared)
    {
        if (c.Element(S + "f") is { } f && f.Value.Length > 0) return "=" + f.Value;
        var value = (string?)c.Element(S + "v");
        return (string?)c.Attribute("t") switch
        {
            "s" when int.TryParse(value, out var i) && i < shared.Count => shared[i],
            "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
            "b" => value == "1" ? "TRUE" : "FALSE",
            null or "n" when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n.ToString("G15", CultureInfo.InvariantCulture),
            _ => value ?? "",
        };
    }

    private static bool SameValue(string now, string said)
    {
        said = said.Trim();
        if (now.Trim() == said) return true;
        if (now.StartsWith('=') && said.StartsWith('='))
            return string.Equals(now.Replace(" ", ""), said.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
        return double.TryParse(now, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && XlsxWriter.Value(said).Value is double b && Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Abs(a));
    }

    private static XElement? Find(XDocument sheet, string reference)
    {
        var r = reference.Replace("$", "").ToUpperInvariant();
        return sheet.Root!.Element(S + "sheetData")?.Elements(S + "row").SelectMany(row => row.Elements(S + "c"))
            .FirstOrDefault(c => string.Equals((string?)c.Attribute("r"), r, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A new cell at its place among the others, its row made if needed.</summary>
    private static XElement Create(XElement data, string reference)
    {
        var (row, column) = Parse(reference);
        var rowElement = data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") == row);
        if (rowElement is null)
        {
            rowElement = new XElement(S + "row", new XAttribute("r", row));
            var after = data.Elements(S + "row").LastOrDefault(r => (int?)r.Attribute("r") < row);
            if (after is null) data.AddFirst(rowElement);
            else after.AddAfterSelf(rowElement);
        }
        // "spans" only says which columns the row uses; a cell outside them would contradict it.
        rowElement.Attribute("spans")?.Remove();
        var cell = new XElement(S + "c", new XAttribute("r", reference));
        var before = rowElement.Elements(S + "c").LastOrDefault(c => Parse((string)c.Attribute("r")!).Column < column);
        if (before is null) rowElement.AddFirst(cell);
        else before.AddAfterSelf(cell);
        // A new cell takes its row's formatting, as when typing into it; under a list, the
        // formatting of the cell above, as Excel extends a list's formats to a row added below it.
        if ((string?)rowElement.Attribute("s") is { } rowStyle && (string?)rowElement.Attribute("customFormat") == "1")
            cell.SetAttributeValue("s", rowStyle);
        else if (data.Elements(S + "row").FirstOrDefault(r => (int?)r.Attribute("r") == row - 1) is { } above
                 && above.Elements(S + "c").FirstOrDefault(c => (string?)c.Attribute("r") is { } r && Parse(r).Column == column) is { } upper
                 && (string?)upper.Attribute("s") is { } upperStyle)
            cell.SetAttributeValue("s", upperStyle);
        return cell;
    }

    internal static (int Row, int Column) Parse(string reference)
    {
        var m = Reference().Match(reference.Replace("$", "").ToUpperInvariant());
        var column = 0;
        foreach (var ch in m.Groups[1].Value) column = column * 26 + (ch - 'A' + 1);
        return (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), column);
    }

    /// <summary>
    /// Writes out a shared formula the cell takes part in, in each of its cells, before the cell
    /// changes: the others only point at the first cell's formula, and would lose theirs with it.
    /// </summary>
    private static void Unshare(XElement data, string reference)
    {
        var cell = data.Elements(S + "row").SelectMany(r => r.Elements(S + "c")).FirstOrDefault(c => (string?)c.Attribute("r") == reference);
        if (cell?.Element(S + "f") is not { } f || (string?)f.Attribute("t") != "shared" || (string?)f.Attribute("si") is not { } si)
            return;
        var group = data.Elements(S + "row").SelectMany(r => r.Elements(S + "c"))
            .Where(c => c.Element(S + "f") is { } g && (string?)g.Attribute("t") == "shared" && (string?)g.Attribute("si") == si).ToList();
        var master = group.FirstOrDefault(c => c.Element(S + "f")!.Value.Length > 0);
        if (master is null) return;
        var (masterRow, masterColumn) = Parse((string)master.Attribute("r")!);
        var formula = master.Element(S + "f")!.Value;
        foreach (var c in group)
        {
            var (row, column) = Parse((string)c.Attribute("r")!);
            var g = c.Element(S + "f")!;
            g.Value = Shift(formula, row - masterRow, column - masterColumn);
            g.Attributes().Where(a => a.Name.LocalName is "t" or "ref" or "si").Remove();
        }
    }

    // ---- inserting and deleting rows -------------------------------------------------------------------

    /// <summary>
    /// Inserts <paramref name="count"/> rows before row <paramref name="from"/>, or deletes that many
    /// from it, as Excel does: the rows below move, and every reference to them moves along (in
    /// formulas on any sheet, defined names, charts, tables, merged cells, conditional formats). A
    /// range that spans the place grows or shrinks; a reference to a deleted cell becomes #REF!. New
    /// rows are formatted like the row above them.
    /// </summary>
    private static void ChangeRows(PartSet parts, List<(string Name, string Part)> sheets, (string Name, string Part) target, bool insert, int from, int count)
    {
        // Shared formulas are written out first: their cells follow the first one's formula by
        // offset, which no longer holds when only some of them move.
        foreach (var (_, part) in sheets)
        {
            var sheetData = parts.Get(part).Root!.Element(S + "sheetData");
            if (sheetData is null) continue;
            var groups = sheetData.Descendants(S + "f").Where(f => (string?)f.Attribute("t") == "shared" && f.Value.Length > 0)
                .Select(f => (string?)f.Parent!.Attribute("r")).OfType<string>().ToList();
            foreach (var reference in groups)
                Unshare(sheetData, reference);
            if (groups.Count > 0) parts.Changed(part);
        }

        var xml = parts.Get(target.Part);
        var data = xml.Root!.Element(S + "sheetData") ?? throw new InvalidOperationException($"Sheet \"{target.Name}\" has no cells part.");
        var last = from + count - 1;
        if (insert)
        {
            foreach (var row in data.Elements(S + "row").Where(r => (int?)r.Attribute("r") >= from).Reverse().ToList())
                MoveRow(row, count);
            var above = data.Elements(S + "row").LastOrDefault(r => (int?)r.Attribute("r") < from);
            var place = above;
            for (var i = 0; i < count; i++)
            {
                var row = new XElement(S + "row", new XAttribute("r", from + i));
                if (above is not null)
                {
                    foreach (var name in new[] { "s", "customFormat", "ht", "customHeight" })
                        if (above.Attribute(name) is { } a) row.SetAttributeValue(name, a.Value);
                    foreach (var c in above.Elements(S + "c").Where(c => c.Attribute("s") is not null))
                        row.Add(new XElement(S + "c", new XAttribute("r", Regex.Replace((string)c.Attribute("r")!, @"\d+$", (from + i).ToString(CultureInfo.InvariantCulture))), new XAttribute("s", (string)c.Attribute("s")!)));
                }
                if (place is null) data.AddFirst(row);
                else place.AddAfterSelf(row);
                place = row;
            }
        }
        else
        {
            data.Elements(S + "row").Where(r => (int?)r.Attribute("r") is { } n && n >= from && n <= last).Remove();
            foreach (var row in data.Elements(S + "row").Where(r => (int?)r.Attribute("r") > last).ToList())
                MoveRow(row, -count);
        }

        string Move(string formula, bool sameSheet) => MoveReferences(formula, sameSheet, target.Name, insert, from, count);

        // Formulas on every sheet: those on this one without a sheet name, and any naming it.
        foreach (var (name, part) in sheets)
        {
            var sheet = parts.Get(part);
            var same = part == target.Part;
            var changed = false;
            foreach (var f in sheet.Descendants(S + "f").Where(f => f.Value.Length > 0))
            {
                var moved = Move(f.Value, same);
                if (moved != f.Value) { f.Value = moved; changed = true; }
                if (same && f.Attribute("ref") is { } fRef) fRef.Value = MoveRanges(fRef.Value, Move);
            }
            if (same)
            {
                // Merged cells, conditional formats, validations, links and the filter, all on this sheet.
                foreach (var e in sheet.Root!.Descendants().Where(e => e.Name.LocalName is "mergeCell" or "hyperlink" or "autoFilter" or "conditionalFormatting" or "dataValidation" or "selection").ToList())
                {
                    var attribute = e.Attribute("ref") ?? e.Attribute("sqref");
                    if (attribute is null) continue;
                    var moved = MoveRanges(attribute.Value, Move);
                    if (moved.Length == 0 && e.Name.LocalName is not "selection") e.Remove();
                    else attribute.Value = moved.Length == 0 ? "A1" : moved;
                }
                foreach (var list in sheet.Root.Elements().Where(e => e.Name.LocalName == "mergeCells").ToList())
                    if (!list.HasElements) list.Remove();
                    else list.SetAttributeValue("count", list.Elements().Count());
                changed = true;
                Dimension(sheet);
            }
            if (changed) parts.Changed(part);
        }

        // Defined names, charts and tables say the sheet's name in their references.
        var workbook = parts.Get("xl/workbook.xml");
        foreach (var definedName in workbook.Root!.Descendants(S + "definedName"))
            definedName.Value = Move(definedName.Value, false);
        parts.Changed("xl/workbook.xml");
        foreach (var chart in parts.Names.Where(n => n.StartsWith("xl/charts/chart", StringComparison.Ordinal) && n.EndsWith(".xml", StringComparison.Ordinal)).ToList())
        {
            var changed = false;
            foreach (var f in parts.Get(chart).Descendants().Where(e => e.Name.LocalName == "f"))
            {
                var moved = Move(f.Value, false);
                if (moved != f.Value) { f.Value = moved; changed = true; }
            }
            if (changed) parts.Changed(chart);
        }
        foreach (var table in Related(parts, target.Part, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/table").ToList())
        {
            var root = parts.Get(table).Root!;
            foreach (var e in root.DescendantsAndSelf().Where(e => e.Attribute("ref") is not null))
                e.SetAttributeValue("ref", MoveRanges((string)e.Attribute("ref")!, Move));
            parts.Changed(table);
        }
    }

    private static void MoveRow(XElement row, int by)
    {
        var number = (int)row.Attribute("r")! + by;
        row.SetAttributeValue("r", number);
        foreach (var c in row.Elements(S + "c"))
            if ((string?)c.Attribute("r") is { } reference)
                c.SetAttributeValue("r", Regex.Replace(reference, @"\d+$", number.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>A list of ranges ("A1:B2 D5") moved; those that no longer exist are left out.</summary>
    private static string MoveRanges(string ranges, Func<string, bool, string> move) =>
        string.Join(' ', ranges.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(r => move(r, true)).Where(r => !r.Contains("#REF!", StringComparison.Ordinal)));

    /// <summary>
    /// A formula with its references to <paramref name="sheet"/> moved for rows inserted at
    /// <paramref name="from"/> or deleted from it. References without a sheet name are to the
    /// formula's own sheet, which <paramref name="sameSheet"/> says it is.
    /// </summary>
    internal static string MoveReferences(string formula, bool sameSheet, string sheet, bool insert, int from, int count)
    {
        var last = from + count - 1;
        var parts = formula.Split('"');
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = RangeReference().Replace(parts[i], m =>
            {
                var named = m.Groups["sheet"].Success ? m.Groups["sheet"].Value[..^1] : null;
                var on = named is null ? null : named.StartsWith('\'') ? named[1..^1].Replace("''", "'") : named;
                if (on is null ? !sameSheet : !on.Equals(sheet, StringComparison.OrdinalIgnoreCase))
                    return m.Value;
                var r1 = int.Parse(m.Groups["r1"].Value, CultureInfo.InvariantCulture);
                int? r2 = m.Groups["r2"].Success ? int.Parse(m.Groups["r2"].Value, CultureInfo.InvariantCulture) : null;
                if (insert)
                {
                    if (r1 >= from) r1 += count;
                    if (r2 >= from) r2 += count;
                }
                else if (r2 is null)
                {
                    if (r1 > last) r1 -= count;
                    else if (r1 >= from) return (named is null ? "" : named + "!") + "#REF!";
                }
                else
                {
                    // A range loses the deleted rows; one made only of them is gone.
                    int a = r1, b = r2.Value;
                    if (a > last) a -= count; else if (a >= from) a = from;
                    if (b > last) b -= count; else if (b >= from) b = from - 1;
                    if (b < a) return (named is null ? "" : named + "!") + "#REF!";
                    (r1, r2) = (a, b);
                }
                var text = m.Groups["sheet"].Value + m.Groups["c1"].Value + m.Groups["d1"].Value + r1;
                return r2 is null ? text : text + ":" + m.Groups["c2"].Value + m.Groups["d2"].Value + r2;
            });
        return string.Join('"', parts);
    }

    /// <summary>A formula as it reads moved by rows and columns: relative references move, $ ones stay.</summary>
    internal static string Shift(string formula, int rows, int columns)
    {
        if (rows == 0 && columns == 0) return formula;
        // Text in quotes is left alone.
        var parts = formula.Split('"');
        for (var i = 0; i < parts.Length; i += 2)
            parts[i] = FormulaReference().Replace(parts[i], m =>
            {
                var column = m.Groups[2].Value;
                var row = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                if (m.Groups[1].Value.Length == 0)
                {
                    var n = 0;
                    foreach (var ch in column) n = n * 26 + (ch - 'A' + 1);
                    column = XlsxWriter.Reference(1, Math.Max(1, n + columns))[..^1];
                }
                if (m.Groups[3].Value.Length == 0)
                    row = Math.Max(1, row + rows);
                return m.Groups[1].Value + column + m.Groups[3].Value + row;
            });
        return string.Join('"', parts);
    }

    private static void Format(XElement cell, string format, XDocument styles, Dictionary<(int, string), int> made)
    {
        var root = styles.Root!;
        var xfs = root.Element(S + "cellXfs");
        if (xfs is null) return;
        var current = (int?)cell.Attribute("s") ?? 0;
        var xf = xfs.Elements(S + "xf").ElementAtOrDefault(current);
        // The cell has a number format of its own already: it stays.
        if (xf is null || ((int?)xf.Attribute("numFmtId") ?? 0) != 0) return;
        if (!made.TryGetValue((current, format), out var index))
        {
            var id = format switch { "0%" => 9, "0.00%" => 10, "#,##0" => 3, "#,##0.00" => 4, _ => -1 };
            if (id < 0)
            {
                var numFmts = root.Element(S + "numFmts");
                if (numFmts is null)
                {
                    numFmts = new XElement(S + "numFmts");
                    root.AddFirst(numFmts);
                }
                var existing = numFmts.Elements(S + "numFmt").FirstOrDefault(n => (string?)n.Attribute("formatCode") == format);
                if (existing is not null) id = (int)existing.Attribute("numFmtId")!;
                else
                {
                    id = Math.Max(163, numFmts.Elements(S + "numFmt").Select(n => (int?)n.Attribute("numFmtId") ?? 0).DefaultIfEmpty(0).Max()) + 1;
                    numFmts.Add(new XElement(S + "numFmt", new XAttribute("numFmtId", id), new XAttribute("formatCode", format)));
                }
                numFmts.SetAttributeValue("count", numFmts.Elements(S + "numFmt").Count());
            }
            var copy = new XElement(xf);
            copy.SetAttributeValue("numFmtId", id);
            copy.SetAttributeValue("applyNumberFormat", 1);
            xfs.Add(copy);
            xfs.SetAttributeValue("count", xfs.Elements(S + "xf").Count());
            made[(current, format)] = index = xfs.Elements(S + "xf").Count() - 1;
        }
        cell.SetAttributeValue("s", index);
    }

    private static void Dimension(XDocument sheet)
    {
        var refs = sheet.Root!.Element(S + "sheetData")!.Elements(S + "row").SelectMany(r => r.Elements(S + "c"))
            .Select(c => (string?)c.Attribute("r")).Where(r => r is not null).Select(r => Parse(r!)).ToList();
        if (refs.Count == 0) return;
        var range = $"{XlsxWriter.Reference(refs.Min(r => r.Row), refs.Min(r => r.Column))}:{XlsxWriter.Reference(refs.Max(r => r.Row), refs.Max(r => r.Column))}";
        var dimension = sheet.Root.Element(S + "dimension");
        if (dimension is not null) dimension.SetAttributeValue("ref", range);
    }

    /// <summary>A table's column names must be its header cells' text, or Excel calls the file damaged.</summary>
    private static void Tables(PartSet parts, string sheetPart, XDocument sheet, List<string> shared)
    {
        foreach (var table in Related(parts, sheetPart, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/table"))
        {
            var xml = parts.Get(table);
            var root = xml.Root!;
            if ((string?)root.Attribute("headerRowCount") == "0" || (string?)root.Attribute("ref") is not { } range || !range.Contains(':'))
                continue;
            var (top, leftColumn) = Parse(range.Split(':')[0]);
            var columns = root.Element(S + "tableColumns")?.Elements(S + "tableColumn").ToList() ?? [];
            var changed = false;
            for (var i = 0; i < columns.Count; i++)
            {
                var cell = Find(sheet, XlsxWriter.Reference(top, leftColumn + i));
                var text = cell is null ? "" : Shown(cell, shared);
                if (text.Length > 0 && !text.StartsWith('=') && (string?)columns[i].Attribute("name") != text)
                {
                    columns[i].SetAttributeValue("name", text);
                    changed = true;
                }
            }
            if (changed) parts.Changed(table);
        }
    }

    /// <summary>Formulas without values: Excel works them all out on opening; its record of what to recalculate goes.</summary>
    private static void Recalculate(PartSet parts)
    {
        var workbook = parts.Get("xl/workbook.xml");
        var calc = workbook.Root!.Element(S + "calcPr");
        if (calc is null)
        {
            calc = new XElement(S + "calcPr");
            // calcPr comes after sheets, definedNames and the like, before oleSize and later parts.
            var after = workbook.Root.Elements().LastOrDefault(e => e.Name.LocalName is "sheets" or "functionGroups" or "externalReferences" or "definedNames");
            if (after is null) workbook.Root.Add(calc);
            else after.AddAfterSelf(calc);
        }
        calc.SetAttributeValue("fullCalcOnLoad", 1);
        parts.Changed("xl/workbook.xml");
        if (parts.Exists("xl/calcChain.xml"))
        {
            parts.Delete("xl/calcChain.xml");
            var rels = parts.Get("xl/_rels/workbook.xml.rels");
            rels.Root!.Elements(Rel + "Relationship").Where(r => ((string?)r.Attribute("Target") ?? "").EndsWith("calcChain.xml", StringComparison.Ordinal)).Remove();
            parts.Changed("xl/_rels/workbook.xml.rels");
            var types = parts.Get("[Content_Types].xml");
            types.Root!.Elements().Where(e => ((string?)e.Attribute("PartName") ?? "").EndsWith("calcChain.xml", StringComparison.Ordinal)).Remove();
            parts.Changed("[Content_Types].xml");
        }
    }

    // ---- text in cells ---------------------------------------------------------------------------------

    private static string ReplaceText(PartSet parts, List<(string Name, string Part)> sheets, string oldText, string newText, bool all)
    {
        if (oldText.Length == 0)
            throw new InvalidOperationException("Give old_text: cells as read_file shows them (\"B7 =SUM(B2:B6)\") or text to find in the cells; or leave it empty and give new_text as cells to set.");
        var sst = parts.Exists("xl/sharedStrings.xml") ? parts.Get("xl/sharedStrings.xml") : null;
        var items = sst?.Root!.Elements(S + "si").ToList() ?? [];
        static string TextOf(XElement e) => string.Concat(e.Descendants(S + "t").Select(t => t.Value));
        int Count(string text) => text.Length == 0 ? 0 : (text.Length - text.Replace(oldText, "").Length) / oldText.Length;

        // How many cells show each shared string, and the cells with their own text.
        var uses = new int[items.Count];
        var inline = new List<(string Part, XElement Cell)>();
        foreach (var (_, part) in sheets)
            foreach (var c in parts.Get(part).Descendants(S + "c"))
            {
                var t = (string?)c.Attribute("t");
                if (t == "s" && int.TryParse((string?)c.Element(S + "v"), out var i) && i < uses.Length) uses[i]++;
                else if (t == "inlineStr") inline.Add((part, c));
            }
        var found = items.Select((si, i) => Count(TextOf(si)) * uses[i]).Sum() + inline.Sum(x => Count(TextOf(x.Cell)));
        if (found == 0)
            throw new InvalidOperationException("old_text was not found in the cells' text (formulas and numbers aren't searched). To change cells, give them as read_file shows them: \"B7 =SUM(B2:B6)\".");
        if (found > 1 && !all)
            throw new InvalidOperationException($"old_text occurs {found} times in the cells. Include more of the text, give the cell (\"A3 Old text\"), or set replace_all.");

        for (var i = 0; i < items.Count; i++)
            if (uses[i] > 0 && Count(TextOf(items[i])) > 0)
            {
                var text = TextOf(items[i]).Replace(oldText, newText, StringComparison.Ordinal);
                items[i].RemoveNodes();
                items[i].Add(new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
                parts.Changed("xl/sharedStrings.xml");
            }
        foreach (var (part, cell) in inline.Where(x => Count(TextOf(x.Cell)) > 0))
        {
            var text = TextOf(cell).Replace(oldText, newText, StringComparison.Ordinal);
            cell.Element(S + "is")!.ReplaceNodes(new XElement(S + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text));
            parts.Changed(part);
        }
        foreach (var (_, part) in sheets)
            Tables(parts, part, parts.Get(part), items.Select(TextOf).ToList());
        return found > 1 ? $"Replaced {found} occurrences in the cells." : "Replaced the text in its cell.";
    }

    // ---- the workbook ----------------------------------------------------------------------------------

    /// <summary>The sheets in order, with their parts.</summary>
    private static List<(string Name, string Part)> Sheets(PartSet parts)
    {
        var targets = Targets(parts, "xl/workbook.xml");
        return parts.Get("xl/workbook.xml").Root!.Element(S + "sheets")?.Elements(S + "sheet")
            .Select(s => ((string?)s.Attribute("name") ?? "", (string?)s.Attribute(R + "id") is { } id && targets.TryGetValue(id, out var t) ? t : null))
            .Where(s => s.Item2 is not null && parts.Exists(s.Item2))
            .Select(s => (s.Item1, s.Item2!)).ToList()
            ?? throw new InvalidOperationException("The workbook lists no sheets.");
    }

    private static Dictionary<string, string> Targets(PartSet parts, string part)
    {
        var folder = Path.GetDirectoryName(part)!.Replace('\\', '/');
        var rels = $"{folder}/_rels/{Path.GetFileName(part)}.rels";
        if (!parts.Exists(rels)) return [];
        return parts.Get(rels).Root!.Elements(Rel + "Relationship")
            .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null && (string?)r.Attribute("TargetMode") != "External")
            .ToDictionary(r => (string)r.Attribute("Id")!, r => PartSet.Resolve(folder, (string)r.Attribute("Target")!));
    }

    private static IEnumerable<string> Related(PartSet parts, string part, string type)
    {
        var folder = Path.GetDirectoryName(part)!.Replace('\\', '/');
        var rels = $"{folder}/_rels/{Path.GetFileName(part)}.rels";
        if (!parts.Exists(rels)) yield break;
        foreach (var r in parts.Get(rels).Root!.Elements(Rel + "Relationship").Where(r => (string?)r.Attribute("Type") == type))
            if (PartSet.Resolve(folder, (string)r.Attribute("Target")!) is var target && parts.Exists(target))
                yield return target;
    }

    [GeneratedRegex(@"^#+\s*(?:Sheet\s+)?""?([^""(]+?)""?\s*(?:\([A-Z]+[0-9]+(?::[A-Z]+[0-9]+)?\))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SheetHeader();

    [GeneratedRegex(@"^(?:(?<sheet>'(?:[^']|'')+'|[^\s!'|""]+)!)?(?<ref>\$?[A-Za-z]{1,3}\$?[1-9][0-9]{0,6})(?:\s+(?<value>.*?))?\s*$")]
    private static partial Regex CellPattern();

    [GeneratedRegex(@"\s+\|\s+(?=(?:(?:'[^']+'|[^\s!'|]+)!)?\$?[A-Za-z]{1,3}\$?[0-9]+(?:\s|$))")]
    private static partial Regex RowSplit();

    [GeneratedRegex(@"^([A-Z]{1,3})([0-9]+)$")]
    private static partial Regex Reference();

    [GeneratedRegex(@"(?<![A-Za-z0-9_.])(\$?)([A-Z]{1,3})(\$?)([0-9]{1,7})(?![A-Za-z0-9_(])")]
    private static partial Regex FormulaReference();

    // A cell or range, with the sheet it is on if named: B7, $B$2:$B$6, Budget!A1, 'My sheet'!C2:C9.
    // Not the 10 of LOG10( nor a name such as Total2026.
    [GeneratedRegex(@"(?<![A-Za-z0-9_.$'])(?<sheet>(?:'(?:[^']|'')+'|[A-Za-z_\p{L}][\w.\p{L}]*)!)?(?<c1>\$?[A-Z]{1,3})(?<d1>\$?)(?<r1>[0-9]{1,7})(?::(?<c2>\$?[A-Z]{1,3})(?<d2>\$?)(?<r2>[0-9]{1,7}))?(?![A-Za-z0-9_(!])")]
    private static partial Regex RangeReference();

    // "insert row 7", "Insert rows 3-4", "delete row 5 on Budget", "delete rows 2 to 3".
    [GeneratedRegex(@"^(?<verb>insert|delete)\s+rows?\s+(?<from>[0-9]{1,7})(?:\s*(?:-|–|to|through|:)\s*(?<to>[0-9]{1,7}))?(?:\s+(?:on|in)\s+(?:sheet\s+)?(?<sheet>.+))?\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex RowLine();
}
