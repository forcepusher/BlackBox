using Sloperator.App.ViewModels;
using Sloperator.Core.Diffs;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>A row of File changes' diff: what a hunk's divider says in place of git's @@ line.</summary>
public sealed class DiffRowTests
{
    private static string Divider(string text)
    {
        var line = new DiffLine(DiffLineKind.Hunk, null, null, text);
        var item = new DiffFileItem(new DiffFile("a.py", "modified", [line]), 0, "C:\\a.py");
        return new DiffRow(item, line).Divider;
    }

    [Theory]
    [InlineData("@@ -96,8 +96,9 @@", "Lines 96-104")]
    [InlineData("@@ -5 +5 @@", "Line 5")]
    [InlineData("@@ -0,0 +1,140 @@", "Lines 1-140")]
    [InlineData("@@ -1,140 +0,0 @@", "Removed lines 1-140")]
    [InlineData("@@ -12,3 +11,0 @@", "Removed lines 12-14")]
    [InlineData("@@ -40,6 +40,7 @@ def run():", "Lines 40-46 · def run():")]
    public void A_hunk_says_which_lines_follow(string hunk, string divider) => Assert.Equal(divider, Divider(hunk));

    [Fact]
    public void A_note_is_shown_as_it_is()
    {
        var line = new DiffLine(DiffLineKind.Note, null, null, "No newline at end of file");
        var item = new DiffFileItem(new DiffFile("a.py", "modified", [line]), 0, "C:\\a.py");
        Assert.Equal("No newline at end of file", new DiffRow(item, line).Divider);
    }
}
