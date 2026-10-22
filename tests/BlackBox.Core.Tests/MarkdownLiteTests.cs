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
}
