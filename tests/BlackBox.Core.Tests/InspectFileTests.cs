using BlackBox.Core.Documents;
using Xunit;

namespace BlackBox.Core.Tests;

// The Inspect file panel: which way it shows a file, and the tables and text it draws itself.
public class InspectFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-inspect-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("notes.md", InspectKind.Markdown)]
    [InlineData("data.csv", InspectKind.Table)]
    [InlineData("data.TSV", InspectKind.Table)]
    [InlineData("report.pdf", InspectKind.Document)]
    [InlineData("letter.docx", InspectKind.Document)]
    [InlineData("sheet.xlsx", InspectKind.Document)]
    [InlineData("deck.pptx", InspectKind.Document)]
    [InlineData("page.html", InspectKind.Document)]
    [InlineData("photo.jpg", InspectKind.Picture)]
    [InlineData("clip.mp4", InspectKind.Media)]
    [InlineData("loop.gif", InspectKind.Media)]
    public void Kinds_by_extension(string name, InspectKind kind) => Assert.Equal(kind, InspectFile.KindOf(Path.Combine(_dir, name)));

    [Fact]
    public void Other_files_are_text_unless_they_hold_binary()
    {
        Assert.Equal(InspectKind.Text, InspectFile.KindOf(Write("Program.cs", "class A { }\n")));
        Assert.Equal(InspectKind.Text, InspectFile.KindOf(Write("README", "plain words\n")));
        var binary = Path.Combine(_dir, "blob.bin");
        File.WriteAllBytes(binary, [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        Assert.Equal(InspectKind.None, InspectFile.KindOf(binary));
        // UTF-16 has zero bytes between its letters, but none once read in its own encoding.
        var wide = Path.Combine(_dir, "wide.log");
        File.WriteAllText(wide, "hello\n", System.Text.Encoding.Unicode);
        Assert.Equal(InspectKind.Text, InspectFile.KindOf(wide));
    }

    [Fact]
    public void A_csv_becomes_a_table_with_quoted_fields_kept_whole()
    {
        var (markdown, rows, columns) = InspectFile.Table(Write("t.csv", "name,note\n\"Smith, J\",\"says \"\"hi\"\"\"\nLee,\"two\nlines\"\nPipe,a|b\n"));
        Assert.Equal(3, rows);
        Assert.Equal(2, columns);
        Assert.Equal(
            "| name | note |\n| --- | --- |\n| Smith, J | says \"hi\" |\n| Lee | two lines |\n| Pipe | a\\|b |\n", markdown);
    }

    [Fact]
    public void A_csv_with_semicolons_is_split_at_them()
    {
        var (markdown, rows, columns) = InspectFile.Table(Write("eu.csv", "Name;Preis\nKaffee;2,50\n"));
        Assert.Equal((1, 2), (rows, columns));
        Assert.Contains("| Kaffee | 2,50 |", markdown);
    }

    [Fact]
    public void A_long_table_shows_its_first_rows_and_counts_them_all()
    {
        var text = "n\n" + string.Concat(Enumerable.Range(1, InspectFile.MaxRows + 20).Select(i => $"{i}\n"));
        var (markdown, rows, _) = InspectFile.Table(Write("long.csv", text));
        Assert.Equal(InspectFile.MaxRows + 20, rows);
        Assert.Equal(InspectFile.MaxRows + 2, markdown.Count(c => c == '\n'));
    }

    [Fact]
    public void A_peek_is_the_start_of_the_file_as_the_panel_draws_it()
    {
        var md = InspectFile.Peek(Write("notes.md", "\n\n# Title\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i}\n"))), InspectKind.Markdown);
        Assert.Null(md.Text);
        Assert.StartsWith("# Title\nline 1\n", md.Markdown);
        Assert.Equal(InspectFile.PeekLines, md.Markdown!.Split('\n').Length);

        var table = InspectFile.Peek(Write("t.csv", "a,b\n" + string.Concat(Enumerable.Range(1, 30).Select(i => $"{i},x\n"))), InspectKind.Table);
        Assert.StartsWith("| a | b |\n| --- | --- |\n| 1 | x |", table.Markdown);
        Assert.Equal(1 + 1 + 8, table.Markdown!.TrimEnd('\n').Split('\n').Length);

        var code = InspectFile.Peek(Write("Program.cs", "class A\r\n{\r\n}\r\n"), InspectKind.Text);
        Assert.Equal((null, "class A\n{\n}"), code);

        Assert.Equal((null, null), InspectFile.Peek(Write("blank.txt", "\n  \n"), InspectKind.Text));
    }

    [Fact]
    public void A_web_page_peeks_as_its_text()
    {
        var (markdown, text) = InspectFile.Peek(Write("page.html", "<html><head><title>Prices</title><script>var x=1;</script></head><body><h1>Prices</h1><p>Coffee costs 2.50</p></body></html>"), InspectKind.Document);
        Assert.Null(text);
        Assert.Equal("# Prices\n\nCoffee costs 2.50", markdown);

        Assert.Equal(("Only a title", null), InspectFile.Peek(Write("empty.html", "<html><head><title>Only a title</title></head><body></body></html>"), InspectKind.Document));
    }

    [Fact]
    public void An_excel_peek_is_its_first_sheet_as_a_table()
    {
        var text = "# Sheet \"Budget\" (B2:D4)\nB2 Category | C2 Planned | D2 Difference\nB3 Rent | C3 $1,200.00 | D3 =C3-B9 → $0.00\nB4 Fun | D4 =C4-B9 → \n(bold: B2, C2)\n(1 chart)\n# Sheet \"Other\"\nA1 Elsewhere\n";
        Assert.Equal("| Category | Planned | Difference |\n| --- | --- | --- |\n| Rent | $1,200.00 | $0.00 |\n| Fun |  |  |\n", InspectFile.SheetTable(text));
        Assert.Null(InspectFile.SheetTable("# Sheet \"Empty\"\n"));
    }

    [Fact]
    public void An_office_file_gives_the_picture_it_keeps_of_itself()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 };
        var with = Path.Combine(_dir, "deck.pptx");
        using (var zip = System.IO.Compression.ZipFile.Open(with, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = zip.CreateEntry("ppt/presentation.xml").Open()) s.WriteByte(0);
            using (var s = zip.CreateEntry("docProps/thumbnail.jpeg").Open()) s.Write(png);
        }
        Assert.Equal(png, InspectFile.SavedThumbnail(with));

        var without = Path.Combine(_dir, "letter.docx");
        using (var zip = System.IO.Compression.ZipFile.Open(without, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("word/document.xml").Open()) s.WriteByte(0);
        Assert.Null(InspectFile.SavedThumbnail(without));

        Assert.Null(InspectFile.SavedThumbnail(Write("broken.xlsx", "not a zip")));
        Assert.Null(InspectFile.SavedThumbnail(Write("notes.md", "# hi")));
    }
}

// PDF pages drawn by PDFium for the panel.
public class PdfPagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-pdfpages-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Pages_are_counted_sized_and_drawn()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var first = builder.AddPage(200, 100);
        first.DrawRectangle(new UglyToad.PdfPig.Core.PdfPoint(0, 0), 100, 100, 1, fill: true);
        builder.AddPage(300, 600);
        var path = Path.Combine(_dir, "two pages ü.pdf");
        File.WriteAllBytes(path, builder.Build());

        using var pdf = PdfPages.Open(path);
        Assert.Equal([(200.0, 100.0), (300.0, 600.0)], pdf.Sizes);
        var (bgra, width, height) = pdf.Render(0, 400)!.Value;
        Assert.Equal((400, 200), (width, height));
        // The rectangle fills the left half black; the right half stays white.
        Assert.True(bgra[(100 * width + 50) * 4] < 40);
        Assert.Equal(255, bgra[(100 * width + 350) * 4]);
        Assert.Null(pdf.Render(2, 400));
    }

    [Fact]
    public void A_file_that_is_not_a_PDF_says_so()
    {
        var path = Path.Combine(_dir, "fake.pdf");
        File.WriteAllText(path, "not a pdf");
        Assert.Throws<InvalidDataException>(() => PdfPages.Open(path));
    }
}
