using Sloperator.App.ViewModels;
using Sloperator.Core.Diffs;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// File changes' Watch: what the agent changed since the panel last read the files, which it scrolls
/// to and lights up. Found by the lines' text, as every line number below an edit moves.
/// </summary>
public sealed class FileChangesWatchTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "bb-watch"));

    private static DiffLine Added(int line, string text) => new(DiffLineKind.Added, null, line, text);

    private static DiffResult Diff(params DiffFile[] files) => new(Root, "Changes in this chat", files);

    /// <summary>The panel's rows for a diff, as it builds them: a header, then the file's lines.</summary>
    private static List<DiffRow> Rows(DiffResult diff)
    {
        var rows = new List<DiffRow>();
        foreach (var file in diff.Files)
        {
            var item = new DiffFileItem(file, rows.Count, diff.FullPath(file));
            rows.Add(new DiffRow(item, null) { Index = rows.Count });
            foreach (var line in file.Lines)
                rows.Add(new DiffRow(item, line) { Index = rows.Count });
        }
        return rows;
    }

    [Fact]
    public void Only_the_lines_changed_since_are_fresh_though_the_numbers_below_moved()
    {
        var before = Diff(new DiffFile("a.ps1", "added", [Added(1, "one"), Added(2, "two"), Added(3, "three")]));
        var after = Diff(new DiffFile("a.ps1", "added", [Added(1, "one"), Added(2, "inserted"), Added(3, "two"), Added(4, "three")]));

        var fresh = FileChangesViewModel.Fresh(before, Rows(after));

        Assert.Equal(["inserted"], fresh.Select(r => r.Text));
        Assert.Equal(2, fresh[0].Index);
    }

    [Fact]
    public void A_new_file_is_fresh_whole_and_an_untouched_one_not_at_all()
    {
        var a = new DiffFile("a.ps1", "added", [Added(1, "same")]);
        var before = Diff(a);
        var after = Diff(a, new DiffFile("b.txt", "added", [Added(1, "x"), Added(2, "x")]));

        var fresh = FileChangesViewModel.Fresh(before, Rows(after));

        Assert.Equal(2, fresh.Count);
        Assert.All(fresh, r => Assert.Equal("b.txt", r.File.Name));
    }

    [Fact]
    public void A_line_repeated_once_more_is_fresh_once()
    {
        var before = Diff(new DiffFile("a.ps1", "added", [Added(1, "}")]));
        var after = Diff(new DiffFile("a.ps1", "added", [Added(1, "}"), Added(2, "}")]));

        Assert.Single(FileChangesViewModel.Fresh(before, Rows(after)));
    }
}
