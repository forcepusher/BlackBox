using BlackBox.Core.Chats;
using Xunit;

namespace BlackBox.Core.Tests;

// Answers seen in the chat window during runs on qwen3.8, and how they should read there.
public class MarkdownLiteTests
{
    private static string Text(List<MdSpan> line) => string.Concat(line.Select(s => s.Text));

    [Fact]
    public void Bold_loses_its_asterisks()
    {
        var lines = MarkdownLite.Render("It's **10:02 PM on Thursday, October 15, 2026** right now.");

        var line = Assert.Single(lines);
        Assert.Equal("It's 10:02 PM on Thursday, October 15, 2026 right now.", Text(line));
        Assert.Equal(new MdSpan("10:02 PM on Thursday, October 15, 2026", MdStyle.Bold), line[1]);
    }

    [Fact]
    public void A_table_becomes_aligned_monospace_rows_with_a_bold_header()
    {
        var lines = MarkdownLite.Render("""
            | Month | Visitors |
            |-------|----------|
            | Jan | 3,200 |
            | **May** | **5,200** |
            """);

        Assert.Equal(["Month   Visitors", "Jan     3,200", "May     5,200"], lines.Select(Text));
        Assert.All(lines.SelectMany(l => l), s => Assert.True(s.Style.HasFlag(MdStyle.Code)));
        Assert.All(lines[0].Where(s => s.Text.Trim().Length > 0), s => Assert.True(s.Style.HasFlag(MdStyle.Bold)));
        Assert.Contains(lines[2], s => s.Text == "May" && s.Style.HasFlag(MdStyle.Bold));
    }

    [Fact]
    public void Right_aligned_columns_line_up_on_the_right()
    {
        var lines = MarkdownLite.Render("| Shop | August |\n|:--|--:|\n| Riverside | 15,700 |\n| Online | 144,600 |");

        Assert.Equal(["Shop         August", "Riverside    15,700", "Online      144,600"], lines.Select(Text));
    }

    [Fact]
    public void Windows_paths_and_wildcards_keep_every_character()
    {
        const string answer = @"Saved to C:\Repos\BlackBox\.dev\blindspots\work\осень.txt; the *.txt files in C:\Repos\*\notes stay, and 5 * 3 * 2 = 30.";

        Assert.Equal(answer, Text(Assert.Single(MarkdownLite.Render(answer))));
    }

    [Fact]
    public void List_items_get_bullets_and_keep_bold_and_code()
    {
        var lines = MarkdownLite.Render("- **Lowest revenue in August:** Riverside, `$15,700`\n  - nested\n1. **Opened Notepad**");

        Assert.Equal(["• Lowest revenue in August: Riverside, $15,700", "  • nested", "1. Opened Notepad"], lines.Select(Text));
        Assert.Contains(lines[0], s => s is { Text: "Lowest revenue in August:", Style: MdStyle.Bold });
        Assert.Contains(lines[0], s => s is { Text: "$15,700", Style: MdStyle.Code });
        Assert.Contains(lines[2], s => s is { Text: "Opened Notepad", Style: MdStyle.Bold });
    }

    [Fact]
    public void Headings_quotes_rules_and_code_blocks()
    {
        var lines = MarkdownLite.Render("### Summary\n> Осень шагает по земле,\n---\n```powershell\nGet-ChildItem *.txt | Remove-Item\n```\nDone.");

        Assert.Equal(["Summary", "▍ Осень шагает по земле,", "", "Get-ChildItem *.txt | Remove-Item", "Done."], lines.Select(Text));
        Assert.Equal(MdStyle.Heading | MdStyle.Bold, lines[0][0].Style);
        Assert.All(lines[1], s => Assert.True(s.Style.HasFlag(MdStyle.Muted)));
        Assert.Equal(MdStyle.Code, Assert.Single(lines[3]).Style);
    }

    [Fact]
    public void Italic_only_where_it_is_meant_and_links_keep_their_address()
    {
        var lines = MarkdownLite.Render("A *very* big file, a*b*c, see [the docs](https://example.com/docs) or [https://x.dev](https://x.dev).");

        Assert.Equal("A very big file, a*b*c, see the docs (https://example.com/docs) or https://x.dev.", Text(Assert.Single(lines)));
        Assert.Contains(lines[0], s => s is { Text: "very", Style: MdStyle.Italic });
    }

    [Fact]
    public void Unfinished_markup_while_a_reply_streams_in_stays_as_written()
    {
        Assert.Equal("The total is **$2,1", Text(Assert.Single(MarkdownLite.Render("The total is **$2,1"))));
        Assert.Equal("Run `Get-Date", Text(Assert.Single(MarkdownLite.Render("Run `Get-Date"))));
    }
}
