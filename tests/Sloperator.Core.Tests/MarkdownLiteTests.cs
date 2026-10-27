using Sloperator.Core.Chats;
using Xunit;

namespace Sloperator.Core.Tests;

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
        const string answer = @"Saved to C:\Repos\Sloperator\.dev\blindspots\work\осень.txt; the *.txt files in C:\Repos\*\notes stay, and 5 * 3 * 2 = 30.";

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
    public void Italic_only_where_it_is_meant_and_links_open_their_page()
    {
        var lines = MarkdownLite.Render("A *very* big file, a*b*c, see [the docs](https://example.com/docs) or [https://x.dev](https://x.dev).");

        Assert.Equal("A very big file, a*b*c, see the docs or https://x.dev.", Text(Assert.Single(lines)));
        Assert.Contains(lines[0], s => s is { Text: "very", Style: MdStyle.Italic });
        Assert.Contains(new MdSpan("the docs", MdStyle.None, "https://example.com/docs"), lines[0]);
        Assert.Contains(new MdSpan("https://x.dev", MdStyle.None, "https://x.dev"), lines[0]);
        Assert.Equal(".", lines[0][^1].Text);
    }

    [Fact]
    public void A_link_to_a_file_keeps_its_target_readable_as_it_cannot_be_opened()
    {
        var line = Assert.Single(MarkdownLite.Render("Saved as [report](C:\\Users\\me\\report.docx)."));

        Assert.Equal("Saved as report (C:\\Users\\me\\report.docx).", Text(line));
        Assert.All(line, s => Assert.Null(s.Url));
    }

    [Fact]
    public void Addresses_written_out_become_links_without_the_punctuation_after_them()
    {
        // As qwen3.8 and Claude wrote them in replies on the FluffyMeow migration.
        var line = Assert.Single(MarkdownLite.Render(
            "Admin console: https://pushmeow.kindzadzagames.com/admin, (see https://en.wikipedia.org/wiki/Let%27s_Encrypt_(CA)) or www.hostkey.com."));

        var links = line.Where(s => s.Url != null).ToList();
        Assert.Equal(["https://pushmeow.kindzadzagames.com/admin", "https://en.wikipedia.org/wiki/Let%27s_Encrypt_(CA)", "www.hostkey.com"], links.Select(s => s.Text));
        Assert.Equal("https://www.hostkey.com", links[2].Url);
        Assert.Equal("Admin console: https://pushmeow.kindzadzagames.com/admin, (see https://en.wikipedia.org/wiki/Let%27s_Encrypt_(CA)) or www.hostkey.com.", Text(line));
    }

    [Fact]
    public void A_link_whose_address_holds_parentheses_opens_all_of_it()
    {
        var line = Assert.Single(MarkdownLite.Render("See [Python](https://en.wikipedia.org/wiki/Python_(programming_language)) and [the API](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ms123401(v=vs.85))."));

        Assert.Equal("See Python and the API.", Text(line));
        Assert.Equal(["https://en.wikipedia.org/wiki/Python_(programming_language)", "https://learn.microsoft.com/en-us/previous-versions/windows/desktop/ms123401(v=vs.85)"],
            line.Where(s => s.Url != null).Select(s => s.Url));
    }

    [Theory]
    [InlineData("详见 https://example.com/docs。", "https://example.com/docs")]
    [InlineData("（https://x.com）", "https://x.com")]
    [InlineData("访问https://example.com/zh，谢谢", "https://example.com/zh")]
    [InlineData("the page https://example.com/a-long-address…", "https://example.com/a-long-address")]
    [InlineData("“https://example.com/quoted”", "https://example.com/quoted")]
    public void An_address_ends_before_chinese_and_japanese_punctuation(string text, string url) =>
        Assert.Equal(url, Assert.Single(Assert.Single(MarkdownLite.Render(text)), s => s.Url != null).Url);

    [Fact]
    public void Addresses_in_code_bold_and_plain_text_are_links_too()
    {
        var lines = MarkdownLite.Render("Check **https://x.dev/health** with `curl http://127.0.0.1:8080/health`.\n```\nwget https://example.com/a.tar.gz\n```");

        Assert.Contains(new MdSpan("https://x.dev/health", MdStyle.Bold, "https://x.dev/health"), lines[0]);
        Assert.Contains(new MdSpan("http://127.0.0.1:8080/health", MdStyle.Code, "http://127.0.0.1:8080/health"), lines[0]);
        Assert.Contains(new MdSpan("https://example.com/a.tar.gz", MdStyle.Code, "https://example.com/a.tar.gz"), lines[1]);

        var plain = MarkdownLite.Plain("my repo: https://github.com/Cromwill/FluffyMeow\nthanks **no markdown here**", MdStyle.Muted);
        Assert.Equal(2, plain.Count);
        Assert.Equal(new MdSpan("https://github.com/Cromwill/FluffyMeow", MdStyle.Muted, "https://github.com/Cromwill/FluffyMeow"), plain[0][1]);
        Assert.Equal("thanks **no markdown here**", Text(plain[1]));
    }

    [Theory]
    [InlineData("one\r\ntwo\r\n\r\nthree")]
    [InlineData("one\r\ntwo\r\n\nthree")]
    [InlineData("one\ntwo\n\nthree")]
    [InlineData("one\rtwo\r\rthree")]
    public void Every_line_and_empty_line_of_a_message_is_a_line_shown(string text)
    {
        // A user's message keeps its empty lines, whichever line breaks it was typed or pasted with.
        Assert.Equal(["one", "two", "", "three"], MarkdownLite.Plain(text).Select(Text));
        Assert.Equal(["one", "two", "", "three"], MarkdownLite.Render(text).Select(Text));
    }

    [Fact]
    public void Text_is_cut_at_its_tables_for_grids()
    {
        var parts = MarkdownLite.Parts("## Other options\n| Option | Verdict |\n|---|---|\n| **WSL2** | Fastest, on `localhost`. |\n\nAfter.\n|---|");

        Assert.Equal(["Other options", "| Option | Verdict |\n|---|---|\n| **WSL2** | Fastest, on `localhost`. |", "After.\n|---|"], parts.Select(p => p.Text));
        Assert.Equal(MdKind.Heading, parts[0].Kind);
        Assert.Null(parts[2].Table);
        var table = parts[1].Table!;
        Assert.Equal(1, table.HeaderRows);
        Assert.Equal(["Option", "Verdict"], table.Rows[0].Select(Text));
        Assert.All(table.Rows[0].SelectMany(c => c), s => Assert.Equal(MdStyle.Bold, s.Style));
        // The chat's font: only inline code is monospace.
        Assert.Equal([new MdSpan("WSL2", MdStyle.Bold)], table.Rows[1][0]);
        Assert.Equal([new MdSpan("Fastest, on ", MdStyle.None), new MdSpan("localhost", MdStyle.Code), new MdSpan(".", MdStyle.None)], table.Rows[1][1]);
    }

    [Fact]
    public void A_table_grid_fills_out_ragged_rows_and_takes_one_dash_alignments()
    {
        var table = MarkdownLite.Parts("| Shop | August | Note |\n|:--|--:|:-:|\n| Riverside | 15,700 |\n| Online | 144,600 | best | extra |").Single().Table!;

        Assert.Equal([-1, 1, 0, -1], table.Align);
        Assert.Equal(3, table.Rows.Count);
        Assert.All(table.Rows, r => Assert.Equal(4, r.Count));
        Assert.Equal(["Riverside", "15,700", "", ""], table.Rows[1].Select(Text));
        // No |---| line: no header; "| - |" placeholders are cells.
        var plain = MarkdownLite.Parts("| a | - |\n| - | d |").Single().Table!;
        Assert.Equal(0, plain.HeaderRows);
        Assert.Equal(["-", "d"], plain.Rows[1].Select(Text));
    }

    [Fact]
    public void A_reply_is_cut_into_the_parts_a_page_shows()
    {
        var parts = MarkdownLite.Parts("""
            # Title
            Some text
            on two lines.

            3. third, a long item
               that wraps on.
            4. fourth
               - nested
               - [ ] to do
               - [x] done

            > A quote
            > going on.
            > > nested
            > - a list in it

            > [!WARNING]
            > Careful.

            ---
            Footnote[^1].

            [^1]: The note.
            """);

        Assert.Equal([
            new MdPart("Title", Kind: MdKind.Heading, Level: 1),
            new MdPart("Some text\non two lines."),
            new MdPart("third, a long item\nthat wraps on.", Kind: MdKind.Item, Marker: "3.", Spaced: true),
            new MdPart("fourth", Kind: MdKind.Item, Marker: "4."),
            new MdPart("nested", Kind: MdKind.Item, Level: 1, Marker: "•"),
            new MdPart("to do", Kind: MdKind.Item, Level: 1, Marker: "☐"),
            new MdPart("done", Kind: MdKind.Item, Level: 1, Marker: "☑"),
            new MdPart("A quote\ngoing on.", Kind: MdKind.Quote, Level: 1, Spaced: true),
            new MdPart("nested", Kind: MdKind.Quote, Level: 2),
            new MdPart("a list in it", Kind: MdKind.Quote, Level: 1, Marker: "•"),
            new MdPart("**Warning**\nCareful.", Kind: MdKind.Quote, Level: 1, Spaced: true),
            new MdPart("", Kind: MdKind.Rule, Spaced: true),
            new MdPart("Footnote[^1]."),
            new MdPart("The note.", Kind: MdKind.Note, Marker: "[1]", Spaced: true),
        ], parts);
    }

    [Fact]
    public void A_paragraph_indented_under_a_list_item_goes_in_with_its_text()
    {
        // The text after a code block in a list item comes as a block of its own, still indented.
        Assert.Equal([new MdPart("Windows asks to restart.", Level: 1)], MarkdownLite.Parts("   Windows asks to restart."));
        Assert.Equal(3, MarkdownLite.Blocks("1. Run:\n\n   ```powershell\n   Enable-Feature\n   ```\n\n   Then restart.")[1].Indent);
    }

    [Fact]
    public void Inline_markup_models_write_reads_as_on_GitHub()
    {
        Assert.Equal([new MdSpan("italic ", MdStyle.Italic), new MdSpan("bold", MdStyle.Italic | MdStyle.Bold), new MdSpan(" italic", MdStyle.Italic)],
            MarkdownLite.Inline("*italic **bold** italic*", MdStyle.None));
        Assert.Equal([new MdSpan("both", MdStyle.Bold | MdStyle.Italic)], MarkdownLite.Inline("***both***", MdStyle.None));
        Assert.Equal([new MdSpan("old", MdStyle.Strike), new MdSpan(" new", MdStyle.None)], MarkdownLite.Inline("~~old~~ new", MdStyle.None));
        Assert.Equal([new MdSpan("bold", MdStyle.Bold), new MdSpan(" and ", MdStyle.None), new MdSpan("italic", MdStyle.Italic)],
            MarkdownLite.Inline("__bold__ and _italic_", MdStyle.None));
        // Underscores inside words and paths stay.
        Assert.Equal("snake_case_name and my_file_v2.txt", Text(MarkdownLite.Inline("snake_case_name and my_file_v2.txt", MdStyle.None)));
        Assert.Equal([new MdSpan("see ", MdStyle.None), new MdSpan("https://x.dev/a", MdStyle.None, "https://x.dev/a")],
            MarkdownLite.Inline("see <https://x.dev/a>", MdStyle.None));
        Assert.Equal("one\ntwo, bold, a <b and List<int> & AT&T", Text(MarkdownLite.Inline("one<br>two, <b>bold</b>, a <b and List<int> &amp; AT&T", MdStyle.None)));
        Assert.Contains(new MdSpan("pic", MdStyle.None, "https://x.dev/p.png"), MarkdownLite.Inline("![pic](https://x.dev/p.png)", MdStyle.None));
        Assert.Equal("Footnote[1].", Text(MarkdownLite.Inline("Footnote[^1].", MdStyle.None)));
    }

    [Fact]
    public void A_line_of_nothing_but_table_dashes_stays_as_written()
    {
        // A scale drawn in text, or a table that opens with its |---| line, as it streams in:
        // measuring a table with no rows threw.
        Assert.Equal(["|---|---|"], MarkdownLite.Render("|---|---|").Select(Text));
        Assert.Equal(["scale", "|----|----|", "0    5    10"], MarkdownLite.Render("scale\n|----|----|\n0    5    10").Select(Text));
    }

    [Fact]
    public void Unfinished_markup_while_a_reply_streams_in_stays_as_written()
    {
        Assert.Equal("The total is **$2,1", Text(Assert.Single(MarkdownLite.Render("The total is **$2,1"))));
        Assert.Equal("Run `Get-Date", Text(Assert.Single(MarkdownLite.Render("Run `Get-Date"))));
    }

    [Fact]
    public void Code_blocks_are_cut_out_of_the_text_with_their_language()
    {
        var blocks = MarkdownLite.Blocks("Run this:\n\n```powershell\nGet-ChildItem C:\\Repos |\n    Sort-Object Length\n```\n\nThen:\n```\nexit\n```");

        Assert.Equal(
            [new MdBlock("Run this:", false), new MdBlock("Get-ChildItem C:\\Repos |\n    Sort-Object Length", true, "powershell"),
             new MdBlock("Then:", false), new MdBlock("exit", true)],
            blocks);
        Assert.Equal([new MdBlock("No code here, `inline` stays.", false)], MarkdownLite.Blocks("No code here, `inline` stays."));
    }

    [Fact]
    public void A_code_block_still_streaming_in_runs_to_the_end()
    {
        Assert.Equal([new MdBlock("Like this:", false), new MdBlock("", true, "pow")], MarkdownLite.Blocks("Like this:\n```pow"));
        Assert.Equal([new MdBlock("Like this:", false), new MdBlock("dotnet publish\n", true, "bash")], MarkdownLite.Blocks("Like this:\n```bash\ndotnet publish\n"));
    }

    [Fact]
    public void A_fence_in_a_list_loses_its_indent_and_only_a_bare_fence_closes_a_block()
    {
        var blocks = MarkdownLite.Blocks("1. Write:\n   ```markdown\n   # Title\n   ```js\n     indented\n   ```\n2. Save it.");

        Assert.Equal(
            [new MdBlock("1. Write:", false), new MdBlock("# Title\n```js\n  indented", true, "markdown", 3), new MdBlock("2. Save it.", false)],
            blocks);
    }
}
