using Sloperator.App.ViewModels;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The line a "Notify me when done" notification gives of the run's last reply.</summary>
public sealed class NotifyLineTests
{
    [Fact]
    public void The_first_paragraph_goes_without_its_markdown()
    {
        Assert.Equal("I wrote the poem into Notepad and saved it as poem.txt on your desktop.",
            MainWindowViewModel.FirstLine("I wrote the poem into **Notepad** and saved it as `poem.txt` on your desktop.\n\nWant me to read it back?"));
        Assert.Equal("Done: the report", MainWindowViewModel.FirstLine("\n## Done: the [report](C:/out/report.md)\n\n- one"));
        Assert.Equal("First point", MainWindowViewModel.FirstLine("```\ncode\n```\n- First point"));
    }

    [Fact]
    public void A_long_line_is_cut_at_a_word()
    {
        var line = MainWindowViewModel.FirstLine(string.Join(' ', Enumerable.Repeat("word", 60)), max: 30);
        Assert.Equal("word word word word word word…", line);
    }

    [Fact]
    public void A_reply_of_nothing_but_marks_says_done()
    {
        Assert.Equal("Done.", MainWindowViewModel.FirstLine("**"));
        Assert.Equal("Done.", MainWindowViewModel.FirstLine("   \n  "));
    }
}
