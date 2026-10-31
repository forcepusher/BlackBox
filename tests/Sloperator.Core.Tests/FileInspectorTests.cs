using Sloperator.Core.Documents;
using Xunit;

namespace Sloperator.Core.Tests;

// The File inspector panel: which way it shows a file, and the tables and text it draws itself.
public class FileInspectorTests : IDisposable
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
    [InlineData("notes.md", InspectorKind.Markdown)]
    [InlineData("data.csv", InspectorKind.Table)]
    [InlineData("data.TSV", InspectorKind.Table)]
    [InlineData("report.pdf", InspectorKind.Document)]
    [InlineData("letter.docx", InspectorKind.Document)]
    [InlineData("sheet.xlsx", InspectorKind.Document)]
    [InlineData("deck.pptx", InspectorKind.Document)]
    [InlineData("page.html", InspectorKind.Document)]
    [InlineData("photo.jpg", InspectorKind.Picture)]
    [InlineData("clip.mp4", InspectorKind.Video)]
    [InlineData("loop.gif", InspectorKind.Animation)]
    [InlineData("song.mp3", InspectorKind.Sound)]
    [InlineData("chair.glb", InspectorKind.Model)]
    public void Kinds_by_extension(string name, InspectorKind kind) => Assert.Equal(kind, FileInspector.KindOf(Path.Combine(_dir, name)));

    // Open file's picker lists each kind's extensions: each must open as that kind (text ones, written, as text).
    [Fact]
    public void Picker_types_open_as_their_kind()
    {
        foreach (var (name, kind, extensions) in FileInspector.PickerTypes)
            foreach (var ext in extensions)
            {
                var path = kind == InspectorKind.Text ? Write("file" + ext, "some text\n") : Path.Combine(_dir, "file" + ext);
                Assert.True(kind == FileInspector.KindOf(path), $"{name}: {ext} opens as {FileInspector.KindOf(path)}");
            }
    }

    // A file's right-click menu offers the Code editor for these, told by the name alone (none of them is written).
    [Theory]
    [InlineData("notes.md", true)]
    [InlineData("data.csv", true)]
    [InlineData("page.html", true)]
    [InlineData("icon.svg", true)]
    [InlineData("Program.cs", true)]
    [InlineData("settings.json", true)]
    [InlineData("build.ps1", true)]
    [InlineData("photo.png", false)]
    [InlineData("clip.mp4", false)]
    [InlineData("song.mp3", false)]
    [InlineData("chair.glb", false)]
    [InlineData("report.pdf", false)]
    [InlineData("letter.docx", false)]
    [InlineData("sheet.xlsx", false)]
    [InlineData("letter.rtf", false)]
    public void Text_formats_by_extension(string name, bool text) => Assert.Equal(text, FileInspector.IsText(Path.Combine(_dir, name)));

    [Fact]
    public void A_file_of_no_known_kind_is_text_by_its_start()
    {
        Assert.True(FileInspector.IsText(Write("README", "plain words\n")));
        Assert.True(FileInspector.IsText(Write(".gitignore", "bin/\nobj/\n")));
        var binary = Path.Combine(_dir, "blob.bin");
        File.WriteAllBytes(binary, [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        Assert.False(FileInspector.IsText(binary));
        Assert.False(FileInspector.IsText(Path.Combine(_dir, "gone.bin")));
        Assert.False(FileInspector.IsText(_dir));
        // A share is never read from: its server may be off.
        Assert.False(FileInspector.IsText(@"\\nobody-here\share\README"));
    }

    [Fact]
    public void Other_files_are_text_unless_they_hold_binary()
    {
        Assert.Equal(InspectorKind.Text, FileInspector.KindOf(Write("Program.cs", "class A { }\n")));
        Assert.Equal(InspectorKind.Text, FileInspector.KindOf(Write("README", "plain words\n")));
        var binary = Path.Combine(_dir, "blob.bin");
        File.WriteAllBytes(binary, [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        Assert.Equal(InspectorKind.None, FileInspector.KindOf(binary));
        // UTF-16 has zero bytes between its letters, but none once read in its own encoding.
        var wide = Path.Combine(_dir, "wide.log");
        File.WriteAllText(wide, "hello\n", System.Text.Encoding.Unicode);
        Assert.Equal(InspectorKind.Text, FileInspector.KindOf(wide));
    }

    [Fact]
    public void A_csv_becomes_a_table_with_quoted_fields_kept_whole()
    {
        var (markdown, rows, columns) = FileInspector.Table(Write("t.csv", "name,note\n\"Smith, J\",\"says \"\"hi\"\"\"\nLee,\"two\nlines\"\nPipe,a|b\n"));
        Assert.Equal(3, rows);
        Assert.Equal(2, columns);
        Assert.Equal(
            "| name | note |\n| --- | --- |\n| Smith, J | says \"hi\" |\n| Lee | two lines |\n| Pipe | a\\|b |\n", markdown);
    }

    [Fact]
    public void A_csv_with_semicolons_is_split_at_them()
    {
        var (markdown, rows, columns) = FileInspector.Table(Write("eu.csv", "Name;Preis\nKaffee;2,50\n"));
        Assert.Equal((1, 2), (rows, columns));
        Assert.Contains("| Kaffee | 2,50 |", markdown);
    }

    [Fact]
    public void A_long_table_shows_its_first_rows_and_counts_them_all()
    {
        var text = "n\n" + string.Concat(Enumerable.Range(1, FileInspector.MaxRows + 20).Select(i => $"{i}\n"));
        var (markdown, rows, _) = FileInspector.Table(Write("long.csv", text));
        Assert.Equal(FileInspector.MaxRows + 20, rows);
        Assert.Equal(FileInspector.MaxRows + 2, markdown.Count(c => c == '\n'));
    }

    [Fact]
    public void A_peek_is_the_start_of_the_file_as_the_panel_draws_it()
    {
        var md = FileInspector.Peek(Write("notes.md", "\n\n# Title\n" + string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i}\n"))), InspectorKind.Markdown);
        Assert.Null(md.Text);
        Assert.StartsWith("# Title\nline 1\n", md.Markdown);
        Assert.Equal(FileInspector.PeekLines, md.Markdown!.Split('\n').Length);

        var table = FileInspector.Peek(Write("t.csv", "a,b\n" + string.Concat(Enumerable.Range(1, 30).Select(i => $"{i},x\n"))), InspectorKind.Table);
        Assert.StartsWith("| a | b |\n| --- | --- |\n| 1 | x |", table.Markdown);
        Assert.Equal(1 + 1 + 8, table.Markdown!.TrimEnd('\n').Split('\n').Length);

        var code = FileInspector.Peek(Write("Program.cs", "class A\r\n{\r\n}\r\n"), InspectorKind.Text);
        Assert.Equal((null, "class A\n{\n}"), code);

        Assert.Equal((null, null), FileInspector.Peek(Write("blank.txt", "\n  \n"), InspectorKind.Text));
    }

    [Fact]
    public void A_web_page_peeks_as_its_text()
    {
        var (markdown, text) = FileInspector.Peek(Write("page.html", "<html><head><title>Prices</title><script>var x=1;</script></head><body><h1>Prices</h1><p>Coffee costs 2.50</p></body></html>"), InspectorKind.Document);
        Assert.Null(text);
        Assert.Equal("# Prices\n\nCoffee costs 2.50", markdown);

        Assert.Equal(("Only a title", null), FileInspector.Peek(Write("empty.html", "<html><head><title>Only a title</title></head><body></body></html>"), InspectorKind.Document));
    }

    [Fact]
    public void An_excel_peek_is_its_first_sheet_as_a_table()
    {
        var text = "# Sheet \"Budget\" (B2:D4)\nB2 Category | C2 Planned | D2 Difference\nB3 Rent | C3 $1,200.00 | D3 =C3-B9 → $0.00\nB4 Fun | D4 =C4-B9 → \n(bold: B2, C2)\n(1 chart)\n# Sheet \"Other\"\nA1 Elsewhere\n";
        Assert.Equal("| Category | Planned | Difference |\n| --- | --- | --- |\n| Rent | $1,200.00 | $0.00 |\n| Fun |  |  |\n", FileInspector.SheetTable(text));
        Assert.Null(FileInspector.SheetTable("# Sheet \"Empty\"\n"));
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
        Assert.Equal(png, FileInspector.SavedThumbnail(with));

        var without = Path.Combine(_dir, "letter.docx");
        using (var zip = System.IO.Compression.ZipFile.Open(without, System.IO.Compression.ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("word/document.xml").Open()) s.WriteByte(0);
        Assert.Null(FileInspector.SavedThumbnail(without));

        Assert.Null(FileInspector.SavedThumbnail(Write("broken.xlsx", "not a zip")));
        Assert.Null(FileInspector.SavedThumbnail(Write("notes.md", "# hi")));
    }
}

// PDF pages drawn for the panel, by Windows' own renderer.
public class PdfPagesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-pdfpages-").FullName;

#if WINDOWS
    public PdfPagesTests() => PdfPages.Opener ??= Sloperator.Platform.Windows.WindowsPdf.Open;
#endif

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
    public void An_area_of_a_page_is_drawn_alone_and_sharp()
    {
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var page = builder.AddPage(200, 100);
        // Black on the left half (PDF's y counts from the bottom).
        page.DrawRectangle(new UglyToad.PdfPig.Core.PdfPoint(0, 0), 100, 100, 1, fill: true);
        var path = Path.Combine(_dir, "area.pdf");
        File.WriteAllBytes(path, builder.Build());

        using var pdf = PdfPages.Open(path);
        // The middle of the page across the black's edge, 40% of its width and half its height.
        var area = new DocumentArea(0.3, 0.25, 0.7, 0.75, 0);
        var (bgra, width, height) = pdf.RenderArea(0, area, 400, 200)!.Value;
        // 80x50 points at 400 pixels along the longer side: 5 pixels a point.
        Assert.Equal((400, 250), (width, height));
        Assert.True(bgra[(125 * width + 50) * 4] < 40); // left of the edge: black
        Assert.Equal(255, bgra[(125 * width + 350) * 4]); // right of it: white
        // Never less sharp than the panel draws the page: 1400 wide is 7 pixels a point.
        Assert.Equal(560, pdf.RenderArea(0, area, 100, 1400)!.Value.Width);
        Assert.Null(pdf.RenderArea(1, area, 400, 1400));
    }

    [Fact]
    public void A_file_that_is_not_a_PDF_says_so()
    {
        var path = Path.Combine(_dir, "fake.pdf");
        File.WriteAllText(path, "not a pdf");
        Assert.Throws<InvalidDataException>(() => PdfPages.Open(path));
    }
}
