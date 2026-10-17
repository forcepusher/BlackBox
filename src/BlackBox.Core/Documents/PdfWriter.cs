using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using AddedFont = UglyToad.PdfPig.Writer.PdfDocumentBuilder.AddedFont;

namespace BlackBox.Core.Documents;

/// <summary>
/// Markdown laid out as a PDF: wrapped paragraphs, headings, lists, tables with a header row that
/// repeats on each page, code, quotes, pictures and links, page numbers when there is more than one
/// page. Western text uses the PDF reader's own Helvetica, so a page weighs a few KB; text in other
/// scripts embeds a font from the system (Arial, Segoe UI, DejaVu Sans) that has them.
/// </summary>
internal sealed partial class PdfWriter
{
    private static readonly bool Letter = RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE" or "PR";
    private readonly double _width = Letter ? 612 : 595.28, _height = Letter ? 792 : 841.89;
    private readonly double _margin = Letter ? 72 : 56.7;
    private double TextWidth => _width - 2 * _margin;

    private readonly PdfDocumentBuilder _builder = new();
    private readonly List<PdfPageBuilder> _pages = [];
    private readonly Func<string, string> _resolve;
    private readonly List<string> _notes;
    private readonly bool _unicode;
    private readonly HashSet<char> _missing;
    private readonly Dictionary<(bool Bold, bool Italic, bool Mono), AddedFont> _fonts = [];
    private readonly Dictionary<(AddedFont, double, string), double> _measured = [];
    private PdfPageBuilder _page = null!;
    private double _y;

    private static readonly (byte, byte, byte) Black = (0, 0, 0), Gray = (89, 89, 89), Blue = (5, 99, 193), HeadingBlue = (31, 56, 100);

    private PdfWriter(Func<string, string> resolve, List<string> notes, bool unicode, HashSet<char> missing) =>
        (_resolve, _notes, _unicode, _missing) = (resolve, notes, unicode, missing);

    public static byte[] Write(IReadOnlyList<Block> blocks, Func<string, string> resolve, List<string> notes)
    {
        // The reader's own Helvetica only for plain ASCII: PdfPig writes é and è in it with codes
        // that readers show as Ø and Ł. Anything else gets a system font, only the letters used.
        var text = string.Concat(blocks.SelectMany(AllText));
        var unicode = text.Any(c => c > '~') && SystemFonts.Value is not null;
        var missing = unicode ? Missing(b => b.AddTrueTypeFont(File.ReadAllBytes(SystemFonts.Value!.Regular)), text)
            : text.Where(c => c > '~').ToHashSet();
        if (missing.Count > 0)
            notes.Add($"No font here has {string.Concat(missing.Take(10))}: they came out as ?.");
        var writer = new PdfWriter(resolve, notes, unicode, missing);
        using (writer._builder)
            return writer.Build(blocks);
    }

    private byte[] Build(IReadOnlyList<Block> blocks)
    {
        NewPage();
        var counters = new int[9];
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            if (block is not ListItemBlock)
                Array.Clear(counters);
            switch (block)
            {
                case HeadingBlock h:
                    double[] sizes = [20, 16, 13.5, 12, 11, 11];
                    var size = sizes[h.Level - 1];
                    var before = _y < _height - _margin - 1 ? (h.Level <= 2 ? 16 : 10) : 0;
                    // Not alone at the bottom of a page: room for it and a couple of lines under it.
                    Space(before);
                    if (_y - size * 1.3 - 30 < _margin) NewPage();
                    Text(h.Text, size, _margin, TextWidth, bold: true, color: HeadingBlue);
                    _y -= 4;
                    break;
                case ParagraphBlock p:
                    Text(p.Text, 11, _margin, TextWidth);
                    _y -= 6;
                    break;
                case ListItemBlock item:
                    var level = Math.Min(item.Level, 8);
                    for (var deeper = level + 1; deeper < counters.Length; deeper++) counters[deeper] = 0;
                    counters[level] = item.Ordered ? (counters[level] == 0 ? Math.Max(item.Number, 1) : counters[level] + 1) : 0;
                    var marker = item.Ordered ? counters[level] + "." : (level % 3) switch { 0 => "•", 1 => "–", _ => "·" };
                    var indent = _margin + 14 + level * 18;
                    Text(item.Text, 11, indent + 14, TextWidth - (indent + 14 - _margin), marker: (marker, indent));
                    _y -= 3;
                    break;
                case TableBlock t when t.Columns > 0:
                    _y -= 2;
                    Table(t);
                    _y -= 8;
                    break;
                case CodeBlock c:
                    Code(c.Text);
                    _y -= 6;
                    break;
                case QuoteBlock q:
                    var top = _y;
                    var startPage = _page;
                    Text(q.Text, 11, _margin + 14, TextWidth - 14, italic: true, color: Gray);
                    if (_page == startPage)
                    {
                        _page.SetStrokeColor(191, 191, 191);
                        _page.DrawLine(new PdfPoint(_margin + 3, top), new PdfPoint(_margin + 3, _y), 2);
                        _page.ResetColor();
                    }
                    _y -= 6;
                    break;
                case ImageBlock image:
                    Picture(image);
                    break;
                case RuleBlock:
                    // A line at the top of a page would separate nothing.
                    Space(14);
                    if (AtTop) break;
                    _page.SetStrokeColor(166, 166, 166);
                    _page.DrawLine(new PdfPoint(_margin, _y + 7), new PdfPoint(_margin + TextWidth, _y + 7), 0.75);
                    _page.ResetColor();
                    break;
                case PageBreakBlock:
                    if (i < blocks.Count - 1 && !AtTop) NewPage();
                    break;
            }
        }

        if (_pages.Count > 1)
            for (var i = 0; i < _pages.Count; i++)
            {
                var number = $"{i + 1} / {_pages.Count}";
                var font = Font(false, false, false);
                _pages[i].SetTextAndFillColor(128, 128, 128);
                _pages[i].AddText(number, 9, new PdfPoint((_width - Measure(number, font, 9)) / 2, _margin / 2), font);
                _pages[i].ResetColor();
            }
        if (blocks.OfType<HeadingBlock>().FirstOrDefault() is { } title)
            _builder.DocumentInformation.Title = MarkdownDocument.Plain(title.Text);
        _builder.DocumentInformation.Producer = "BlackBox";
        return _builder.Build();
    }

    private void NewPage()
    {
        _page = _builder.AddPage(_width, _height);
        _pages.Add(_page);
        _y = _height - _margin;
    }

    /// <summary>Nothing on this page yet.</summary>
    private bool AtTop => _y >= _height - _margin - 0.5;

    /// <summary>A filled rectangle without the hairline outline a fill otherwise gets.</summary>
    private void Fill(double x, double y, double width, double height, (byte R, byte G, byte B) color)
    {
        _page.SetTextAndFillColor(color.R, color.G, color.B);
        _page.SetStrokeColor(color.R, color.G, color.B);
        _page.DrawRectangle(new PdfPoint(x, y), width, height, 0.01, fill: true);
        _page.ResetColor();
    }

    /// <summary>Moves down by <paramref name="height"/>, onto a new page when this one is full.</summary>
    private void Space(double height)
    {
        if (_y - height < _margin) NewPage();
        else _y -= height;
    }

    // ---- text ------------------------------------------------------------------------------------------

    private sealed record Piece(string Text, AddedFont Font, double Size, (byte R, byte G, byte B) Color, string? Link, bool Strike, double Width, bool IsSpace);

    /// <summary>Wraps and draws spans from <paramref name="x"/> within <paramref name="width"/>, moving down a line at a time.</summary>
    /// <param name="marker">A bullet or number to draw at the left of the first line.</param>
    private void Text(IReadOnlyList<Span> spans, double size, double x, double width, bool bold = false, bool italic = false,
        (byte, byte, byte)? color = null, (string Text, double X)? marker = null)
    {
        var lines = Lines(spans, size, width, bold, italic, color ?? Black);
        for (var i = 0; i < lines.Count; i++)
        {
            var lineHeight = size * 1.32;
            if (_y - lineHeight < _margin) NewPage();
            var baseline = _y - size;
            if (i == 0 && marker is { } m)
            {
                // Bullets are drawn, not typed: the built-in font has no • to type them with.
                var middle = baseline + size * 0.32;
                switch (m.Text)
                {
                    case "•":
                        _page.DrawCircle(new PdfPoint(m.X + 2.5, middle), 4, 0.1, fill: true);
                        break;
                    case "–":
                        _page.DrawLine(new PdfPoint(m.X, middle), new PdfPoint(m.X + 6, middle), 0.9);
                        break;
                    case "·":
                        _page.DrawCircle(new PdfPoint(m.X + 2.5, middle), 2.2, 0.1, fill: true);
                        break;
                    default:
                        Draw(new Piece(m.Text, Font(bold, false, false), size, color ?? Black, null, false, 0, false), m.X, baseline);
                        break;
                }
            }
            DrawLine(lines[i], x, baseline);
            _y -= lineHeight;
        }
    }

    /// <summary>A line's words, those in the same style drawn together with the spaces between them, so the text copies out as written.</summary>
    private void DrawLine(List<Piece> line, double x, double baseline)
    {
        var end = line.Count;
        while (end > 0 && line[end - 1].IsSpace) end--;
        var at = x;
        for (var i = 0; i < end;)
        {
            var first = line[i];
            var text = new StringBuilder();
            var width = 0.0;
            var j = i;
            for (; j < end && line[j].Font == first.Font && line[j].Size == first.Size && line[j].Color == first.Color
                   && line[j].Link == first.Link && line[j].Strike == first.Strike; j++)
            {
                text.Append(line[j].Text);
                width += line[j].Width;
            }
            if (text.ToString().Trim().Length > 0)
                Draw(first with { Text = text.ToString(), Width = width }, at, baseline);
            at += width;
            i = j;
        }
    }

    private void Draw(Piece piece, double x, double baseline)
    {
        var colored = piece.Color != Black;
        if (colored) _page.SetTextAndFillColor(piece.Color.R, piece.Color.G, piece.Color.B);
        _page.AddText(piece.Text, piece.Size, new PdfPoint(x, baseline), piece.Font);
        if (colored) _page.ResetColor();
        if (piece.Link is not null || piece.Strike)
        {
            var width = piece.Width > 0 ? piece.Width : Measure(piece.Text, piece.Font, piece.Size);
            var y = piece.Strike ? baseline + piece.Size * 0.3 : baseline - piece.Size * 0.12;
            _page.SetStrokeColor(piece.Color.R, piece.Color.G, piece.Color.B);
            _page.DrawLine(new PdfPoint(x, y), new PdfPoint(x + width, y), 0.5);
            _page.ResetColor();
            if (piece.Link is { } url)
                _page.AddLink(url, new PdfRectangle(x, baseline - piece.Size * 0.25, x + width, baseline + piece.Size * 0.85));
        }
    }

    /// <summary>The spans broken into lines of words that fit <paramref name="width"/>; a line break in them always breaks.</summary>
    private List<List<Piece>> Lines(IReadOnlyList<Span> spans, double size, double width, bool bold, bool italic, (byte, byte, byte) color)
    {
        var lines = new List<List<Piece>> { new() };
        var x = 0.0;
        foreach (var span in spans)
        {
            var styled = Font(bold || span.Bold, italic || span.Italic, span.Code);
            var link = span.Link is { } url && Uri.TryCreate(url, UriKind.Absolute, out _) ? url : null;
            var spanColor = link is not null ? Blue : color;
            var parts = span.Text.Split('\n');
            for (var p = 0; p < parts.Length; p++)
            {
                if (p > 0)
                {
                    lines.Add([]);
                    x = 0;
                }
                foreach (Match token in Token().Matches(Encodable(parts[p])))
                {
                    var text = token.Value;
                    var isSpace = char.IsWhiteSpace(text[0]);
                    if (isSpace)
                    {
                        if (lines[^1].Count == 0) continue;
                        text = text.Contains('\t') ? "    " : " ";
                    }
                    var font = Covering(text, styled);
                    var w = Measure(text, font, size);
                    if (!isSpace && x + w > width && lines[^1].Any(piece => !piece.IsSpace))
                    {
                        while (lines[^1].Count > 0 && lines[^1][^1].IsSpace) lines[^1].RemoveAt(lines[^1].Count - 1);
                        lines.Add([]);
                        x = 0;
                    }
                    // A word longer than the line (a long link, a path) is broken where it has to be.
                    while (!isSpace && w > width && text.Length > 1)
                    {
                        var fits = 1;
                        while (fits < text.Length && Measure(text[..(fits + 1)], font, size) <= width) fits++;
                        lines[^1].Add(new Piece(text[..fits], font, size, spanColor, link, span.Strike, Measure(text[..fits], font, size), false));
                        lines.Add([]);
                        text = text[fits..];
                        w = Measure(text, font, size);
                    }
                    lines[^1].Add(new Piece(text, font, size, spanColor, link, span.Strike, w, isSpace));
                    x = lines[^1].Sum(piece => piece.Width);
                }
            }
        }
        return lines;
    }

    private double Measure(string text, AddedFont font, double size)
    {
        if (text.Length == 0) return 0;
        if (_measured.TryGetValue((font, size, text), out var known)) return known;
        var letters = _page.MeasureText(text, size, new PdfPoint(0, 0), font);
        var width = letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
        return _measured[(font, size, text)] = width;
    }

    private AddedFont Font(bool bold, bool italic, bool mono)
    {
        var key = (bold, italic && !mono, mono);
        if (_fonts.TryGetValue(key, out var font)) return font;
        if (_unicode && SystemFonts.Value is { } files)
        {
            var file = mono ? files.Mono ?? files.Regular : bold && italic ? files.BoldItalic ?? files.Bold : bold ? files.Bold : italic ? files.Italic ?? files.Regular : files.Regular;
            font = _builder.AddTrueTypeFont(File.ReadAllBytes(file ?? files.Regular));
        }
        else
            font = _builder.AddStandard14Font(mono ? (bold ? Standard14Font.CourierBold : Standard14Font.Courier)
                : bold && italic ? Standard14Font.HelveticaBoldOblique : bold ? Standard14Font.HelveticaBold
                : italic ? Standard14Font.HelveticaOblique : Standard14Font.Helvetica);
        return _fonts[key] = font;
    }

    /// <summary>
    /// The font, or the regular one for a word it lacks a letter of. Only the regular font is
    /// checked for the whole text, but Arial Italic and Consolas have no Arabic and Consolas no
    /// Hebrew, so a quote or code in them made the whole PDF fail ("font does not contain").
    /// </summary>
    private AddedFont Covering(string text, AddedFont font)
    {
        if (!_unicode || font == Font(false, false, false))
            return font;
        foreach (var c in text)
            if (!Has(font, c))
                return Font(false, false, false);
        return font;
    }

    private readonly Dictionary<(AddedFont, char), bool> _has = [];

    private bool Has(AddedFont font, char c)
    {
        if (char.IsWhiteSpace(c))
            return true;
        if (_has.TryGetValue((font, c), out var has))
            return has;
        try
        {
            _page.MeasureText(c.ToString(), 10, new PdfPoint(0, 0), font);
            has = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            has = false;
        }
        return _has[(font, c)] = has;
    }

    /// <summary>The text with what the built-in fonts can't show as ?, when they are the ones in use.</summary>
    private string Encodable(string text) => _missing.Count == 0 || !text.Any(_missing.Contains) ? text : new string(text.Select(c => _missing.Contains(c) ? '?' : c).ToArray());

    // ---- tables ----------------------------------------------------------------------------------------

    private void Table(TableBlock t)
    {
        const double size = 10, pad = 4;
        var columns = t.Columns;
        IReadOnlyList<Span> CellText(int r, int c) => c < t.Rows[r].Count ? (t.Header && r == 0 ? t.Rows[r][c].Select(s => s with { Bold = true }).ToList() : t.Rows[r][c]) : [];
        // Each column as wide as its widest cell on one line, at least its longest word; shrunk to the page when too wide.
        var natural = new double[columns];
        var least = new double[columns];
        for (var c = 0; c < columns; c++)
            for (var r = 0; r < t.Rows.Count; r++)
                foreach (var line in Lines(CellText(r, c), size, double.MaxValue, false, false, Black))
                {
                    natural[c] = Math.Max(natural[c], line.Sum(p => p.Width) + 2 * pad);
                    least[c] = Math.Max(least[c], line.Where(p => !p.IsSpace).Select(p => p.Width).DefaultIfEmpty(0).Max() + 2 * pad);
                }
        var widths = natural.Select(n => Math.Max(n, 24)).ToArray();
        if (widths.Sum() > TextWidth)
        {
            var floor = least.Select(l => Math.Min(Math.Max(l, 24), TextWidth / columns)).ToArray();
            var rest = TextWidth - floor.Sum();
            var extra = widths.Select((w, c) => Math.Max(0, w - floor[c])).ToArray();
            widths = floor.Select((f, c) => f + (extra.Sum() > 0 ? rest * extra[c] / extra.Sum() : 0)).ToArray();
        }

        for (var r = 0; r < t.Rows.Count; r++)
        {
            var cells = Enumerable.Range(0, columns).Select(c => Lines(CellText(r, c), size, widths[c] - 2 * pad, false, false, Black)).ToList();
            var height = cells.Max(l => l.Count) * size * 1.3 + 2 * pad;
            if (_y - height < _margin)
            {
                NewPage();
                if (t.Header && r > 0)
                    Row(0, Enumerable.Range(0, columns).Select(c => Lines(CellText(0, c), size, widths[c] - 2 * pad, false, false, Black)).ToList());
            }
            Row(r, cells);
        }

        void Row(int r, List<List<List<Piece>>> cells)
        {
            var height = cells.Max(l => l.Count) * size * 1.3 + 2 * pad;
            var x = _margin;
            for (var c = 0; c < columns; c++)
            {
                if (t.Header && r == 0)
                    Fill(x, _y - height, widths[c], height, (217, 226, 243));
                _page.SetStrokeColor(191, 191, 191);
                _page.DrawRectangle(new PdfPoint(x, _y - height), widths[c], height, 0.5);
                _page.ResetColor();
                var align = c < t.Align.Count ? t.Align[c] : 'l';
                var baseline = _y - pad - size;
                foreach (var line in cells[c])
                {
                    var used = line.Sum(p => p.Width);
                    DrawLine(line, x + pad + (align == 'r' ? widths[c] - 2 * pad - used : align == 'c' ? (widths[c] - 2 * pad - used) / 2 : 0), baseline);
                    baseline -= size * 1.3;
                }
                x += widths[c];
            }
            _y -= height;
        }
    }

    // ---- code and pictures -----------------------------------------------------------------------------

    private void Code(string code)
    {
        const double size = 9;
        var font = Font(false, false, true);
        // Every character of a monospace font is as wide as the next; spaces and indents are kept.
        var perLine = Math.Max(10, (int)((TextWidth - 12) / Math.Max(Measure("M", font, size), 1)));
        _y -= 2;
        foreach (var raw in Encodable(code.Replace("\t", "    ")).Split('\n'))
        {
            for (var start = 0; start == 0 || start < raw.Length; start += perLine)
            {
                var line = raw.Length == 0 ? "" : raw.Substring(start, Math.Min(perLine, raw.Length - start));
                var height = size * 1.4;
                if (_y - height < _margin) NewPage();
                Fill(_margin, _y - height, TextWidth, height, (242, 242, 242));
                if (line.Trim().Length > 0)
                    _page.AddText(line, size, new PdfPoint(_margin + 6, _y - size * 1.1), Covering(line, font));
                _y -= height;
                if (raw.Length == 0) break;
            }
        }
    }

    private void Picture(ImageBlock image)
    {
        DocPicture picture;
        try
        {
            picture = DocPicture.Load(_resolve(image.Source));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _notes.Add($"The picture {image.Source} was left out: {ex.Message}.");
            Text([new Span($"[{(image.Alt.Length > 0 ? image.Alt : image.Source)}]", Italic: true)], 11, _margin, TextWidth);
            return;
        }
        var (w, h) = picture.Fit(TextWidth, (_height - 2 * _margin) * 0.7);
        if (_y - h - 6 < _margin) NewPage();
        var rect = new PdfRectangle(_margin + (TextWidth - w) / 2, _y - h, _margin + (TextWidth - w) / 2 + w, _y);
        if (picture.Extension == "png") _page.AddPng(picture.Data, rect);
        else _page.AddJpeg(picture.Data, rect);
        _y -= h + 8;
    }

    // ---- fonts and characters --------------------------------------------------------------------------

    private static IEnumerable<string> AllText(Block block) => block switch
    {
        HeadingBlock h => h.Text.Select(s => s.Text),
        ParagraphBlock p => p.Text.Select(s => s.Text),
        ListItemBlock l => l.Text.Select(s => s.Text),
        QuoteBlock q => q.Text.Select(s => s.Text),
        CodeBlock c => [c.Text],
        TableBlock t => t.Rows.SelectMany(r => r).SelectMany(c => c).Select(s => s.Text),
        _ => [],
    };

    /// <summary>The characters of <paramref name="text"/> a font doesn't have: Arial has no Chinese, say.</summary>
    private static HashSet<char> Missing(Func<PdfDocumentBuilder, AddedFont> add, string text)
    {
        using var builder = new PdfDocumentBuilder();
        var font = add(builder);
        var page = builder.AddPage(100, 100);
        var missing = new HashSet<char>();
        foreach (var c in text.Where(c => c is not ('\n' or '\t' or '\r')).Distinct())
        {
            try { page.MeasureText(c.ToString(), 10, new PdfPoint(0, 0), font); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                missing.Add(c);
            }
        }
        return missing;
    }

    private sealed record FontFiles(string Regular, string? Bold, string? Italic, string? BoldItalic, string? Mono);

    /// <summary>A TrueType family on this system that covers more than Western text, or null.</summary>
    private static readonly Lazy<FontFiles?> SystemFonts = new(() =>
    {
        var windows = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        (string Dir, string Regular, string Bold, string Italic, string BoldItalic, string Mono)[] families =
        [
            (windows, "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf", "consola.ttf"),
            (windows, "segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf", "consola.ttf"),
            ("/usr/share/fonts/truetype/dejavu", "DejaVuSans.ttf", "DejaVuSans-Bold.ttf", "DejaVuSans-Oblique.ttf", "DejaVuSans-BoldOblique.ttf", "DejaVuSansMono.ttf"),
            ("/System/Library/Fonts/Supplemental", "Arial.ttf", "Arial Bold.ttf", "Arial Italic.ttf", "Arial Bold Italic.ttf", "Courier New.ttf"),
        ];
        foreach (var f in families)
        {
            string? Find(string name) => File.Exists(Path.Combine(f.Dir, name)) ? Path.Combine(f.Dir, name) : null;
            if (Find(f.Regular) is { } regular)
                return new FontFiles(regular, Find(f.Bold), Find(f.Italic), Find(f.BoldItalic), Find(f.Mono));
        }
        return null;
    });

    [GeneratedRegex(@"\s+|\S+")]
    private static partial Regex Token();
}
