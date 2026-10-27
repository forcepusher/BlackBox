using Sloperator.Core.Chats;
using Sloperator.Core.Documents;
using Xunit;

namespace Sloperator.Core.Tests;

// Text selected in the File inspector panel: where it is in the file, for the chat's box and the model.
public class DocumentPickTests
{
    [Fact]
    public void A_selection_in_a_text_file_is_its_lines()
    {
        var text = "one\ntwo\nthree\nfour";

        var pick = DocumentPick.InText(text, text.IndexOf("wo"), text.IndexOf("hr") + 2);

        Assert.Equal(new DocumentPick("wo\nthr", "lines 2-3"), pick);
        // A selection of a line with its break doesn't reach the next line.
        Assert.Equal("line 2", DocumentPick.InText(text, 4, 8)!.Where);
        Assert.Null(DocumentPick.InText(text, 5, 5));
    }

    [Fact]
    public void Blocks_say_which_line_they_start_on()
    {
        MarkdownLite.Blocks("\n# Title\n\nSome text.\n\n```cs\nvar x = 1;\n```\n\nThe end.", out var starts);

        Assert.Equal([1, 6, 9], starts);
    }

    [Fact]
    public void A_selection_in_a_Markdown_file_is_its_lines_there()
    {
        var markdown = "# Notes\n\nIntro line.\n\n- **first** point\n- second point\n\n```cs\nvar a = 1;\nvar b = 2;\n```\n\n| A | B |\n|---|---|\n| 1 | 2 |";

        // The list in the first block: shown without its marks, "• first point".
        var blocks = MarkdownLite.Blocks(markdown);
        var shown = string.Join("\n", MarkdownLite.Render(blocks[0].Text).Select(l => string.Concat(l.Select(s => s.Text))));
        var at = shown.IndexOf("first", StringComparison.Ordinal);
        var pick = DocumentPick.InMarkdown(markdown, 0, at, shown.IndexOf("second", StringComparison.Ordinal) + 3, table: false);
        Assert.Equal("lines 5-6", pick!.Where);
        Assert.StartsWith("first point", pick.Text);

        // The code block's second line is line 10 of the file.
        Assert.Equal(new DocumentPick("var b", "line 10"), DocumentPick.InMarkdown(markdown, 1, 11, 16, table: false));

        // A table's row past its |---| line is the line it is on.
        var table = string.Join("\n", MarkdownLite.Render(blocks[2].Text).Select(l => string.Concat(l.Select(s => s.Text))));
        Assert.Equal("line 15", DocumentPick.InMarkdown(markdown, 2, table.LastIndexOf('1'), table.LastIndexOf('1') + 1, table: false)!.Where);
    }

    [Fact]
    public void A_selection_in_a_CSV_is_its_cells_or_rows_as_a_spreadsheet_names_them()
    {
        var markdown = "| Name | Age | City |\n| --- | --- | --- |\n| Ann | 31 | Oslo |\n| Bob | 4 | Rome |\n| Cy | 77 | Lima |";
        var shown = MarkdownLite.Render(markdown).Select(l => string.Concat(l.Select(s => s.Text))).ToList();
        var all = string.Join("\n", shown);
        int At(string text) => all.IndexOf(text, StringComparison.Ordinal);

        // One cell of the third row (Bob's): B3.
        var cell = DocumentPick.InMarkdown(markdown, 0, At("4 "), At("4 ") + 1, table: true);
        Assert.Equal(("4", "cell B3"), (cell!.Text, cell.Where));
        Assert.Equal(["Age"], cell.Columns);

        // From Ann's age to her city.
        var cells = DocumentPick.InMarkdown(markdown, 0, At("31"), At("Oslo") + 4, table: true)!;
        Assert.Equal("cells B2:C2", cells.Where);
        Assert.Equal(["Age", "City"], cells.Columns);

        // A whole row, and rows: every column named.
        var row = DocumentPick.InMarkdown(markdown, 0, At("Cy"), all.Length, table: true)!;
        Assert.Equal("row 4", row.Where);
        var rows = DocumentPick.InMarkdown(markdown, 0, At("Bob"), At("Lima"), table: true)!;
        Assert.Equal("rows 3-4", rows.Where);
        Assert.Equal(["Name", "Age", "City"], rows.Columns);

        // The header is row 1, and names no columns of itself.
        var header = DocumentPick.InMarkdown(markdown, 0, 0, At("Ann") + 1, table: true)!;
        Assert.Equal("rows 1-2", header.Where);
        Assert.Null(header.Columns);
    }

    [Fact]
    public void Columns_past_Z_are_named_as_a_spreadsheet_does()
    {
        Assert.Equal(["A", "Z", "AA", "AZ", "BA"], new[] { 0, 25, 26, 51, 52 }.Select(DocumentPick.Letters));
    }
}
