using BlackBox.Core.Documents;
using Xunit;

namespace BlackBox.Core.Tests;

// The Document preview panel: which way it shows a file, and the tables and text it draws itself.
public class DocumentPreviewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-docpreview-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    [Theory]
    [InlineData("notes.md", DocumentKind.Markdown)]
    [InlineData("data.csv", DocumentKind.Table)]
    [InlineData("data.TSV", DocumentKind.Table)]
    [InlineData("report.pdf", DocumentKind.Document)]
    [InlineData("letter.docx", DocumentKind.Document)]
    [InlineData("sheet.xlsx", DocumentKind.Document)]
    [InlineData("deck.pptx", DocumentKind.Document)]
    [InlineData("page.html", DocumentKind.Document)]
    [InlineData("photo.jpg", DocumentKind.Picture)]
    [InlineData("clip.mp4", DocumentKind.Media)]
    [InlineData("loop.gif", DocumentKind.Media)]
    public void Kinds_by_extension(string name, DocumentKind kind) => Assert.Equal(kind, DocumentPreview.KindOf(Path.Combine(_dir, name)));

    [Fact]
    public void Other_files_are_text_unless_they_hold_binary()
    {
        Assert.Equal(DocumentKind.Text, DocumentPreview.KindOf(Write("Program.cs", "class A { }\n")));
        Assert.Equal(DocumentKind.Text, DocumentPreview.KindOf(Write("README", "plain words\n")));
        var binary = Path.Combine(_dir, "blob.bin");
        File.WriteAllBytes(binary, [0x4D, 0x5A, 0x00, 0x01, 0x02]);
        Assert.Equal(DocumentKind.None, DocumentPreview.KindOf(binary));
        // UTF-16 has zero bytes between its letters, but none once read in its own encoding.
        var wide = Path.Combine(_dir, "wide.log");
        File.WriteAllText(wide, "hello\n", System.Text.Encoding.Unicode);
        Assert.Equal(DocumentKind.Text, DocumentPreview.KindOf(wide));
    }

    [Fact]
    public void A_csv_becomes_a_table_with_quoted_fields_kept_whole()
    {
        var (markdown, rows, columns) = DocumentPreview.Table(Write("t.csv", "name,note\n\"Smith, J\",\"says \"\"hi\"\"\"\nLee,\"two\nlines\"\nPipe,a|b\n"));
        Assert.Equal(3, rows);
        Assert.Equal(2, columns);
        Assert.Equal(
            "| name | note |\n| --- | --- |\n| Smith, J | says \"hi\" |\n| Lee | two lines |\n| Pipe | a\\|b |\n", markdown);
    }

    [Fact]
    public void A_csv_with_semicolons_is_split_at_them()
    {
        var (markdown, rows, columns) = DocumentPreview.Table(Write("eu.csv", "Name;Preis\nKaffee;2,50\n"));
        Assert.Equal((1, 2), (rows, columns));
        Assert.Contains("| Kaffee | 2,50 |", markdown);
    }

    [Fact]
    public void A_long_table_shows_its_first_rows_and_counts_them_all()
    {
        var text = "n\n" + string.Concat(Enumerable.Range(1, DocumentPreview.MaxRows + 20).Select(i => $"{i}\n"));
        var (markdown, rows, _) = DocumentPreview.Table(Write("long.csv", text));
        Assert.Equal(DocumentPreview.MaxRows + 20, rows);
        Assert.Equal(DocumentPreview.MaxRows + 2, markdown.Count(c => c == '\n'));
    }
}
