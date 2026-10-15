using System.Globalization;
using System.Xml.Linq;

namespace BlackBox.Core.Documents;

/// <summary>
/// Markdown into a PowerPoint deck (16:9): slides are separated by lines of ---, or, without any,
/// start at each # or ## heading. A slide's first heading is its title; lists become bullets
/// (numbered ones numbered), and tables and pictures are laid out beside or below the text. A first
/// slide with just a title and a line or two under it becomes a title slide.
/// </summary>
internal sealed class PptxWriter
{
    private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private static readonly XNamespace R = OfficePackage.R;

    private const long SlideWidth = 12192000, SlideHeight = 6858000;
    // The content area of a Title and Content slide.
    private const long Left = 838200, Top = 1825625, Width = 10515600, Height = 4351338;

    private readonly OfficePackage _package = new();
    private readonly Func<string, string> _resolve;
    private readonly List<string> _notes;
    private int _media;

    private PptxWriter(Func<string, string> resolve, List<string> notes) => (_resolve, _notes) = (resolve, notes);

    public static byte[] Write(IReadOnlyList<Block> blocks, Func<string, string> resolve, List<string> notes) =>
        new PptxWriter(resolve, notes).Build(blocks);

    /// <summary>The blocks of each slide.</summary>
    internal static List<List<Block>> Slides(IReadOnlyList<Block> blocks)
    {
        var slides = new List<List<Block>> { new() };
        var separated = blocks.Any(b => b is RuleBlock or PageBreakBlock);
        foreach (var block in blocks)
        {
            if (block is RuleBlock or PageBreakBlock)
                slides.Add([]);
            else
            {
                if (!separated && block is HeadingBlock { Level: <= 2 } && slides[^1].Count > 0)
                    slides.Add([]);
                slides[^1].Add(block);
            }
        }
        slides.RemoveAll(s => s.Count == 0);
        if (slides.Count == 0)
            slides.Add([]);
        return slides;
    }

    private byte[] Build(IReadOnlyList<Block> blocks)
    {
        var slides = Slides(blocks);
        var presentationRels = new List<(string, string, string, bool)>
        {
            ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster", "slideMasters/slideMaster1.xml", false),
        };
        var slideIds = new XElement(P + "sldIdLst");
        for (var i = 0; i < slides.Count; i++)
        {
            var id = $"rId{i + 2}";
            presentationRels.Add((id, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide", $"slides/slide{i + 1}.xml", false));
            slideIds.Add(new XElement(P + "sldId", new XAttribute("id", 256 + i), new XAttribute(R + "id", id)));
            Slide(i + 1, slides[i], titleSlide: i == 0 && IsTitleSlide(slides[i]));
        }
        var n = slides.Count + 2;
        presentationRels.Add(($"rId{n}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/presProps", "presProps.xml", false));
        presentationRels.Add(($"rId{n + 1}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/viewProps", "viewProps.xml", false));
        presentationRels.Add(($"rId{n + 2}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme", "theme/theme1.xml", false));
        presentationRels.Add(($"rId{n + 3}", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/tableStyles", "tableStyles.xml", false));

        _package.AddRelationships("", [("rId1", OfficePackage.OfficeDocument, "ppt/presentation.xml", false), ("rId2", OfficePackage.CoreProperties, "docProps/core.xml", false)]);
        _package.Add("ppt/presentation.xml", new XDocument(new XElement(P + "presentation", Namespaces(), new XAttribute("saveSubsetFonts", 1),
            new XElement(P + "sldMasterIdLst", new XElement(P + "sldMasterId", new XAttribute("id", 2147483648), new XAttribute(R + "id", "rId1"))),
            slideIds,
            new XElement(P + "sldSz", new XAttribute("cx", SlideWidth), new XAttribute("cy", SlideHeight)),
            new XElement(P + "notesSz", new XAttribute("cx", 6858000), new XAttribute("cy", 9144000)))),
            "application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml");
        _package.AddRelationships("ppt/presentation.xml", presentationRels);
        _package.Add("ppt/presProps.xml", new XDocument(new XElement(P + "presentationPr", Namespaces())), "application/vnd.openxmlformats-officedocument.presentationml.presProps+xml");
        _package.Add("ppt/viewProps.xml", new XDocument(new XElement(P + "viewPr", Namespaces(),
            new XElement(P + "normalViewPr", new XElement(P + "restoredLeft", new XAttribute("sz", 15620)), new XElement(P + "restoredTop", new XAttribute("sz", 94660))),
            new XElement(P + "gridSpacing", new XAttribute("cx", 76200), new XAttribute("cy", 76200)))),
            "application/vnd.openxmlformats-officedocument.presentationml.viewProps+xml");
        _package.Add("ppt/tableStyles.xml", new XDocument(new XElement(A + "tblStyleLst", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute("def", TableStyle))),
            "application/vnd.openxmlformats-officedocument.presentationml.tableStyles+xml");
        _package.Add("ppt/theme/theme1.xml", Theme(), "application/vnd.openxmlformats-officedocument.theme+xml");
        _package.Add("ppt/slideMasters/slideMaster1.xml", Master(), "application/vnd.openxmlformats-officedocument.presentationml.slideMaster+xml");
        _package.AddRelationships("ppt/slideMasters/slideMaster1.xml",
        [
            ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout", "../slideLayouts/slideLayout1.xml", false),
            ("rId2", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout", "../slideLayouts/slideLayout2.xml", false),
            ("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/theme", "../theme/theme1.xml", false),
        ]);
        for (var i = 1; i <= 2; i++)
        {
            _package.Add($"ppt/slideLayouts/slideLayout{i}.xml", Layout(title: i == 1), "application/vnd.openxmlformats-officedocument.presentationml.slideLayout+xml");
            _package.AddRelationships($"ppt/slideLayouts/slideLayout{i}.xml",
                [("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster", "../slideMasters/slideMaster1.xml", false)]);
        }
        _package.AddCoreProperties(slides[0].OfType<HeadingBlock>().Select(h => MarkdownDocument.Plain(h.Text)).FirstOrDefault());
        return _package.Build();
    }

    private static IEnumerable<XAttribute> Namespaces() =>
        [new(XNamespace.Xmlns + "a", A), new(XNamespace.Xmlns + "r", R), new(XNamespace.Xmlns + "p", P)];

    /// <summary>A title and at most a few lines of plain text or a subheading under it ("# Northwind" then "## Q3 summary").</summary>
    private static bool IsTitleSlide(List<Block> slide) =>
        slide.FirstOrDefault() is HeadingBlock && slide.Skip(1).All(b => b is ParagraphBlock or HeadingBlock) && slide.Count <= 3;

    // ---- a slide ---------------------------------------------------------------------------------------

    private void Slide(int number, List<Block> blocks, bool titleSlide)
    {
        var rels = new List<(string Id, string Type, string Target, bool External)>
        {
            ("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout", $"../slideLayouts/slideLayout{(titleSlide ? 1 : 2)}.xml", false),
        };
        var tree = new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr", Transform(0, 0, 0, 0, group: true)));
        var shapeId = 2;
        var title = blocks.FirstOrDefault() as HeadingBlock;
        var content = title is null ? blocks : blocks.Skip(1).ToList();
        if (title is not null)
            tree.Add(Placeholder(shapeId++, "Title", new XElement(P + "ph", new XAttribute("type", titleSlide ? "ctrTitle" : "title")), null,
                [Paragraph(title.Text, 0, rels, bullet: null, size: null)]));

        var texts = content.Where(b => b is ParagraphBlock or ListItemBlock or QuoteBlock or CodeBlock or HeadingBlock).ToList();
        var tables = content.OfType<TableBlock>().Where(t => t.Columns > 0).ToList();
        var pictures = content.OfType<ImageBlock>().Select(LoadPicture).OfType<(DocPicture Picture, string Alt)>().ToList();

        if (titleSlide)
        {
            if (texts.Count > 0)
                tree.Add(Placeholder(shapeId++, "Subtitle", new XElement(P + "ph", new XAttribute("type", "subTitle"), new XAttribute("idx", 1)), null,
                    texts.Select(t => Paragraph(t is HeadingBlock h ? h.Text : ((ParagraphBlock)t).Text, 0, rels, bullet: null, size: null))));
        }
        else
        {
            // Text on the left when there is a picture or a table too; the table under it when there is room.
            var (textArea, sideArea) = texts.Count == 0 ? ((0L, 0L, 0L, 0L), (Left, Top, Width, Height))
                : pictures.Count > 0 ? ((Left, Top, Width * 55 / 100, Height), (Left + Width * 58 / 100, Top, Width * 42 / 100, Height))
                : tables.Count > 0 ? ((Left, Top, Width, Height * 35 / 100), (Left, Top + Height * 38 / 100, Width, Height * 62 / 100))
                : ((Left, Top, Width, Height), (0L, 0L, 0L, 0L));
            if (texts.Count > 0)
            {
                var size = FontSize(texts, textArea.Item3 < Width || textArea.Item4 < Height);
                var paragraphs = new List<XElement>();
                foreach (var block in texts)
                    paragraphs.AddRange(TextParagraphs(block, rels, size));
                var place = textArea == (Left, Top, Width, Height) ? null : Transform(textArea.Item1, textArea.Item2, textArea.Item3, textArea.Item4);
                tree.Add(Placeholder(shapeId++, "Content", new XElement(P + "ph", new XAttribute("idx", 1)), place, paragraphs));
            }
            var area = sideArea;
            var items = tables.Count + pictures.Count;
            if (items > 0)
            {
                // Side by side when they are pictures beside text, else one under the other.
                var across = texts.Count == 0 && tables.Count == 0;
                var gap = 152400L;
                var each = across ? (area.Item3 - gap * (items - 1)) / items : (area.Item4 - gap * (items - 1)) / items;
                var k = 0;
                foreach (var table in tables)
                {
                    var (x, y, w, h) = across ? (area.Item1 + k * (each + gap), area.Item2, each, area.Item4) : (area.Item1, area.Item2 + k * (each + gap), area.Item3, each);
                    tree.Add(Table(shapeId++, table, x, y, w, h, rels));
                    k++;
                }
                foreach (var picture in pictures)
                {
                    var (x, y, w, h) = across ? (area.Item1 + k * (each + gap), area.Item2, each, area.Item4) : (area.Item1, area.Item2 + k * (each + gap), area.Item3, each);
                    var id = $"rId{rels.Count + 1}";
                    var media = $"../media/image{++_media}.{picture.Picture.Extension}";
                    rels.Add((id, OfficePackage.ImageRel, media, false));
                    _package.Add($"ppt/media/image{_media}.{picture.Picture.Extension}", picture.Picture.Data);
                    tree.Add(Picture(shapeId++, id, picture, x, y, w, h));
                    k++;
                }
            }
        }

        _package.Add($"ppt/slides/slide{number}.xml", new XDocument(new XElement(P + "sld", Namespaces(),
            new XElement(P + "cSld", tree),
            new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping")))),
            "application/vnd.openxmlformats-officedocument.presentationml.slide+xml");
        _package.AddRelationships($"ppt/slides/slide{number}.xml", rels);
    }

    /// <summary>Smaller type the more there is to say, so a full slide still fits.</summary>
    private static int FontSize(List<Block> texts, bool narrow)
    {
        var lines = texts.Sum(b => 1 + MarkdownDocument.Plain(b switch
        {
            ParagraphBlock p => p.Text,
            ListItemBlock l => l.Text,
            QuoteBlock q => q.Text,
            HeadingBlock h => h.Text,
            CodeBlock c => [new Span(c.Text)],
            _ => [],
        }).Length / (narrow ? 45 : 80));
        return lines <= 5 ? 2400 : lines <= 8 ? 2000 : lines <= 12 ? 1600 : 1400;
    }

    private IEnumerable<XElement> TextParagraphs(Block block, List<(string, string, string, bool)> rels, int size)
    {
        switch (block)
        {
            case ListItemBlock item:
                yield return Paragraph(item.Text, Math.Min(item.Level, 4), rels, bullet: item.Ordered ? "number" : "bullet", size: size - item.Level * 200);
                break;
            case ParagraphBlock p:
                yield return Paragraph(p.Text, 0, rels, bullet: "none", size: size);
                break;
            case HeadingBlock h:
                yield return Paragraph(h.Text.Select(s => s with { Bold = true }).ToList(), 0, rels, bullet: "none", size: size);
                break;
            case QuoteBlock q:
                yield return Paragraph(q.Text.Select(s => s with { Italic = true }).ToList(), 0, rels, bullet: "none", size: size);
                break;
            case CodeBlock c:
                foreach (var line in c.Text.Split('\n'))
                    yield return Paragraph([new Span(line, Code: true)], 0, rels, bullet: "none", size: Math.Min(size, 1600));
                break;
        }
    }

    /// <param name="bullet">"bullet", "number", "none", or null to keep the placeholder's own.</param>
    private XElement Paragraph(IReadOnlyList<Span> text, int level, List<(string Id, string Type, string Target, bool External)> rels, string? bullet, int? size)
    {
        var p = new XElement(A + "p");
        var pPr = new XElement(A + "pPr");
        if (level > 0) pPr.Add(new XAttribute("lvl", level));
        switch (bullet)
        {
            case "none":
                pPr.Add(new XAttribute("marL", 0), new XAttribute("indent", 0), new XElement(A + "buNone"));
                break;
            case "number":
                pPr.Add(new XAttribute("marL", 457200 * (level + 1)), new XAttribute("indent", -342900), new XElement(A + "buFont", new XAttribute("typeface", "+mj-lt")),
                    new XElement(A + "buAutoNum", new XAttribute("type", "arabicPeriod")));
                break;
        }
        if (pPr.HasAttributes || pPr.HasElements) p.Add(pPr);
        foreach (var span in text)
        {
            var lines = span.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                    p.Add(new XElement(A + "br", RunProperties(span with { Link = null }, size, rels)));
                if (lines[i].Length > 0)
                    p.Add(new XElement(A + "r", RunProperties(span, size, rels), new XElement(A + "t", lines[i])));
            }
        }
        p.Add(new XElement(A + "endParaRPr", new XAttribute("lang", Lang), size is null ? null : new XAttribute("sz", size), new XAttribute("dirty", 0)));
        return p;
    }

    private static readonly string Lang = CultureInfo.CurrentCulture.Name is { Length: > 0 } name ? name : "en-US";

    private static XElement RunProperties(Span span, int? size, List<(string Id, string Type, string Target, bool External)> rels)
    {
        var rPr = new XElement(A + "rPr", new XAttribute("lang", Lang));
        if (size is not null) rPr.Add(new XAttribute("sz", size));
        if (span.Bold) rPr.Add(new XAttribute("b", 1));
        if (span.Italic) rPr.Add(new XAttribute("i", 1));
        if (span.Strike) rPr.Add(new XAttribute("strike", "sngStrike"));
        rPr.Add(new XAttribute("dirty", 0));
        if (span.Code) rPr.Add(new XElement(A + "latin", new XAttribute("typeface", "Consolas")), new XElement(A + "cs", new XAttribute("typeface", "Consolas")));
        if (span.Link is { } url && Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            var id = $"rId{rels.Count + 1}";
            rels.Add((id, OfficePackage.HyperlinkRel, url, true));
            rPr.Add(new XElement(A + "hlinkClick", new XAttribute(R + "id", id)));
        }
        return rPr;
    }

    private static XElement Placeholder(int id, string name, XElement ph, XElement? transform, IEnumerable<XElement> paragraphs) =>
        new(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"{name} {id - 1}")),
                new XElement(P + "cNvSpPr", new XElement(A + "spLocks", new XAttribute("noGrp", 1))),
                new XElement(P + "nvPr", ph)),
            new XElement(P + "spPr", transform),
            new XElement(P + "txBody", new XElement(A + "bodyPr", new XElement(A + "normAutofit")), new XElement(A + "lstStyle"), paragraphs));

    private static XElement Transform(long x, long y, long cx, long cy, bool group = false) =>
        new(A + "xfrm",
            new XElement(A + "off", new XAttribute("x", x), new XAttribute("y", y)),
            new XElement(A + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy)),
            group ? new XElement(A + "chOff", new XAttribute("x", 0), new XAttribute("y", 0)) : null,
            group ? new XElement(A + "chExt", new XAttribute("cx", 0), new XAttribute("cy", 0)) : null);

    // ---- tables and pictures ---------------------------------------------------------------------------

    private const string TableStyle = "{5C22544A-7EE6-4342-B048-85BDC9FD1C3A}"; // Medium Style 2 - Accent 1, built into PowerPoint

    private XElement Table(int id, TableBlock t, long x, long y, long width, long height, List<(string Id, string Type, string Target, bool External)> rels)
    {
        var columns = t.Columns;
        var lengths = Enumerable.Range(0, columns)
            .Select(c => Math.Clamp(t.Rows.Max(r => c < r.Count ? MarkdownDocument.Plain(r[c]).Length : 0), 3, 40)).ToList();
        var widths = lengths.Select(l => width * l / lengths.Sum()).ToList();
        var size = t.Rows.Count <= 6 ? 1600 : t.Rows.Count <= 10 ? 1400 : 1100;
        var rowHeight = Math.Min(height / Math.Max(t.Rows.Count, 1), 370840);
        var table = new XElement(A + "tbl",
            new XElement(A + "tblPr", new XAttribute("firstRow", t.Header ? 1 : 0), new XAttribute("bandRow", 1), new XElement(A + "tableStyleId", TableStyle)),
            new XElement(A + "tblGrid", widths.Select(w => new XElement(A + "gridCol", new XAttribute("w", w)))));
        foreach (var row in t.Rows)
        {
            var tr = new XElement(A + "tr", new XAttribute("h", rowHeight));
            for (var c = 0; c < columns; c++)
            {
                var paragraph = Paragraph(c < row.Count ? row[c] : [], 0, rels, bullet: null, size: size);
                var align = c < t.Align.Count ? t.Align[c] : 'l';
                if (align != 'l')
                    paragraph.AddFirst(new XElement(A + "pPr", new XAttribute("algn", align == 'c' ? "ctr" : "r")));
                tr.Add(new XElement(A + "tc", new XElement(A + "txBody", new XElement(A + "bodyPr"), new XElement(A + "lstStyle"), paragraph), new XElement(A + "tcPr")));
            }
            table.Add(tr);
        }
        return new XElement(P + "graphicFrame",
            new XElement(P + "nvGraphicFramePr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Table {id - 1}")),
                new XElement(P + "cNvGraphicFramePr", new XElement(A + "graphicFrameLocks", new XAttribute("noGrp", 1))),
                new XElement(P + "nvPr")),
            new XElement(P + "xfrm", new XElement(A + "off", new XAttribute("x", x), new XAttribute("y", y)), new XElement(A + "ext", new XAttribute("cx", width), new XAttribute("cy", rowHeight * t.Rows.Count))),
            new XElement(A + "graphic", new XElement(A + "graphicData", new XAttribute("uri", "http://schemas.openxmlformats.org/drawingml/2006/table"), table)));
    }

    private (DocPicture Picture, string Alt)? LoadPicture(ImageBlock image)
    {
        try
        {
            return (DocPicture.Load(_resolve(image.Source)), image.Alt);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            _notes.Add($"The picture {image.Source} was left out: {ex.Message}.");
            return null;
        }
    }

    private static XElement Picture(int id, string rel, (DocPicture Picture, string Alt) picture, long x, long y, long width, long height)
    {
        // As large as the area allows, its proportions kept, centred in it.
        var scale = Math.Min((double)width / picture.Picture.Width, (double)height / picture.Picture.Height);
        long cx = (long)(picture.Picture.Width * scale), cy = (long)(picture.Picture.Height * scale);
        return new XElement(P + "pic",
            new XElement(P + "nvPicPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Picture {id - 1}"), new XAttribute("descr", picture.Alt)),
                new XElement(P + "cNvPicPr", new XElement(A + "picLocks", new XAttribute("noChangeAspect", 1))),
                new XElement(P + "nvPr")),
            new XElement(P + "blipFill", new XElement(A + "blip", new XAttribute(R + "embed", rel)), new XElement(A + "stretch", new XElement(A + "fillRect"))),
            new XElement(P + "spPr", Transform(x + (width - cx) / 2, y + (height - cy) / 2, cx, cy),
                new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))));
    }

    // ---- master, layouts and theme ---------------------------------------------------------------------

    private static XElement Shapes(params XElement[] shapes) => new(P + "spTree",
        new XElement(P + "nvGrpSpPr", new XElement(P + "cNvPr", new XAttribute("id", 1), new XAttribute("name", "")), new XElement(P + "cNvGrpSpPr"), new XElement(P + "nvPr")),
        new XElement(P + "grpSpPr", Transform(0, 0, 0, 0, group: true)),
        shapes);

    private static XElement MasterPlaceholder(int id, string name, XElement ph, long x, long y, long cx, long cy, string anchor) =>
        new(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr", new XElement(A + "spLocks", new XAttribute("noGrp", 1))),
                new XElement(P + "nvPr", ph)),
            new XElement(P + "spPr", Transform(x, y, cx, cy), new XElement(A + "prstGeom", new XAttribute("prst", "rect"), new XElement(A + "avLst"))),
            new XElement(P + "txBody",
                new XElement(A + "bodyPr", new XAttribute("vert", "horz"), new XAttribute("lIns", 91440), new XAttribute("tIns", 45720), new XAttribute("rIns", 91440),
                    new XAttribute("bIns", 45720), new XAttribute("rtlCol", 0), new XAttribute("anchor", anchor), new XElement(A + "normAutofit")),
                new XElement(A + "lstStyle"),
                new XElement(A + "p", new XElement(A + "endParaRPr", new XAttribute("lang", "en-US")))));

    private static XElement Level(string name, long marL, long indent, int size, bool bullet, bool major = false) =>
        new(A + name, new XAttribute("marL", marL), new XAttribute("indent", indent), new XAttribute("algn", "l"), new XAttribute("defTabSz", 914400),
            new XAttribute("rtl", 0), new XAttribute("eaLnBrk", 1), new XAttribute("latinLnBrk", 0), new XAttribute("hangingPunct", 1),
            new XElement(A + "lnSpc", new XElement(A + "spcPct", new XAttribute("val", 90000))),
            new XElement(A + "spcBef", major ? new XElement(A + "spcPct", new XAttribute("val", 0)) : new XElement(A + "spcPts", new XAttribute("val", 1000))),
            bullet ? new XElement(A + "buFont", new XAttribute("typeface", "Arial")) : null,
            bullet ? new XElement(A + "buChar", new XAttribute("char", "•")) : new XElement(A + "buNone"),
            new XElement(A + "defRPr", new XAttribute("sz", size), new XAttribute("kern", 1200),
                new XElement(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "tx1"))),
                new XElement(A + "latin", new XAttribute("typeface", major ? "+mj-lt" : "+mn-lt")),
                new XElement(A + "ea", new XAttribute("typeface", major ? "+mj-ea" : "+mn-ea")),
                new XElement(A + "cs", new XAttribute("typeface", major ? "+mj-cs" : "+mn-cs"))));

    private static XDocument Master() => new(new XElement(P + "sldMaster", Namespaces(),
        new XElement(P + "cSld",
            new XElement(P + "bg", new XElement(P + "bgRef", new XAttribute("idx", 1001), new XElement(A + "schemeClr", new XAttribute("val", "bg1")))),
            Shapes(
                MasterPlaceholder(2, "Title Placeholder 1", new XElement(P + "ph", new XAttribute("type", "title")), 838200, 365125, 10515600, 1325563, "ctr"),
                MasterPlaceholder(3, "Text Placeholder 2", new XElement(P + "ph", new XAttribute("type", "body"), new XAttribute("idx", 1)), Left, Top, Width, Height, "t"))),
        new XElement(P + "clrMap", new XAttribute("bg1", "lt1"), new XAttribute("tx1", "dk1"), new XAttribute("bg2", "lt2"), new XAttribute("tx2", "dk2"),
            new XAttribute("accent1", "accent1"), new XAttribute("accent2", "accent2"), new XAttribute("accent3", "accent3"), new XAttribute("accent4", "accent4"),
            new XAttribute("accent5", "accent5"), new XAttribute("accent6", "accent6"), new XAttribute("hlink", "hlink"), new XAttribute("folHlink", "folHlink")),
        new XElement(P + "sldLayoutIdLst",
            new XElement(P + "sldLayoutId", new XAttribute("id", 2147483649), new XAttribute(R + "id", "rId1")),
            new XElement(P + "sldLayoutId", new XAttribute("id", 2147483650), new XAttribute(R + "id", "rId2"))),
        new XElement(P + "txStyles",
            new XElement(P + "titleStyle", Level("lvl1pPr", 0, 0, 4000, bullet: false, major: true)),
            new XElement(P + "bodyStyle",
                Level("lvl1pPr", 228600, -228600, 2800, true), Level("lvl2pPr", 685800, -228600, 2400, true), Level("lvl3pPr", 1143000, -228600, 2000, true),
                Level("lvl4pPr", 1600200, -228600, 1800, true), Level("lvl5pPr", 2057400, -228600, 1800, true)),
            new XElement(P + "otherStyle", new XElement(A + "defPPr", new XElement(A + "defRPr", new XAttribute("lang", "en-US"))),
                Level("lvl1pPr", 0, 0, 1800, bullet: false)))));

    private static XDocument Layout(bool title)
    {
        XElement Sp(int id, string name, XElement ph, XElement? spPr, XElement? lstStyle, string? anchor) => new(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr", new XElement(A + "spLocks", new XAttribute("noGrp", 1))),
                new XElement(P + "nvPr", ph)),
            spPr ?? new XElement(P + "spPr"),
            new XElement(P + "txBody",
                new XElement(A + "bodyPr", anchor is null ? null : new XAttribute("anchor", anchor), new XElement(A + "normAutofit")),
                lstStyle ?? new XElement(A + "lstStyle"),
                new XElement(A + "p", new XElement(A + "endParaRPr", new XAttribute("lang", "en-US")))));
        var shapes = title
            ? Shapes(
                Sp(2, "Title 1", new XElement(P + "ph", new XAttribute("type", "ctrTitle")), new XElement(P + "spPr", Transform(1524000, 1122363, 9144000, 2387600)),
                    new XElement(A + "lstStyle", new XElement(A + "lvl1pPr", new XAttribute("algn", "ctr"), new XElement(A + "defRPr", new XAttribute("sz", 6000)))), "b"),
                Sp(3, "Subtitle 2", new XElement(P + "ph", new XAttribute("type", "subTitle"), new XAttribute("idx", 1)), new XElement(P + "spPr", Transform(1524000, 3602038, 9144000, 1655762)),
                    new XElement(A + "lstStyle", new XElement(A + "lvl1pPr", new XAttribute("marL", 0), new XAttribute("indent", 0), new XAttribute("algn", "ctr"),
                        new XElement(A + "buNone"), new XElement(A + "defRPr", new XAttribute("sz", 2400)))), null))
            : Shapes(
                Sp(2, "Title 1", new XElement(P + "ph", new XAttribute("type", "title")), null, null, null),
                Sp(3, "Content Placeholder 2", new XElement(P + "ph", new XAttribute("idx", 1)), null, null, null));
        return new XDocument(new XElement(P + "sldLayout", Namespaces(), new XAttribute("type", title ? "title" : "obj"), new XAttribute("preserve", 1),
            new XElement(P + "cSld", new XAttribute("name", title ? "Title Slide" : "Title and Content"), shapes),
            new XElement(P + "clrMapOvr", new XElement(A + "masterClrMapping"))));
    }

    private static XDocument Theme()
    {
        static XElement Color(string name, string rgb) => new(A + name, new XElement(A + "srgbClr", new XAttribute("val", rgb)));
        static XElement Placeholder() => new(A + "solidFill", new XElement(A + "schemeClr", new XAttribute("val", "phClr")));
        static XElement Fonts(string name, string latin) => new(A + name,
            new XElement(A + "latin", new XAttribute("typeface", latin)), new XElement(A + "ea", new XAttribute("typeface", "")), new XElement(A + "cs", new XAttribute("typeface", "")));
        return new XDocument(new XElement(A + "theme", new XAttribute(XNamespace.Xmlns + "a", A), new XAttribute("name", "Office Theme"),
            new XElement(A + "themeElements",
                new XElement(A + "clrScheme", new XAttribute("name", "Office"),
                    new XElement(A + "dk1", new XElement(A + "sysClr", new XAttribute("val", "windowText"), new XAttribute("lastClr", "000000"))),
                    new XElement(A + "lt1", new XElement(A + "sysClr", new XAttribute("val", "window"), new XAttribute("lastClr", "FFFFFF"))),
                    Color("dk2", "44546A"), Color("lt2", "E7E6E6"), Color("accent1", "4472C4"), Color("accent2", "ED7D31"), Color("accent3", "A5A5A5"),
                    Color("accent4", "FFC000"), Color("accent5", "5B9BD5"), Color("accent6", "70AD47"), Color("hlink", "0563C1"), Color("folHlink", "954F72")),
                new XElement(A + "fontScheme", new XAttribute("name", "Office"), Fonts("majorFont", "Calibri Light"), Fonts("minorFont", "Calibri")),
                new XElement(A + "fmtScheme", new XAttribute("name", "Office"),
                    new XElement(A + "fillStyleLst", Placeholder(), Placeholder(), Placeholder()),
                    new XElement(A + "lnStyleLst", new[] { 6350, 12700, 19050 }.Select(w => new XElement(A + "ln", new XAttribute("w", w), Placeholder()))),
                    new XElement(A + "effectStyleLst", Enumerable.Range(0, 3).Select(_ => new XElement(A + "effectStyle", new XElement(A + "effectLst")))),
                    new XElement(A + "bgFillStyleLst", Placeholder(), Placeholder(), Placeholder()))),
            new XElement(A + "objectDefaults"),
            new XElement(A + "extraClrSchemeLst")));
    }
}
