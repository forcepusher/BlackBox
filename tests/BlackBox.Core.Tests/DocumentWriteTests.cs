using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using BlackBox.Core.Documents;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using SkiaSharp;
using UglyToad.PdfPig;
using Xunit;

namespace BlackBox.Core.Tests;

// write_file making Word, Excel, PowerPoint and PDF files from Markdown, and edit_file changing them in place.
public class DocumentWriteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-docs-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };

    private Task<ToolResult> Write(string file, string content, bool append = false) =>
        new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["content"] = content, ["append"] = append }, Ctx(), CancellationToken.None);

    private Task<ToolResult> Edit(string file, string oldText, string newText, bool all = false) =>
        new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = oldText, ["new_text"] = newText, ["replace_all"] = all }, Ctx(), CancellationToken.None);

    /// <summary>What read_file shows, without its line numbers.</summary>
    private async Task<string> Read(string file) => System.Text.RegularExpressions.Regex.Replace(
        (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = file }, Ctx(), CancellationToken.None)).Text.ReplaceLineEndings("\n"), @"(?m)^\d+\t", "");

    private const string Letter = """
        # Quarterly report

        Prepared for **Northwind** by the *finance team*.
        12 Harbour Street
        Seattle

        ## Highlights

        - Revenue grew **12%**
        - Two new stores
          - Airport
          - Station
        1. Hire a manager
        2. Open in March

        | Store | Q3 sales | Change |
        |-------|---------:|:------:|
        | Airport | $37,450 | +8% |
        | Station | $21,300 | -2% |

        > Best quarter since 2019.

        ```
        total = sum(sales)
            print(total)
        ```

        See [the dashboard](https://example.com/q3) for more.

        ---

        \newpage

        ## Next steps
        Done.
        """;

    [Fact]
    public async Task A_word_document_is_made_from_markdown_with_real_headings_lists_and_tables()
    {
        var result = await Write("report.docx", Letter);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("a Word document: 3 headings", result.Text);

        var text = await Read("report.docx");
        Assert.Contains("# Quarterly report", text);
        Assert.Contains("## Highlights", text);
        Assert.Contains("Prepared for **Northwind** by the finance team.\n12 Harbour Street\nSeattle", text.ReplaceLineEndings("\n"));
        Assert.Contains("- Revenue grew **12%**", text);
        Assert.Contains("| **Store** | **Q3 sales** | **Change** |", text);
        Assert.Contains("| Airport | $37,450 | +8% |", text);

        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "report.docx"));
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var body = XDocument.Load(zip.GetEntry("word/document.xml")!.Open()).Root!.Element(w + "body")!;
        var paragraphs = body.Elements(w + "p").ToList();
        // Lists use Word's numbering: bullets one level deeper for the nested ones, numbers on their own list.
        var items = paragraphs.Where(p => p.Descendants(w + "numPr").Any()).ToList();
        Assert.Equal(["0", "0", "1", "1", "0", "0"], items.Select(p => (string)p.Descendants(w + "ilvl").Single().Attribute(w + "val")!));
        Assert.NotEqual((string)items[0].Descendants(w + "numId").Single().Attribute(w + "val")!, (string)items[4].Descendants(w + "numId").Single().Attribute(w + "val")!);
        Assert.Single(body.Elements(w + "tbl"));
        Assert.Contains(body.Descendants(w + "br"), br => (string?)br.Attribute(w + "type") == "page");
        Assert.Single(body.Descendants(w + "hyperlink"));
        Assert.Contains(zip.Entries, e => e.FullName == "word/numbering.xml");
    }

    [Fact]
    public async Task A_workbook_gets_a_sheet_per_heading_and_typed_values_and_formulas()
    {
        var result = await Write("budget.xlsx", """
            # Budget

            | Item | Q1 | Share | Due |
            |------|----|-------|-----|
            | Rent | $1,200.00 | 40% | 2026-11-01 |
            | Food | 850 | 28.5% | 2026-11-15 |
            | Code | 007 | | |
            | **Total** | =SUM(B2:B4) | =SUM(C2:C3) | |

            ## Notes
            Paid on the first of each month.
            """);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("\"Budget\" (A1:D5), \"Notes\" (A1:A1)", result.Text);

        var text = await Read("budget.xlsx");
        Assert.Contains("# Sheet \"Budget\"", text);
        // Numbers as Excel shows them, in a form edit_file reads back as the same value and format.
        Assert.Contains("B2 $1,200.00", text);
        Assert.Contains("C2 40%", text);
        Assert.Contains("D2 2026-11-01", text); // not 46327, Excel's number for that day
        Assert.Contains("B4 007", text);   // leading zeros stay text
        Assert.Contains("B5 =SUM(B2:B4)", text);
        Assert.Contains("# Sheet \"Notes\"", text);
        Assert.Contains("A1 Paid on the first of each month.", text);

        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "budget.xlsx"));
        var formats = XDocument.Load(zip.GetEntry("xl/styles.xml")!.Open()).Descendants().Where(e => e.Name.LocalName == "numFmt")
            .Select(e => (string)e.Attribute("formatCode")!).ToList();
        Assert.Contains("\"$\"#,##0.00", formats);
        Assert.Contains("yyyy-mm-dd", formats);
        Assert.Contains("fullCalcOnLoad=\"1\"", XDocument.Load(zip.GetEntry("xl/workbook.xml")!.Open()).ToString());
    }

    [Fact]
    public async Task Documents_are_written_whatever_the_digits_characters_or_culture()
    {
        // Arabic-Indic and full-width digits matched \d and then failed int.Parse and double.Parse;
        // a form feed or a terminal's escape code made the XML writer throw; and with a Finnish
        // culture the document's date came out as 2026-10-17T12.34.56Z.
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("fi-FI");
        try
        {
            Assert.False((await Write("arabic.docx", "١. البند الأول\n٢. البند الثاني\n\nPage\fbreak and \u001b[31mred\u001b[0m")).IsError);
            Assert.False((await Write("numbers.xlsx", "| A | B |\n|---|---|\n| ١٢٣ | １２３ |\n| 1e400 | 5 |")).IsError);
            var text = await Read("numbers.xlsx");
            Assert.Contains("A2 ١٢٣", text);
            Assert.Contains("A3 1e400", text);
            using var zip = ZipFile.OpenRead(Path.Combine(_dir, "arabic.docx"));
            var core = XDocument.Load(zip.GetEntry("docProps/core.xml")!.Open()).ToString();
            Assert.Matches(@"<dcterms:created[^>]*>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z<", core);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task A_workbook_of_plain_numbers_says_how_to_show_them_in_dollars()
    {
        // Asked for amounts in dollars, a model wrote plain numbers and then took the workbook apart.
        var plain = await Write("plain.xlsx", "| Item | Amount |\n|---|---|\n| Rent | 1200 |\n| Total | =SUM(B2:B2) |");
        Assert.Contains("written as $1,200.00, 12% or 2026-10-16 they show in dollars", plain.Text);
        var dollars = await Write("dollars.xlsx", "| Item | Amount |\n|---|---|\n| Rent | $1,200.00 |\n| Total | =SUM(B2:B2) |");
        Assert.DoesNotContain("show in dollars", dollars.Text);
    }

    [Fact]
    public async Task A_presentation_gets_a_slide_per_section_with_a_title_slide_first()
    {
        using (var bmp = new SKBitmap(400, 300))
        {
            bmp.Erase(SKColors.SteelBlue);
            using var png = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, "chart.png"), png.ToArray());
        }
        var result = await Write("deck.pptx", """
            # Q3 results
            Northwind, October 2026

            ---

            ## Sales
            - Airport up **8%**
            - Station down 2%
              - roadworks

            ![Sales chart](chart.png)

            ---

            ## By store
            | Store | Sales |
            |---|---:|
            | Airport | 37,450 |
            """);
        Assert.False(result.IsError, result.Text);
        Assert.Contains("a presentation of 3 slides: Q3 results; Sales; By store", result.Text);

        var text = await Read("deck.pptx");
        Assert.Contains("--- slide 1 of 3 ---\nQ3 results\nNorthwind, October 2026", text.ReplaceLineEndings("\n"));
        Assert.Contains("Airport up 8%", text);
        Assert.Contains("roadworks", text);
        Assert.Contains("Airport\n37,450", text.ReplaceLineEndings("\n"));
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "deck.pptx"));
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("ppt/media/image1.png", StringComparison.Ordinal));
        Assert.Contains("slideLayout1.xml", new StreamReader(zip.GetEntry("ppt/slides/_rels/slide1.xml.rels")!.Open()).ReadToEnd());

        // A subheading under the first title makes a title slide too, as qwen3.8 wrote its first one.
        Assert.False((await Write("sub.pptx", "# Northwind Coffee Roasters\n## Q3 2026 Summary\n\n---\n\n## Highlights\n- Cold brew")).IsError);
        using var sub = ZipFile.OpenRead(Path.Combine(_dir, "sub.pptx"));
        Assert.Contains("slideLayout1.xml", new StreamReader(sub.GetEntry("ppt/slides/_rels/slide1.xml.rels")!.Open()).ReadToEnd());
    }

    [Fact]
    public async Task A_pdf_is_laid_out_with_wrapped_text_tables_and_page_numbers()
    {
        var longText = string.Join(" ", Enumerable.Repeat("The quick brown fox jumps over the lazy dog.", 40));
        var result = await Write("out.pdf", Letter.Replace("Done.", longText + "\n\nCafé crème, “quotes” — and 5 £."));
        Assert.False(result.IsError, result.Text);
        Assert.Contains("a PDF of 2 pages", result.Text);

        using var pdf = PdfDocument.Open(Path.Combine(_dir, "out.pdf"));
        var first = pdf.GetPage(1).Text;
        Assert.Contains("Quarterly report", first);
        Assert.Contains("Airport", first);
        Assert.Contains("$37,450", first);
        Assert.Contains("total = sum(sales)", first);
        Assert.Contains("1 / 2", first);
        var second = pdf.GetPage(2).Text;
        Assert.Contains("Next steps", second);
        Assert.Contains("Café crème, “quotes” — and 5 £.", second);

        // Plain ASCII needs no font of its own: the reader's Helvetica, a few KB.
        Assert.False((await Write("plain.pdf", "# Hello\n\n- one\n- two\n\nPlain text.")).IsError);
        Assert.True(new FileInfo(Path.Combine(_dir, "plain.pdf")).Length < 20_000);
        using var plain = PdfDocument.Open(Path.Combine(_dir, "plain.pdf"));
        Assert.Contains("Plain text.", plain.GetPage(1).Text);
    }

    [Fact]
    public async Task Text_in_another_script_embeds_a_font_that_has_it()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Arial from Windows.");
        var result = await Write("ru.pdf", "# Отчёт\n\nПродажи выросли на 12% (€5).");
        Assert.False(result.IsError, result.Text);
        using var pdf = PdfDocument.Open(Path.Combine(_dir, "ru.pdf"));
        Assert.Contains("Продажи выросли на 12% (€5)", pdf.GetPage(1).Text);
        // The font goes in with only the letters used.
        Assert.True(new FileInfo(Path.Combine(_dir, "ru.pdf")).Length < 300_000);
    }

    [Fact]
    public async Task Arabic_and_Hebrew_in_a_quote_or_code_are_written_in_the_regular_font()
    {
        // Arial Italic and Consolas have no Arabic, Consolas no Hebrew: a quote or code in them made
        // the whole PDF fail with "font does not contain a character".
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Arial and Consolas from Windows.");
        var result = await Write("rtl.pdf", "# تقرير\n\n> اقتباس *with English* in it\n\nThe key is `מפתח` here.\n\n```\nקוד عربي\n```");
        Assert.False(result.IsError, result.Text);
        Assert.DoesNotContain("came out as ?", result.Text);
        using var pdf = PdfDocument.Open(Path.Combine(_dir, "rtl.pdf"));
        Assert.Contains("with English", pdf.GetPage(1).Text);
    }

    [Fact]
    public async Task An_existing_document_is_not_overwritten_but_one_made_here_can_be_added_to()
    {
        File.WriteAllBytes(Path.Combine(_dir, "theirs.docx"), File.ReadAllBytes(await Make("seed.docx", "# Theirs")));
        var refused = await Write("theirs.docx", "# Mine");
        Assert.True(refused.IsError);
        Assert.Contains("use edit_file", refused.Text);

        Assert.False((await Write("notes.docx", "# Notes\n\nFirst part.")).IsError);
        var added = await Write("notes.docx", "Second part.", append: true);
        Assert.False(added.IsError, added.Text);
        Assert.Contains("Added to", added.Text);
        Assert.Contains("First part.\nSecond part.", (await Read("notes.docx")).ReplaceLineEndings("\n"));

        Assert.True((await Write("theirs.docx", "More.", append: true)).IsError);
    }

    private async Task<string> Make(string name, string markdown)
    {
        Assert.False((await Write(name, markdown)).IsError);
        return Path.Combine(_dir, name);
    }

    // ---- edit_file -------------------------------------------------------------------------------------

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>A Word file whose body is <paramref name="body"/>, as Word writes it: text split into runs.</summary>
    private string WordFile(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Entry(string entry, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(xml);
            }
            Entry("[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>""");
            Entry("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>""");
            Entry("word/document.xml", $"""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body>{body}<w:sectPr/></w:body></w:document>""");
        }
        return path;
    }

    private static XElement Body(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return XDocument.Load(zip.GetEntry("word/document.xml")!.Open()).Root!.Element(W + "body")!;
    }

    private const string Contract = """
        <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Service con</w:t></w:r><w:r><w:t>tract</w:t></w:r></w:p>
        <w:p><w:r><w:t xml:space="preserve">Signed on </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>1 March</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t xml:space="preserve"> 2026</w:t></w:r><w:r><w:t xml:space="preserve"> by ACME Ltd.</w:t></w:r></w:p>
        <w:p><w:r><w:t>The client’s fee is due monthly.</w:t></w:r></w:p>
        <w:tbl><w:tr><w:tc><w:p><w:r><w:t>ACME Ltd</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
        <w:p><w:r><w:t>Signed by ACME Ltd and us.</w:t></w:r></w:p>
        """;

    [Fact]
    public async Task Word_text_is_replaced_across_runs_and_keeps_its_formatting()
    {
        var path = WordFile("contract.docx", Contract);

        // Copied from read_file with its marks; the date spans two bold runs and only it changes.
        var result = await Edit("contract.docx", "Signed on **1 March 2026** by ACME Ltd.", "Signed on **15 March 2026** by ACME Ltd.");
        Assert.False(result.IsError, result.Text);
        var signed = Body(path).Elements(W + "p").ElementAt(1);
        Assert.Equal("Signed on 15 March 2026 by ACME Ltd.", signed.Value);
        // Only the 5 went in, bold like the date around it.
        Assert.NotNull(signed.Elements(W + "r").Single(r => r.Value == "5").Element(W + "rPr")?.Element(W + "b"));
        Assert.Contains("Signed on **15 March 2026** by ACME Ltd.", await Read("contract.docx"));

        // The heading is matched across its two runs, without its "# ".
        Assert.False((await Edit("contract.docx", "# Service contract", "# Service agreement")).IsError);
        var heading = Body(path).Elements(W + "p").First();
        Assert.Equal("Service agreement", heading.Value);
        Assert.Equal("Heading1", (string?)heading.Descendants(W + "pStyle").Single().Attribute(W + "val"));

        // A straight quote finds the curly one, and the curly one stays.
        Assert.False((await Edit("contract.docx", "client's fee is due monthly", "client's fee is due quarterly")).IsError);
        Assert.Equal("The client’s fee is due quarterly.", Body(path).Elements(W + "p").ElementAt(2).Value);

        // Three places: refused without replace_all, all changed with it, the table cell too.
        var many = await Edit("contract.docx", "ACME Ltd", "Globex GmbH");
        Assert.True(many.IsError);
        Assert.Contains("occurs 3 times", many.Text);
        Assert.False((await Edit("contract.docx", "ACME Ltd", "Globex GmbH", all: true)).IsError);
        var text = await Read("contract.docx");
        Assert.DoesNotContain("ACME", text);
        Assert.Contains("| Globex GmbH |", text);
    }

    [Fact]
    public async Task New_paragraphs_and_headings_come_from_line_breaks_and_whole_paragraphs_can_go()
    {
        var path = WordFile("plan.docx", """
            <w:p><w:pPr><w:pStyle w:val="ListParagraph"/></w:pPr><w:r><w:t>Step one</w:t></w:r></w:p>
            <w:p><w:r><w:t>Closing words.</w:t></w:r></w:p>
            <w:p><w:r><w:t>Remove me.</w:t></w:r></w:p>
            <w:p><w:r><w:t>Keep me.</w:t></w:r></w:p>
            """);

        Assert.False((await Edit("plan.docx", "Step one", "Step one\nStep two")).IsError);
        var paragraphs = Body(path).Elements(W + "p").ToList();
        Assert.Equal(["Step one", "Step two", "Closing words.", "Remove me.", "Keep me."], paragraphs.Select(p => p.Value));
        Assert.Equal("ListParagraph", (string?)paragraphs[1].Descendants(W + "pStyle").Single().Attribute(W + "val"));

        Assert.False((await Edit("plan.docx", "Closing words.", "Closing words.\n# Appendix\nSee the **attached** list.")).IsError);
        paragraphs = Body(path).Elements(W + "p").ToList();
        Assert.Equal("Heading1", (string?)paragraphs[3].Descendants(W + "pStyle").Single().Attribute(W + "val"));
        Assert.Contains(paragraphs[4].Elements(W + "r"), r => r.Value == "attached" && r.Element(W + "rPr")?.Element(W + "b") is not null);

        // Text across two paragraphs, and a paragraph taken out whole.
        Assert.False((await Edit("plan.docx", "Remove me.\nKeep", "Keep")).IsError);
        Assert.Equal(["Step one", "Step two", "Closing words.", "Appendix", "See the attached list.", "Keep me."], Body(path).Elements(W + "p").Select(p => p.Value));
        Assert.False((await Edit("plan.docx", "See the attached list.", "")).IsError);
        Assert.DoesNotContain(Body(path).Elements(W + "p"), p => p.Value.Contains("attached"));
    }

    [Fact]
    public async Task Slides_are_edited_in_place()
    {
        await Write("deck.pptx", "# Q3 results\nNorthwind\n\n---\n\n## Sales\n- Airport up 8%\n- Station down 2%");
        var result = await Edit("deck.pptx", "Station down 2%", "Station down **3%**");
        Assert.False(result.IsError, result.Text);
        var text = await Read("deck.pptx");
        Assert.Contains("Station down 3%", text);
        Assert.Contains("Airport up 8%", text);
    }

    [Fact]
    public async Task Cells_are_set_as_read_file_shows_them_and_a_stale_read_is_refused()
    {
        await Write("budget.xlsx", "# Budget\n\n| Item | Cost |\n|---|---|\n| Rent | 1200 |\n| Food | 850 |\n| Total | =SUM(B2:B3) |");

        var stale = await Edit("budget.xlsx", "B4 =SUM(B2:B2) → 1200", "B4 =SUM(B2:B5)");
        Assert.True(stale.IsError);
        Assert.Contains("holds =SUM(B2:B3)", stale.Text);

        var result = await Edit("budget.xlsx", "A4 Total | B4 =SUM(B2:B3) → 2050", "A4 Travel | B4 300\nA5 Total | B5 =SUM(B2:B4)\nBudget!C1 Paid");
        Assert.False(result.IsError, result.Text);
        Assert.Contains("A4, B4, A5, B5, C1 on sheet \"Budget\"", result.Text);
        var text = await Read("budget.xlsx");
        Assert.Contains("A4 Travel | B4 300", text);
        Assert.Contains("A5 Total | B5 =SUM(B2:B4)", text);
        Assert.Contains("C1 Paid", text);

        // Plain text is found in the cells.
        Assert.False((await Edit("budget.xlsx", "Travel", "Trains")).IsError);
        Assert.Contains("A4 Trains", await Read("budget.xlsx"));
    }

    [Fact]
    public async Task A_number_changed_under_a_total_has_excel_work_the_total_out_again()
    {
        // In a workbook Excel saved last, the total it kept in the file was shown unchanged.
        await Write("budget.xlsx", "# Budget\n\n| Item | Cost |\n|---|---|\n| Rent | 1200 |\n| Food | 850 |\n| **Total** | =SUM(B2:B3) |");
        var path = Path.Combine(_dir, "budget.xlsx");
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
        {
            // As Excel leaves it: the total worked out, and no recalculation asked for.
            var entry = zip.GetEntry("xl/workbook.xml")!;
            XDocument workbook;
            using (var stream = entry.Open()) workbook = XDocument.Load(stream);
            workbook.Descendants(s + "calcPr").Attributes("fullCalcOnLoad").Remove();
            entry.Delete();
            using (var stream = zip.CreateEntry("xl/workbook.xml").Open()) workbook.Save(stream);
        }

        var result = await Edit("budget.xlsx", "", "B3 900");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Excel works the formulas out when it opens the file", result.Text);
        using var check = ZipFile.OpenRead(path);
        Assert.Equal("1", (string?)XDocument.Load(check.GetEntry("xl/workbook.xml")!.Open()).Descendants(s + "calcPr").Single().Attribute("fullCalcOnLoad"));
    }

    [Fact]
    public async Task Rows_are_inserted_above_a_total_and_deleted_with_formulas_following()
    {
        await Write("budget.xlsx", "# Budget\n\n| Item | Cost |\n|---|---|\n| Rent | $1,200 |\n| Food | $850 |\n| **Total** | =SUM(B2:B3) |");
        var path = Path.Combine(_dir, "budget.xlsx");

        var result = await Edit("budget.xlsx", "", "insert row 4\nA4 Internet | B4 40\nB5 =SUM(B2:B4)");
        Assert.False(result.IsError, result.Text);
        Assert.Contains(": Inserted row 4 on sheet \"Budget\", then set A4, B4, B5", result.Text);
        var text = await Read("budget.xlsx");
        Assert.Contains("A4 Internet | B4 $40", text); // in dollars, as the row above it
        Assert.Contains("A5 Total | B5 =SUM(B2:B4)", text);
        Assert.Contains("(bold: A1, B1, A5)", text);

        // The new row looks like the one above it, the total row stays bold.
        XNamespace s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        string? Style(string cell)
        {
            using var zip = ZipFile.OpenRead(path);
            return (string?)XDocument.Load(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open()).Descendants(s + "c").Single(c => (string?)c.Attribute("r") == cell).Attribute("s");
        }
        Assert.Equal(Style("B3"), Style("B4"));
        Assert.Equal(Style("A3"), Style("A4"));
        Assert.NotEqual(Style("A4"), Style("A5"));

        // Deleting Food: the total's range loses that row and the total moves up.
        Assert.False((await Edit("budget.xlsx", "", "delete row 3")).IsError);
        text = await Read("budget.xlsx");
        Assert.Contains("A3 Internet | B3 $40", text);
        Assert.Contains("A4 Total | B4 =SUM(B2:B3)", text);

        // Ten million rows ran out of memory, past where a sheet ends anyway.
        var huge = await Edit("budget.xlsx", "", "insert rows 2-9999999");
        Assert.True(huge.IsError);
        Assert.Contains("a sheet ends at row 1,048,576", huge.Text);
    }

    [Theory]
    [InlineData("SUM(B2:B6)", true, 7, 1, "SUM(B2:B6)")]          // ends above the new row: Excel leaves it
    [InlineData("SUM(B2:B7)+B7*2", true, 7, 1, "SUM(B2:B8)+B8*2")] // spans it: grows
    [InlineData("Budget!$B$2:$B$7", false, 7, 2, "Budget!$B$2:$B$9")]
    [InlineData("'Other sheet'!B9+B9", true, 7, 1, "'Other sheet'!B9+B10")]
    [InlineData("SUM(B2:B6)", false, 3, 2, "SUM(B2:B6)")]           // another sheet's own cells
    [InlineData("SUM(B2:B6)+B5", true, -3, 2, "SUM(B2:B4)+B3")]      // rows 3-4 deleted
    [InlineData("B3+LOG10(B4)", true, -3, 2, "#REF!+LOG10(#REF!)")]
    public void References_move_with_inserted_and_deleted_rows(string formula, bool sameSheet, int at, int count, string expected)
    {
        var insert = at > 0;
        Assert.Equal(expected, SheetEdit.MoveReferences(formula, sameSheet, "Budget", insert, Math.Abs(at), count));
    }

    [Fact]
    public void A_shared_formula_reads_right_from_every_cell_it_moves_to()
    {
        Assert.Equal("SUM(C2:C6)*$A$1+D7", SheetEdit.Shift("SUM(B2:B6)*$A$1+C7", 0, 1));
        Assert.Equal("B3&\"A1\"&LOG10(C4)", SheetEdit.Shift("B2&\"A1\"&LOG10(C3)", 1, 0));
    }

    [Fact]
    public async Task A_pdf_cannot_be_edited_in_place()
    {
        await Write("out.pdf", "# Hello");
        var result = await Edit("out.pdf", "Hello", "Hi");
        Assert.True(result.IsError);
        Assert.Contains("can't be changed in place", result.Text);
    }

    [Fact]
    public void Markdown_is_read_into_blocks()
    {
        var blocks = MarkdownDocument.Parse("# Using C#\n\nText with \\*stars\\* and C:\\Users\\me\n- [ ] call\n- [x] book\n<https://example.com>");
        Assert.Equal("Using C#", MarkdownDocument.Plain(((HeadingBlock)blocks[0]).Text));
        Assert.Equal("Text with *stars* and C:\\Users\\me", MarkdownDocument.Plain(((ParagraphBlock)blocks[1]).Text));
        Assert.Equal("☐ call", MarkdownDocument.Plain(((ListItemBlock)blocks[2]).Text));
        Assert.Equal("☑ book", MarkdownDocument.Plain(((ListItemBlock)blocks[3]).Text));
        Assert.Equal("https://example.com", ((ParagraphBlock)blocks[4]).Text.Single().Link);
    }

    /// <summary>
    /// Opt-in (BB_DOC_OUT): the sample documents, made and edited, in a folder to open in Word,
    /// Excel and PowerPoint, which say so when a file is damaged in a way no test here sees.
    /// </summary>
    [Fact]
    public async Task Samples_to_open_in_office()
    {
        var output = Environment.GetEnvironmentVariable("BB_DOC_OUT");
        Assert.SkipWhen(output is null, "Set BB_DOC_OUT to a folder to run.");
        using (var bmp = new SKBitmap(640, 360))
        {
            bmp.Erase(SKColors.SteelBlue);
            using var png = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, "chart.png"), png.ToArray());
        }
        var letter = Letter.Replace("> Best quarter since 2019.", "> Best quarter since 2019.\n\n![Sales chart](chart.png)");
        async Task Ok(Task<ToolResult> step) { var r = await step; Assert.False(r.IsError, r.Text); }
        await Ok(Write("report.docx", letter));
        await Ok(Write("report.pdf", letter + "\n\nCafé crème, “quotes” — and €5."));
        await Ok(Write("budget.xlsx", """
            # Budget
            | Item | Q1 | Share | Due |
            |------|----|-------|-----|
            | Rent | $1,200.00 | 40% | 2026-11-01 |
            | Food | 850 | 28.5% | 2026-11-15 |
            | **Total** | =SUM(B2:B3) | =SUM(C2:C3) | |
            ## Notes
            Paid on the first of each month.
            """));
        await Ok(Write("deck.pptx", "# Q3 results\nNorthwind, October 2026\n\n---\n\n## Sales\n- Airport up **8%**\n- Station down 2%\n  - roadworks\n\n![Sales chart](chart.png)\n\n---\n\n## By store\n| Store | Sales |\n|---|---:|\n| Airport | 37,450 |\n| Station | 21,300 |"));
        foreach (var name in new[] { "report.docx", "budget.xlsx", "deck.pptx" })
            File.Copy(Path.Combine(_dir, name), Path.Combine(_dir, "edited-" + name));
        await Ok(Edit("edited-report.docx", "Prepared for **Northwind**", "Prepared for **Contoso**"));
        await Ok(Edit("edited-report.docx", "Two new stores", "Three new stores\nOne closed store"));
        await Ok(Edit("edited-budget.xlsx", "A4 Total | B4 =SUM(B2:B3) → ", "A4 Travel | B4 300\nA5 Total | B5 =SUM(B2:B4)"));
        await Ok(Edit("edited-deck.pptx", "Station down 2%", "Station down **3%**"));
        Directory.CreateDirectory(output!);
        foreach (var file in Directory.GetFiles(_dir).Where(f => !f.EndsWith(".png", StringComparison.Ordinal)))
            File.Copy(file, Path.Combine(output!, Path.GetFileName(file)), overwrite: true);
    }
}
