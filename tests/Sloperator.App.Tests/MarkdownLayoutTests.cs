using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// Whole replies as models write them (Replies\*.md: every construct at once, a setup guide with
/// code in its steps, an answer with nested lists, the user's tables) as the chat lays them out.
/// </summary>
public sealed class MarkdownLayoutTests
{
    public static TheoryData<string> Replies() => new(Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Replies"), "*.md").Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(Replies))]
    public void Every_line_shows_and_list_items_wrap_under_their_own_text(string file)
    {
        var markdown = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Replies", file));
        foreach (var width in new[] { 1000, 760, 520 })
        {
            var problems = HeadlessApp.Run(() => Problems(markdown, width));
            Assert.True(problems.Count == 0, $"At a window {width} wide: {string.Join("; ", problems)}");
        }
    }

    [Fact]
    public void Markup_never_shows_as_written()
    {
        var shown = HeadlessApp.Run(() =>
        {
            var (window, scroll) = Show(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Replies", "setup-guide.md")), 1000);
            var text = string.Join("\n", scroll.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Inlines?.Text ?? t.Text));
            window.Close();
            return text;
        });
        foreach (var markup in new[] { "**", "## ", "> ", "[!WARNING]", "|---", "1. ", "- " })
            Assert.DoesNotContain(markup, shown);
    }

    internal static (Window, ScrollViewer) Show(string markdown, int width)
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
        foreach (var block in scroll.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible && b.Inlines?.Count > 0))
        {
            var layout = block.TextLayout;
            var shown = layout.TextLines.Sum(l => l.Length);
            var all = block.Inlines!.Text!.Length;
            if (shown < all)
                problems.Add($"\"{block.Inlines.Text[..Math.Min(30, all)]}\": {shown} of {all} characters laid out");
            if (layout.Height > block.Bounds.Height - block.Padding.Top - block.Padding.Bottom + 0.5)
                problems.Add($"\"{block.Inlines.Text[..Math.Min(30, all)]}\": text {layout.Height:F1} high in {block.Bounds.Height:F1}");
        }
        // A list item's text sits right of its marker, so its wrapped lines start there too.
        foreach (var parts in scroll.GetVisualDescendants().OfType<MarkdownParts>())
            foreach (var grid in parts.GetVisualDescendants().OfType<Grid>().Where(g => g.Children.Count == 2 && g.Children[1] is SelectableTextBlock))
            {
                var marker = grid.Children[0];
                var text = (SelectableTextBlock)grid.Children[1];
                if (text.Bounds.X + text.Padding.Left < marker.Bounds.Right - 0.5)
                    problems.Add($"\"{text.Inlines?.Text}\" starts at {text.Bounds.X + text.Padding.Left}, left of its marker's end {marker.Bounds.Right}");
            }
        window.Close();
        return problems;
    }
}
