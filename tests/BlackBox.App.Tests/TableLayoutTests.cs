using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BlackBox.App.ViewModels;
using BlackBox.App.Views;
using Xunit;

namespace BlackBox.App.Tests;

/// <summary>
/// A reply's Markdown tables as the chat lays them out: a grid whose cells wrap inside their own
/// column. They were lines of monospace text, and a long cell wrapped back under the first column.
/// </summary>
public sealed class TableLayoutTests
{
    /// <summary>The user's two tables that fell apart (2026-10-26).</summary>
    private const string Options = """
        ## Other options
        | Option | Verdict |
        |---|---|
        | **WSL2 + Docker Engine inside Ubuntu** | Lightest and fastest. Ports reach Windows on `localhost`, so Unity needs no IP. But it isn't really a separate server: it shares one VM with every other WSL distro and has no real snapshots. The better networking mode ("mirrored") needs Windows 11, so on your Windows 10 you get the older mode. Good if you just want containers with the least setup. |
        | **Vagrant + VirtualBox/Hyper-V** | Works, but heavier: you write a Vagrantfile and install VirtualBox. Multipass does the same job more simply. |
        | **Plain QEMU / Hyper-V Manager** | Too much manual work for a setup you want to recreate easily. |
        """;

    private const string Qemu = """
        ## What Multipass does that you'd script yourself with QEMU
        | Task | Multipass | QEMU on Windows |
        |---|---|---|
        | **Get an OS** | Downloads and caches Ubuntu images | Download an Ubuntu cloud image yourself, then `qemu-img create -b ... -F qcow2` an overlay and `qemu-img resize` it to the disk size you want |
        | **First-boot setup** (user, SSH key, Docker) | `--cloud-init file.yaml` | Supply the cloud-init files yourself: build a small seed ISO (the usual tool, `cloud-localds`, doesn't exist on Windows), or serve them over HTTP and point the VM at it with `-smbios type=1,serial=ds=nocloud;s=http://10.0.2.2:8000/` |
        | **Speed** | Uses Hyper-V automatically | Turn on the *Windows Hypervisor Platform* feature and pass `-accel whpx`. If you forget, QEMU silently falls back to pure software emulation (its default), which is very slow. HAXM (an older speed-up option) is Intel-only, so it's no use on your Ryzen. |
        | **Networking** | The VM gets its own IP on Hyper-V's default network | QEMU's default networking isn't reachable from Windows, so every port needs a forward (`hostfwd=tcp::2222-:22,hostfwd=tcp::5105-:5105,...`). A real IP of its own needs the TAP-Windows driver and a network bridge |
        | **Snapshots** | `multipass snapshot` / `restore` | `qemu-img snapshot` on the disk image (VM powered off), or `savevm`/`loadvm` in the QEMU monitor (QEMU's built-in console) |
        """;

    private static readonly string Wide = "| " + string.Join(" | ", Enumerable.Range(1, 14).Select(i => $"Column{i}Name")) + " |\n|"
        + string.Concat(Enumerable.Repeat("---|", 14)) + "\n| " + string.Join(" | ", Enumerable.Range(1, 14).Select(i => $"value{i}")) + " |";

    public static TheoryData<string> Tables() => new()
    {
        Options,
        Qemu,
        "Before.\n\n" + Options + "\n\nAfter, and a second:\n\n" + Qemu + "\n\nThe end.",
        // Alignment row, ragged rows (short and long), no header, a cell of one long word.
        "| Shop | August | Note |\n|:--|--:|:-:|\n| Riverside | 15,700 |\n| Online | 144,600 | best | extra |",
        "| a | b |\n| c | d |",
        "| Path |\n|---|\n| " + @"C:\Repos\BlackBox\.dev\wt\md-tables\src\BlackBox.App\Views\MarkdownTable.cs" + " |",
        Wide,
    };

    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_cell_wraps_inside_its_own_column(string markdown)
    {
        foreach (var width in new[] { 1000, 760, 520 })
        {
            var problems = HeadlessApp.Run(() => Problems(markdown, width));
            Assert.True(problems.Count == 0, $"At a window {width} wide: {string.Join("; ", problems)}");
        }
    }

    [Fact]
    public void Header_cells_are_bold_and_cells_keep_code_and_italics_in_the_chat_font()
    {
        var (header, code, italic, fonts) = HeadlessApp.Run(() =>
        {
            var (window, scroll) = Show(Qemu, 1000);
            var table = scroll.GetVisualDescendants().OfType<MarkdownTable>().Single();
            var runs = table.Cells.SelectMany(c => c.Inlines!.OfType<Run>()).ToList();
            var result = (
                table.Cells.Take(3).All(c => c.Inlines!.OfType<Run>().All(r => r.FontWeight == FontWeight.SemiBold)),
                runs.Any(r => r.Text == "--cloud-init file.yaml" && r.FontFamily.Name.Contains("Mono")),
                runs.Any(r => r.Text == "Windows Hypervisor Platform" && r.FontStyle == FontStyle.Italic),
                table.Cells.Select(c => c.FontFamily.Name).Distinct().ToList());
            window.Close();
            return result;
        });
        Assert.True(header);
        Assert.True(code);
        Assert.True(italic);
        Assert.DoesNotContain(fonts, f => f.Contains("Mono"));
    }

    [Fact]
    public void A_table_too_wide_for_the_reply_scrolls_sideways()
    {
        var (extent, viewport) = HeadlessApp.Run(() =>
        {
            var (window, scroll) = Show(Wide, 760);
            var inner = scroll.GetVisualDescendants().OfType<MarkdownTable>().Single().GetVisualDescendants().OfType<ScrollViewer>().First();
            var result = (inner.Extent.Width, inner.Viewport.Width);
            window.Close();
            return result;
        });
        Assert.True(extent > viewport + 100, $"{extent} in {viewport}");
    }

    private static (Window, ScrollViewer) Show(string markdown, int width)
    {
        var main = new MainWindow();
        var scroll = main.FindControl<ScrollViewer>("ChatScroll")!;
        ((Panel)scroll.Parent!).Children.Remove(scroll);
        var items = scroll.GetLogicalDescendantsOfType<ItemsControl>().First();
        var item = new AssistantMessageItem { Text = markdown };
        item.Finish(DateTime.UtcNow);
        items.ItemsSource = new[] { item };
        var window = new Window { Width = width, Height = 900, Content = scroll };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, scroll);
    }

    private static List<string> Problems(string markdown, int width)
    {
        var (window, scroll) = Show(markdown, width);
        var problems = new List<string>();
        var tables = scroll.GetVisualDescendants().OfType<MarkdownTable>().ToList();
        var expected = markdown.Split("\n\n").Count(p => p.Contains("|"));
        if (tables.Count != expected)
            problems.Add($"{tables.Count} tables shown, not {expected}");
        if (scroll.GetVisualDescendants().OfType<SelectableTextBlock>().Any(b => b.Inlines?.Text?.Contains("|---") == true || b.Inlines?.Text?.Contains("| **") == true))
            problems.Add("pipes shown as text");
        foreach (var table in tables)
        {
            var reply = table.FindAncestorOfType<MarkdownView>()!;
            var inner = table.GetVisualDescendants().OfType<ScrollViewer>().First();
            var scrolls = inner.Extent.Width > inner.Viewport.Width + 0.5;
            if (!scrolls && table.Bounds.Width > reply.Bounds.Width + 0.5)
                problems.Add($"table {table.Bounds.Width:F0} wide in a reply {reply.Bounds.Width:F0} wide");
            var lefts = new Dictionary<int, double>();
            foreach (var (cell, i) in table.Cells.Select((c, i) => (c, i)))
            {
                var layout = cell.TextLayout;
                var shown = layout.TextLines.Sum(l => l.Length);
                var all = cell.Inlines?.Text?.Length ?? 0;
                if (shown < all)
                    problems.Add($"cell {i}: {shown} of {all} characters laid out");
                if (layout.Height > cell.Bounds.Height - cell.Padding.Top - cell.Padding.Bottom + 0.5)
                    problems.Add($"cell {i}: text {layout.Height:F1} high in {cell.Bounds.Height:F1}");
                if (layout.Width > cell.Bounds.Width - cell.Padding.Left - cell.Padding.Right + 0.5)
                    problems.Add($"cell {i}: text {layout.Width:F1} wide in {cell.Bounds.Width:F1}");
                var column = i % CountColumns(table);
                if (lefts.TryGetValue(column, out var left) && Math.Abs(left - cell.Bounds.X) > 0.01)
                    problems.Add($"cell {i} starts at {cell.Bounds.X}, its column at {left}");
                lefts[column] = cell.Bounds.X;
            }
        }
        window.Close();
        return problems;
    }

    private static int CountColumns(MarkdownTable table) =>
        table.Cells.Count(c => Math.Abs(c.Bounds.Y - table.Cells[0].Bounds.Y) < 0.01);
}
