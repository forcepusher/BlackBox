using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using SkiaSharp;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace BlackBox.Core.Tests;

// read_file on what people hand over besides text: pictures, PDFs and Office documents.
public class ReadFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-read-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ToolContext Ctx(bool seesImages = true) =>
        new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir, SeesImages = seesImages };

    private Task<ToolResult> Read(string file, ToolContext? ctx = null, int? offset = null) =>
        new ReadFileTool().ExecuteAsync(offset is { } o ? new JsonObject { ["path"] = file, ["offset"] = o } : new JsonObject { ["path"] = file }, ctx ?? Ctx(), CancellationToken.None);

    [Fact]
    public async Task A_picture_is_shown_to_the_model_scaled_and_on_white()
    {
        // 4000x500 with a transparent half: scaled to fit 2048 wide, sides on the 32 px patch grid.
        using (var bmp = new SKBitmap(new SKImageInfo(4000, 500, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            bmp.Erase(SKColors.Transparent);
            using (var canvas = new SKCanvas(bmp))
                canvas.DrawRect(0, 0, 2000, 500, new SKPaint { Color = SKColors.Red });
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(_dir, "wide.png"), data.ToArray());
        }

        var result = await Read("wide.png");

        Assert.False(result.IsError);
        Assert.Contains("a 4000x500 picture, shown at 2048x256", result.Text);
        Assert.Equal("The picture wide.png:", result.ImageCaption);
        using var shown = SKBitmap.Decode(result.Image);
        Assert.Equal((2048, 256), (shown.Width, shown.Height));
        Assert.True(shown.GetPixel(1500, 128).Red > 200 && shown.GetPixel(1500, 128).Green > 200); // the transparent half is white, not black

        Assert.Contains("can't see images", (await Read("wide.png", Ctx(seesImages: false))).Text);
    }

    [Fact]
    public async Task A_pdf_comes_back_as_its_text_page_by_page()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4).AddText("Northwind Q3 report", 14, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        var second = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        second.AddText("Airport September 37,450", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 750), font);
        second.AddText("Board meets on 12 November", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 730), font);
        File.WriteAllBytes(Path.Combine(_dir, "report.pdf"), builder.Build());

        var result = await Read("report.pdf");

        Assert.False(result.IsError);
        Assert.Contains("--- page 1 of 2 ---", result.Text);
        Assert.Contains("Northwind Q3 report", result.Text);
        Assert.Contains("--- page 2 of 2 ---", result.Text);
        Assert.Contains("Airport September 37,450", result.Text);
        Assert.Matches(@"\d+\tBoard meets on 12 November", result.Text); // numbered lines, as for text files
    }

    [Fact]
    public async Task A_word_document_keeps_headings_lists_bold_and_tables()
    {
        Package("letter.docx", ("word/document.xml", $"""
            <w:document xmlns:w="{W}"><w:body>
              <w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Thank you</w:t></w:r></w:p>
              <w:p><w:r><w:rPr><w:b/></w:rPr><w:t>Dear Mr. Smith,</w:t></w:r></w:p>
              <w:p><w:r><w:t xml:space="preserve">The heating works </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>again</w:t></w:r><w:r><w:t>.</w:t></w:r></w:p>
              <w:p><w:pPr><w:numPr><w:ilvl w:val="0"/></w:numPr></w:pPr><w:r><w:t>Fast</w:t></w:r></w:p>
              <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Rent</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>1200</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """));

        var text = (await Read("letter.docx")).Text;

        Assert.Contains("1\t# Thank you", text);
        Assert.Contains("2\t**Dear Mr. Smith,**", text);
        Assert.Contains("3\tThe heating works **again**.", text);
        Assert.Contains("4\t- Fast", text);
        Assert.Contains("5\t| Rent | 1200 |", text);
    }

    [Fact]
    public async Task An_excel_workbook_lists_every_cell_with_its_formula()
    {
        Package("budget.xlsx",
            ("xl/workbook.xml", $"""<workbook xmlns="{S}" xmlns:r="{R}"><sheets><sheet name="Budget" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels", $"""<Relationships xmlns="{Pr}"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>"""),
            ("xl/sharedStrings.xml", $"""<sst xmlns="{S}"><si><t>Category</t></si><si><t>Rent</t></si><si><r><t>To</t></r><r><t>tal</t></r></si></sst>"""),
            ("xl/worksheets/sheet1.xml", $"""
                <worksheet xmlns="{S}"><dimension ref="A1:B3"/><sheetData>
                  <row r="1"><c r="A1" t="s"><v>0</v></c></row>
                  <row r="2"><c r="A2" t="s"><v>1</v></c><c r="B2"><v>512.29999999999995</v></c></row>
                  <row r="3"><c r="A3" t="s"><v>2</v></c><c r="B3"><f>SUM(B2:B2)</f><v>512.29999999999995</v></c></row>
                </sheetData></worksheet>
                """));

        var text = (await Read("budget.xlsx")).Text;

        Assert.Contains("1\t# Sheet \"Budget\" (A1:B3)", text);
        Assert.Contains("2\tA1 Category", text);
        // Numbers as Excel shows them, not the 17 digits the file keeps.
        Assert.Contains("3\tA2 Rent | B2 512.3", text);
        Assert.Contains("4\tA3 Total | B3 =SUM(B2:B2) → 512.3", text);
    }

    [Fact]
    public async Task A_presentation_goes_slide_by_slide()
    {
        Package("deck.pptx",
            ("ppt/presentation.xml", $"""<p:presentation xmlns:p="{P}" xmlns:r="{R}"><p:sldIdLst><p:sldId id="256" r:id="rId2"/><p:sldId id="257" r:id="rId3"/></p:sldIdLst></p:presentation>"""),
            ("ppt/_rels/presentation.xml.rels", $"""<Relationships xmlns="{Pr}"><Relationship Id="rId2" Target="slides/slide1.xml"/><Relationship Id="rId3" Target="slides/slide2.xml"/></Relationships>"""),
            ("ppt/slides/slide1.xml", $"""<p:sld xmlns:p="{P}" xmlns:a="{A}"><a:p><a:r><a:t>Q3 at a glance</a:t></a:r></a:p></p:sld>"""),
            ("ppt/slides/slide2.xml", $"""<p:sld xmlns:p="{P}" xmlns:a="{A}"><a:p><a:r><a:t>Online </a:t></a:r><a:r><a:t>up 29%</a:t></a:r></a:p></p:sld>"""));

        var text = (await Read("deck.pptx")).Text;

        Assert.Contains("--- slide 1 of 2 ---", text);
        Assert.Contains("Q3 at a glance", text);
        Assert.Contains("Online up 29%", text);
    }

    [Fact]
    public async Task A_damaged_document_says_so_and_other_binaries_say_what_can_be_read()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.docx"), "not a zip");
        var broken = await Read("broken.docx");
        Assert.True(broken.IsError);
        Assert.Contains("could not be read as a document", broken.Text);

        File.WriteAllBytes(Path.Combine(_dir, "setup.exe"), [77, 90, 0, 0, 3, 0]);
        Assert.Contains("PDF and Office documents, and pictures", (await Read("setup.exe")).Text);
    }

    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string Pr = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>A minimal Office file: just the parts the reader looks at.</summary>
    private void Package(string name, params (string Entry, string Xml)[] parts)
    {
        using var zip = ZipFile.Open(Path.Combine(_dir, name), ZipArchiveMode.Create);
        foreach (var (entry, xml) in parts)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entry).Open(), new UTF8Encoding(false));
            writer.Write(xml);
        }
    }
}
