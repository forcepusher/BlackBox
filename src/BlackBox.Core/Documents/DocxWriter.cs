using System.Globalization;
using System.Xml.Linq;

namespace BlackBox.Core.Documents;

/// <summary>
/// Markdown into a Word document: headings as Word's Heading 1-6 (so they show in the navigation
/// pane and a table of contents), real bulleted and numbered lists, tables with a header row,
/// pictures, links, code and quotes, on A4 or, in the US and its neighbours, Letter.
/// </summary>
internal sealed class DocxWriter
{
    internal static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = OfficePackage.R;
    private static readonly XNamespace WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace Pic = "http://schemas.openxmlformats.org/drawingml/2006/picture";

    private readonly OfficePackage _package = new();
    private readonly List<(string Id, string Type, string Target, bool External)> _rels = [];
    private readonly List<XElement> _numbers = [];
    private readonly Func<string, string> _resolve;
    private readonly List<string> _notes;
    private int _pictures;

    private DocxWriter(Func<string, string> resolve, List<string> notes) => (_resolve, _notes) = (resolve, notes);

    /// <param name="resolve">Turns a picture's path as written into a full one.</param>
    /// <param name="notes">Gets what couldn't be put in (a picture that isn't there).</param>
    public static byte[] Write(IReadOnlyList<Block> blocks, Func<string, string> resolve, List<string> notes) =>
        new DocxWriter(resolve, notes).Build(blocks);

    // Page: A4, or Letter where that is the paper in use; 1 inch margins.
    private static readonly bool Letter = RegionInfo.CurrentRegion.TwoLetterISORegionName is "US" or "CA" or "MX" or "PH" or "CL" or "CO" or "VE" or "PR";
    private static int PageWidth => Letter ? 12240 : 11906;
    private static int PageHeight => Letter ? 15840 : 16838;
    private const int Margin = 1440;
    private static int TextWidth => PageWidth - 2 * Margin;

    private byte[] Build(IReadOnlyList<Block> blocks)
    {
        _rels.Add(("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml", false));
        _rels.Add(("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering", "numbering.xml", false));
        _rels.Add(("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/settings", "settings.xml", false));

        var body = new XElement(W + "body");
        // The numbering of the numbered list going on, and its level: it ends with anything but
        // a list item, or with a bullet at its level or above.
        int? list = null;
        var listLevel = 0;
        foreach (var block in blocks)
        {
            if (block is not ListItemBlock || block is ListItemBlock { Ordered: false } bullet && bullet.Level <= listLevel)
                list = null;
            switch (block)
            {
                case HeadingBlock h:
                    body.Add(Paragraph(h.Text, new XElement(W + "pStyle", new XAttribute(W + "val", $"Heading{h.Level}"))));
                    break;
                case ParagraphBlock p:
                    body.Add(Paragraph(p.Text));
                    break;
                case ListItemBlock item:
                    var numId = 1;
                    if (item.Ordered)
                    {
                        if (list is null)
                        {
                            list = NewNumberedList(item.Number, Math.Min(item.Level, 8));
                            listLevel = item.Level;
                        }
                        numId = list.Value;
                    }
                    body.Add(Paragraph(item.Text,
                        new XElement(W + "pStyle", new XAttribute(W + "val", "ListParagraph")),
                        new XElement(W + "numPr",
                            new XElement(W + "ilvl", new XAttribute(W + "val", Math.Min(item.Level, 8))),
                            new XElement(W + "numId", new XAttribute(W + "val", numId)))));
                    break;
                case TableBlock t when t.Columns > 0:
                    body.Add(Table(t));
                    // Two tables in a row would merge into one; a paragraph keeps them apart.
                    body.Add(new XElement(W + "p"));
                    break;
                case CodeBlock c:
                    body.Add(Paragraph([new Span(c.Text)], new XElement(W + "pStyle", new XAttribute(W + "val", "Code"))));
                    break;
                case QuoteBlock q:
                    body.Add(Paragraph(q.Text, new XElement(W + "pStyle", new XAttribute(W + "val", "Quote"))));
                    break;
                case ImageBlock image:
                    body.Add(Picture(image));
                    break;
                case RuleBlock:
                    body.Add(new XElement(W + "p", new XElement(W + "pPr", new XElement(W + "pBdr",
                        new XElement(W + "bottom", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 6), new XAttribute(W + "space", 1), new XAttribute(W + "color", "A6A6A6"))))));
                    break;
                case PageBreakBlock:
                    body.Add(new XElement(W + "p", new XElement(W + "r", new XElement(W + "br", new XAttribute(W + "type", "page")))));
                    break;
            }
        }
        body.Add(new XElement(W + "sectPr",
            new XElement(W + "pgSz", new XAttribute(W + "w", PageWidth), new XAttribute(W + "h", PageHeight)),
            new XElement(W + "pgMar", new XAttribute(W + "top", Margin), new XAttribute(W + "right", Margin), new XAttribute(W + "bottom", Margin),
                new XAttribute(W + "left", Margin), new XAttribute(W + "header", 708), new XAttribute(W + "footer", 708), new XAttribute(W + "gutter", 0))));

        var document = new XElement(W + "document",
            new XAttribute(XNamespace.Xmlns + "w", W), new XAttribute(XNamespace.Xmlns + "r", R),
            new XAttribute(XNamespace.Xmlns + "wp", WP), new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute(XNamespace.Xmlns + "pic", Pic),
            body);

        _package.AddRelationships("", [("rId1", OfficePackage.OfficeDocument, "word/document.xml", false), ("rId2", OfficePackage.CoreProperties, "docProps/core.xml", false)]);
        _package.Add("word/document.xml", new XDocument(document), "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml");
        _package.AddRelationships("word/document.xml", _rels);
        _package.Add("word/styles.xml", Styles(), "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml");
        _package.Add("word/numbering.xml", Numbering(), "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml");
        _package.Add("word/settings.xml", new XDocument(new XElement(W + "settings", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "defaultTabStop", new XAttribute(W + "val", 720)),
            new XElement(W + "compat", new XElement(W + "compatSetting", new XAttribute(W + "name", "compatibilityMode"),
                new XAttribute(W + "uri", "http://schemas.microsoft.com/office/word"), new XAttribute(W + "val", 15))))),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.settings+xml");
        _package.AddCoreProperties(blocks.OfType<HeadingBlock>().Select(h => MarkdownDocument.Plain(h.Text)).FirstOrDefault());
        return _package.Build();
    }

    // ---- paragraphs and runs -------------------------------------------------------------------------

    private XElement Paragraph(IReadOnlyList<Span> text, params XElement[] properties)
    {
        var p = new XElement(W + "p");
        if (properties.Length > 0)
            p.Add(new XElement(W + "pPr", properties));
        AddRuns(p, text);
        return p;
    }

    /// <summary>The spans as runs; a link's spans go in one hyperlink.</summary>
    internal void AddRuns(XElement paragraph, IReadOnlyList<Span> text, XElement? baseProperties = null)
    {
        XElement? link = null;
        string? linkTarget = null;
        foreach (var span in text)
        {
            var target = paragraph;
            if (span.Link is { } url && Uri.TryCreate(url, UriKind.Absolute, out _))
            {
                if (link is null || linkTarget != url)
                {
                    var id = "rId" + (_rels.Count + 1);
                    _rels.Add((id, OfficePackage.HyperlinkRel, url, true));
                    link = new XElement(W + "hyperlink", new XAttribute(R + "id", id), new XAttribute(W + "history", 1));
                    linkTarget = url;
                    paragraph.Add(link);
                }
                target = link;
            }
            else
                link = null;
            foreach (var run in Runs(span, baseProperties))
                target.Add(run);
        }
    }

    internal static IEnumerable<XElement> Runs(Span span, XElement? baseProperties = null)
    {
        var lines = span.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var run = new XElement(W + "r");
            if (RunProperties(span, baseProperties) is { } rPr)
                run.Add(rPr);
            if (i > 0)
                run.Add(new XElement(W + "br"));
            var parts = lines[i].Split('\t');
            for (var j = 0; j < parts.Length; j++)
            {
                if (j > 0) run.Add(new XElement(W + "tab"));
                if (parts[j].Length > 0)
                    run.Add(new XElement(W + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), parts[j]));
            }
            if (run.Elements().Any(e => e.Name != W + "rPr"))
                yield return run;
        }
    }

    /// <summary>The span's style on top of <paramref name="baseProperties"/>, the elements in the order Word's schema wants.</summary>
    internal static XElement? RunProperties(Span span, XElement? baseProperties)
    {
        var rPr = baseProperties is null ? new XElement(W + "rPr") : new XElement(baseProperties);
        if (span.Link is not null) Set(rPr, "rStyle", new XElement(W + "rStyle", new XAttribute(W + "val", "Hyperlink")));
        if (span.Code)
        {
            Set(rPr, "rFonts", new XElement(W + "rFonts", new XAttribute(W + "ascii", "Consolas"), new XAttribute(W + "hAnsi", "Consolas"), new XAttribute(W + "cs", "Consolas")));
            Set(rPr, "shd", new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", "F2F2F2")));
        }
        if (span.Bold) Set(rPr, "b", new XElement(W + "b"));
        if (span.Italic) Set(rPr, "i", new XElement(W + "i"));
        if (span.Strike) Set(rPr, "strike", new XElement(W + "strike"));
        return rPr.HasElements ? rPr : null;
    }

    // Where each run property goes among the others (Word rejects them out of order).
    private static readonly string[] RunOrder =
        ["rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow", "emboss", "imprint", "noProof",
         "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern", "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd",
         "fitText", "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath"];

    private static void Set(XElement rPr, string name, XElement value)
    {
        rPr.Element(W + name)?.Remove();
        var order = Array.IndexOf(RunOrder, name);
        var after = rPr.Elements().LastOrDefault(e => Array.IndexOf(RunOrder, e.Name.LocalName) is var o && o >= 0 && o < order);
        if (after is null) rPr.AddFirst(value);
        else after.AddAfterSelf(value);
    }

    // ---- lists -----------------------------------------------------------------------------------------

    /// <summary>A numbering of its own for a numbered list, so each list starts again at its first number.</summary>
    private int NewNumberedList(int start, int level)
    {
        var id = _numbers.Count + 2;
        _numbers.Add(new XElement(W + "num", new XAttribute(W + "numId", id),
            new XElement(W + "abstractNumId", new XAttribute(W + "val", 1)),
            new XElement(W + "lvlOverride", new XAttribute(W + "ilvl", level), new XElement(W + "startOverride", new XAttribute(W + "val", Math.Max(start, 1))))));
        return id;
    }

    private XDocument Numbering()
    {
        static XElement Level(int level, string format, string text) => new(W + "lvl", new XAttribute(W + "ilvl", level),
            new XElement(W + "start", new XAttribute(W + "val", 1)),
            new XElement(W + "numFmt", new XAttribute(W + "val", format)),
            new XElement(W + "lvlText", new XAttribute(W + "val", text)),
            new XElement(W + "lvlJc", new XAttribute(W + "val", "left")),
            new XElement(W + "pPr", new XElement(W + "ind", new XAttribute(W + "left", 720 * (level + 1)), new XAttribute(W + "hanging", 360))));
        string[] bullets = ["•", "◦", "▪"];
        string[] formats = ["decimal", "lowerLetter", "lowerRoman"];
        return new XDocument(new XElement(W + "numbering", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", 0),
                new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")),
                Enumerable.Range(0, 9).Select(l => Level(l, "bullet", bullets[l % 3]))),
            new XElement(W + "abstractNum", new XAttribute(W + "abstractNumId", 1),
                new XElement(W + "multiLevelType", new XAttribute(W + "val", "hybridMultilevel")),
                Enumerable.Range(0, 9).Select(l => Level(l, formats[l % 3], $"%{l + 1}."))),
            new XElement(W + "num", new XAttribute(W + "numId", 1), new XElement(W + "abstractNumId", new XAttribute(W + "val", 0))),
            _numbers));
    }

    // ---- tables ----------------------------------------------------------------------------------------

    private XElement Table(TableBlock t)
    {
        var columns = t.Columns;
        // Columns as wide as their longest text, within reason, filling the text width.
        var lengths = Enumerable.Range(0, columns)
            .Select(c => Math.Clamp(t.Rows.Max(r => c < r.Count ? MarkdownDocument.Plain(r[c]).Split('\n').Max(l => l.Length) : 0), 4, 40))
            .ToList();
        var widths = lengths.Select(l => (int)(TextWidth * (double)l / lengths.Sum())).ToList();
        var table = new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblStyle", new XAttribute(W + "val", "TableGrid")),
                new XElement(W + "tblW", new XAttribute(W + "w", 5000), new XAttribute(W + "type", "pct")),
                new XElement(W + "tblLook", new XAttribute(W + "val", "04A0"), new XAttribute(W + "firstRow", 1), new XAttribute(W + "lastRow", 0),
                    new XAttribute(W + "firstColumn", 0), new XAttribute(W + "lastColumn", 0), new XAttribute(W + "noHBand", 0), new XAttribute(W + "noVBand", 1))),
            new XElement(W + "tblGrid", widths.Select(w => new XElement(W + "gridCol", new XAttribute(W + "w", w)))));
        for (var r = 0; r < t.Rows.Count; r++)
        {
            var header = t.Header && r == 0;
            var row = new XElement(W + "tr");
            if (header)
                row.Add(new XElement(W + "trPr", new XElement(W + "tblHeader")));
            for (var c = 0; c < columns; c++)
            {
                IReadOnlyList<Span> text = c < t.Rows[r].Count ? t.Rows[r][c] : [];
                if (header) text = text.Select(s => s with { Bold = true }).ToList();
                var align = c < t.Align.Count ? t.Align[c] : 'l';
                var cellParagraph = Paragraph(text, align == 'l' ? [new XElement(W + "spacing", new XAttribute(W + "after", 0))]
                    : [new XElement(W + "spacing", new XAttribute(W + "after", 0)), new XElement(W + "jc", new XAttribute(W + "val", align == 'c' ? "center" : "right"))]);
                row.Add(new XElement(W + "tc",
                    new XElement(W + "tcPr",
                        new XElement(W + "tcW", new XAttribute(W + "w", widths[c]), new XAttribute(W + "type", "dxa")),
                        header ? new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", "D9E2F3")) : null),
                    cellParagraph));
            }
            table.Add(row);
        }
        return table;
    }

    // ---- pictures --------------------------------------------------------------------------------------

    private XElement Picture(ImageBlock image)
    {
        DocPicture picture;
        try
        {
            picture = DocPicture.Load(_resolve(image.Source));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _notes.Add($"The picture {image.Source} was left out: {ex.Message}.");
            return Paragraph([new Span($"[{(image.Alt.Length > 0 ? image.Alt : image.Source)}]", Italic: true)]);
        }
        var n = ++_pictures;
        var name = $"media/image{n}.{picture.Extension}";
        var id = "rId" + (_rels.Count + 1);
        _rels.Add((id, OfficePackage.ImageRel, name, false));
        _package.Add("word/" + name, picture.Data);
        var (w, h) = picture.Fit(TextWidth / 20.0, (PageHeight - 2 * Margin) / 20.0 * 0.6);
        long cx = (long)(w * 12700), cy = (long)(h * 12700);
        return new XElement(W + "p",
            new XElement(W + "pPr", new XElement(W + "jc", new XAttribute(W + "val", "center"))),
            new XElement(W + "r", new XElement(W + "drawing",
                new XElement(WP + "inline", new XAttribute("distT", 0), new XAttribute("distB", 0), new XAttribute("distL", 0), new XAttribute("distR", 0),
                    new XElement(WP + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                    new XElement(WP + "docPr", new XAttribute("id", n), new XAttribute("name", $"Picture {n}"), new XAttribute("descr", image.Alt)),
                    new XElement(WP + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noChangeAspect", 1))),
                    new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/picture"),
                        new XElement(Pic + "pic",
                            new XElement(Pic + "nvPicPr",
                                new XElement(Pic + "cNvPr", new XAttribute("id", 0), new XAttribute("name", Path.GetFileName(name))),
                                new XElement(Pic + "cNvPicPr")),
                            new XElement(Pic + "blipFill",
                                new XElement(A + "blip", new XAttribute(R + "embed", id)),
                                new XElement(A + "stretch", new XElement(A + "fillRect"))),
                            new XElement(Pic + "spPr",
                                new XElement(A + "xfrm", new XElement(A + "off", new XAttribute("x", 0), new XAttribute("y", 0)),
                                    new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))))))))));
    }

    // ---- styles ----------------------------------------------------------------------------------------

    private static XDocument Styles()
    {
        static XAttribute V(object value) => new(W + "val", value);
        static XElement Style(string type, string id, string name, params object[] content) =>
            new(W + "style", new XAttribute(W + "type", type), new XAttribute(W + "styleId", id), new XElement(W + "name", V(name)), content);
        static XElement Fonts(string font) => new(W + "rFonts", new XAttribute(W + "ascii", font), new XAttribute(W + "hAnsi", font), new XAttribute(W + "cs", font));

        int[] sizes = [32, 26, 24, 22, 22, 22];
        var headings = Enumerable.Range(1, 6).Select(level => Style("paragraph", $"Heading{level}", $"heading {level}",
            new XElement(W + "basedOn", V("Normal")), new XElement(W + "next", V("Normal")), new XElement(W + "uiPriority", V(9)), new XElement(W + "qFormat"),
            new XElement(W + "pPr", new XElement(W + "keepNext"), new XElement(W + "keepLines"),
                new XElement(W + "spacing", new XAttribute(W + "before", level == 1 ? 360 : 240), new XAttribute(W + "after", level == 1 ? 120 : 80)),
                new XElement(W + "outlineLvl", V(level - 1))),
            new XElement(W + "rPr", Fonts("Calibri Light"), level >= 3 ? new XElement(W + "b") : null, level >= 4 ? new XElement(W + "i") : null,
                new XElement(W + "color", V(level <= 2 ? "2F5496" : "1F3763")), new XElement(W + "sz", V(sizes[level - 1])), new XElement(W + "szCs", V(sizes[level - 1])))));

        return new XDocument(new XElement(W + "styles", new XAttribute(XNamespace.Xmlns + "w", W),
            new XElement(W + "docDefaults",
                new XElement(W + "rPrDefault", new XElement(W + "rPr", Fonts("Calibri"), new XElement(W + "sz", V(22)), new XElement(W + "szCs", V(22)),
                    new XElement(W + "lang", V(CultureInfo.CurrentCulture.Name is { Length: > 0 } lang ? lang : "en-US")))),
                new XElement(W + "pPrDefault", new XElement(W + "pPr", new XElement(W + "spacing",
                    new XAttribute(W + "after", 160), new XAttribute(W + "line", 259), new XAttribute(W + "lineRule", "auto"))))),
            new XElement(W + "style", new XAttribute(W + "type", "paragraph"), new XAttribute(W + "default", 1), new XAttribute(W + "styleId", "Normal"),
                new XElement(W + "name", V("Normal")), new XElement(W + "qFormat")),
            new XElement(W + "style", new XAttribute(W + "type", "character"), new XAttribute(W + "default", 1), new XAttribute(W + "styleId", "DefaultParagraphFont"),
                new XElement(W + "name", V("Default Paragraph Font")), new XElement(W + "uiPriority", V(1)), new XElement(W + "semiHidden"), new XElement(W + "unhideWhenUsed")),
            new XElement(W + "style", new XAttribute(W + "type", "table"), new XAttribute(W + "default", 1), new XAttribute(W + "styleId", "TableNormal"),
                new XElement(W + "name", V("Normal Table")), new XElement(W + "semiHidden"), new XElement(W + "unhideWhenUsed"),
                new XElement(W + "tblPr", new XElement(W + "tblInd", new XAttribute(W + "w", 0), new XAttribute(W + "type", "dxa")),
                    new XElement(W + "tblCellMar",
                        new XElement(W + "top", new XAttribute(W + "w", 0), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "left", new XAttribute(W + "w", 108), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "bottom", new XAttribute(W + "w", 0), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "right", new XAttribute(W + "w", 108), new XAttribute(W + "type", "dxa"))))),
            new XElement(W + "style", new XAttribute(W + "type", "numbering"), new XAttribute(W + "default", 1), new XAttribute(W + "styleId", "NoList"),
                new XElement(W + "name", V("No List")), new XElement(W + "semiHidden"), new XElement(W + "unhideWhenUsed")),
            headings,
            Style("paragraph", "Title", "Title", new XElement(W + "basedOn", V("Normal")), new XElement(W + "next", V("Normal")), new XElement(W + "qFormat"),
                new XElement(W + "rPr", Fonts("Calibri Light"), new XElement(W + "sz", V(56)), new XElement(W + "szCs", V(56)))),
            Style("paragraph", "ListParagraph", "List Paragraph", new XElement(W + "basedOn", V("Normal")), new XElement(W + "qFormat"),
                new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "after", 60)), new XElement(W + "ind", new XAttribute(W + "left", 720)), new XElement(W + "contextualSpacing"))),
            Style("paragraph", "Quote", "Quote", new XElement(W + "basedOn", V("Normal")), new XElement(W + "next", V("Normal")), new XElement(W + "qFormat"),
                new XElement(W + "pPr",
                    new XElement(W + "pBdr", new XElement(W + "left", new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 18), new XAttribute(W + "space", 8), new XAttribute(W + "color", "BFBFBF"))),
                    new XElement(W + "ind", new XAttribute(W + "left", 576), new XAttribute(W + "right", 576))),
                new XElement(W + "rPr", new XElement(W + "i"), new XElement(W + "color", V("595959")))),
            Style("paragraph", "Code", "Code", new XElement(W + "basedOn", V("Normal")), new XElement(W + "qFormat"),
                new XElement(W + "pPr", new XElement(W + "shd", new XAttribute(W + "val", "clear"), new XAttribute(W + "color", "auto"), new XAttribute(W + "fill", "F2F2F2")),
                    new XElement(W + "spacing", new XAttribute(W + "after", 160), new XAttribute(W + "line", 240), new XAttribute(W + "lineRule", "auto"))),
                new XElement(W + "rPr", Fonts("Consolas"), new XElement(W + "sz", V(19)), new XElement(W + "szCs", V(19)))),
            Style("character", "Hyperlink", "Hyperlink", new XElement(W + "basedOn", V("DefaultParagraphFont")), new XElement(W + "uiPriority", V(99)), new XElement(W + "unhideWhenUsed"),
                new XElement(W + "rPr", new XElement(W + "color", V("0563C1")), new XElement(W + "u", V("single")))),
            Style("table", "TableGrid", "Table Grid", new XElement(W + "basedOn", V("TableNormal")), new XElement(W + "uiPriority", V(39)),
                new XElement(W + "pPr", new XElement(W + "spacing", new XAttribute(W + "after", 0), new XAttribute(W + "line", 240), new XAttribute(W + "lineRule", "auto"))),
                new XElement(W + "tblPr", new XElement(W + "tblBorders",
                    new[] { "top", "left", "bottom", "right", "insideH", "insideV" }.Select(side => new XElement(W + side,
                        new XAttribute(W + "val", "single"), new XAttribute(W + "sz", 4), new XAttribute(W + "space", 0), new XAttribute(W + "color", "BFBFBF")))),
                    new XElement(W + "tblCellMar",
                        new XElement(W + "top", new XAttribute(W + "w", 40), new XAttribute(W + "type", "dxa")),
                        new XElement(W + "bottom", new XAttribute(W + "w", 40), new XAttribute(W + "type", "dxa")))))));
    }
}
