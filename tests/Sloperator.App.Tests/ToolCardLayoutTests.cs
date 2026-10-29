using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// A tool call's card: a shell command under a short title, open; the screen a click left folded,
/// the picture of a look open.
/// </summary>
public sealed class ToolCardLayoutTests
{
    [Fact]
    public void A_shell_card_has_a_short_title_and_the_command_open_under_it()
    {
        const string command = "Get-ChildItem C:\\Users\\user\\Documents -Recurse -Filter *.xlsx | Select-Object FullName, Length";
        var (title, code) = HeadlessApp.Run(() => Shown(new ToolCallItem("1", "run_shell", "Run: " + command), card =>
            (card.GetVisualDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible && t.Text == "Run a command").Text,
             card.GetVisualDescendants().OfType<SelectableTextBlock>().Single(b => b.Classes.Contains("command")) is var b && b.IsEffectivelyVisible ? b.Text : null)));
        Assert.Equal("Run a command", title);
        Assert.Equal(command, code);
    }

    [Fact]
    public void A_shell_card_says_what_the_command_does_when_the_model_said_it()
    {
        var summary = new Sloperator.Core.Tools.ShellTool().Describe(new System.Text.Json.Nodes.JsonObject
        {
            ["command"] = "Get-ChildItem $env:TEMP\n| Select-Object -First 3",
            ["description"] = "List the newest\nlog files ",
        });
        var item = new ToolCallItem("1", "run_shell", summary);
        Assert.Equal("Run a command · List the newest log files", item.Title);
        Assert.Equal("Get-ChildItem $env:TEMP\n| Select-Object -First 3", item.Command);
        var bare = new ToolCallItem("1", "run_shell", new Sloperator.Core.Tools.ShellTool().Describe(new System.Text.Json.Nodes.JsonObject { ["command"] = "dir", ["description"] = " " }));
        Assert.Equal(("Run a command", "dir"), (bare.Title, bare.Command));
    }

    [Fact]
    public void Any_other_card_keeps_its_summary_as_the_title_and_has_no_command()
    {
        var item = new ToolCallItem("1", "read_file", "Read C:\\notes.txt");
        Assert.Equal("Read C:\\notes.txt", item.Title);
        Assert.False(item.HasCommand);
        Assert.False(new ToolCallItem("1", "ssh", "SSH box: ls").HasCommand);
    }

    [Theory]
    [InlineData("mouse_click", false)]
    [InlineData("wait", false)]
    [InlineData("press_keys", false)]
    [InlineData("screenshot", true)]
    [InlineData("zoom", true)]
    public void The_screen_an_action_left_is_folded_and_a_look_is_open(string tool, bool open)
    {
        var (imageShown, folded) = HeadlessApp.Run(() =>
        {
            var item = new ToolCallItem("1", tool, tool) { Screenshot = Picture(), ResultText = "done" };
            return Shown(item, card =>
            {
                var shot = card.GetVisualDescendants().OfType<Expander>().FirstOrDefault(e => e.IsVisible && Equals(e.Header, "Screenshot"));
                var image = card.GetVisualDescendants().OfType<Image>().Any(i => i.Source == item.Screenshot && i.IsEffectivelyVisible && i.Bounds.Height > 0);
                return (image, shot is { IsExpanded: false });
            });
        });
        Assert.Equal(open, imageShown);
        Assert.Equal(!open, folded);
    }

    private static Bitmap Picture() => new WriteableBitmap(new Avalonia.PixelSize(64, 40), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    /// <summary>The chat's own templates, out of a main window, showing one card.</summary>
    private static T Shown<T>(ToolCallItem item, Func<Control, T> read)
    {
        var main = new MainWindow();
        var scroll = main.FindControl<ScrollViewer>("ChatScroll")!;
        ((Panel)scroll.Parent!).Children.Remove(scroll);
        var items = scroll.GetLogicalDescendantsOfType<ItemsControl>().First();
        items.ItemsSource = new[] { item };
        var window = new Window { Width = 900, Height = 900, Content = scroll };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            return read(scroll);
        }
        finally
        {
            window.Close();
        }
    }
}
