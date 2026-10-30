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

    [Fact]
    public void Selected_words_keep_their_font_and_size()
    {
        // Avalonia drew a selection over runs at the block's size in one run's font: the whole
        // selection turned into the code font a size up and the line grew (the user, 2026-10-30).
        var (before, after) = HeadlessApp.Run(() =>
        {
            var (window, scroll) = Show("Here is **what I did** with `notes.md` and `echo hi`, step by step, all in one go.", 1000);
            var block = scroll.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => b.Inlines?.Text?.Contains("notes.md") == true);
            var before = Shown(block);
            block.SelectionStart = 10;
            block.SelectionEnd = block.Inlines!.Text!.Length - 10;
            window.UpdateLayout();
            var after = Shown(block);
            window.Close();
            return (before, after);
        });
        Assert.Equal(before, after);
    }

    [Fact]
    public void Code_in_a_line_is_as_big_as_the_words_around_it()
    {
        // A reply's block got its text before it was in the window, at Avalonia's 12, and its code
        // stayed 11 beside the reply's 14 (the user, 2026-10-30); it is the words' own size now.
        var (text, code) = HeadlessApp.Run(() =>
        {
            var (window, scroll) = Show("Run `notes.md` now.", 1000);
            var block = scroll.GetVisualDescendants().OfType<SelectableTextBlock>().First(b => b.Inlines?.Text?.Contains("notes.md") == true);
            var runs = block.TextLayout.TextLines.SelectMany(l => l.TextRuns).Where(r => r.Text.Length > 0).ToList();
            var sizes = (block.FontSize, runs.First(r => r.Text.ToString().Contains("notes")).Properties!.FontRenderingEmSize);
            window.Close();
            return sizes;
        });
        Assert.Equal(14, text);
        Assert.Equal(14, code);
    }

    /// <summary>Each character's font, weight and size, and each line's width, in a block as laid out.</summary>
    private static List<string> Shown(SelectableTextBlock block)
    {
        var all = new List<string>();
        foreach (var line in block.TextLayout.TextLines)
        {
            foreach (var run in line.TextRuns.Where(r => r.Text.Length > 0))
                foreach (var c in run.Text.ToString())
                    all.Add($"{c} {run.Properties!.Typeface.FontFamily.Name} {run.Properties.Typeface.Weight} {run.Properties.FontRenderingEmSize}");
            all.Add($"line {line.WidthIncludingTrailingWhitespace:F2}");
        }
        return all;
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
