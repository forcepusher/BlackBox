using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Sloperator.Core.Chats;
using SkiaSharp;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>
/// The picture of its work a run ends with (capture_result): in the run's last reply, under its
/// words, with the model's caption under it; saved with the chat and back when it is opened again.
/// </summary>
public sealed class CaptureReplyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sloperator-capture-reply-" + Guid.NewGuid().ToString("N")[..8]);

    public CaptureReplyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    private string Png(string name, int width, int height)
    {
        var path = Path.Combine(_dir, name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.SteelBlue);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }

    private static ToolCallItem Capture(string file, string arguments, DateTime at) =>
        new("c" + file.GetHashCode(), "capture_result", "Capture the result") { Arguments = arguments, SavedFiles = [file], State = ToolItemState.Done, At = at };

    [Fact]
    public void The_last_capture_of_a_run_goes_in_its_last_reply_and_comes_back_with_the_chat()
    {
        var before = Png("capture-1.png", 64, 40);
        var first = Png("capture-2.png", 64, 40);
        var last = Png("capture-3.png", 240, 120);
        var services = new PreviewServices(null, () => null);
        var started = DateTime.UtcNow;

        var (inReply, inEarlier, loaded, layout) = HeadlessApp.Run(() =>
        {
            var earlier = new AssistantMessageItem { Text = "An earlier answer.", IsStreaming = false, At = started.AddMinutes(-2) };
            var reply = new AssistantMessageItem { Text = "Made the chart in **Budget.xlsx**.", IsStreaming = false, At = started.AddSeconds(30) };
            List<ChatItemViewModel> items =
            [
                Capture(before, "{}", started.AddMinutes(-3)),
                earlier,
                new UserMessageItem("Make a chart") { At = started },
                Capture(first, """{"caption":"First try"}""", started.AddSeconds(10)),
                Capture(last, """{"window":"Excel","caption":"The chart, by month"}""", started.AddSeconds(20)),
                reply,
            ];
            MainWindowViewModel.ShowCaptureInReply(items, started, services);

            // Saved and opened again, as History does.
            var store = new ChatStore(_dir);
            store.Save(new SavedChat { Id = "chat", Title = "Make a chart", Entries = items.Select(MainWindowViewModel.ToEntry).ToList() });
            var entry = store.Load("chat")!.Entries.Last(e => e.Kind == ChatEntryKind.Assistant);
            var reopened = MainWindowViewModel.ReplyFrom(entry, services);
            return ((reply.Capture?.Path, reply.Caption), earlier.HasCapture, (reopened.Capture?.Path, reopened.Caption), Laid(reopened));
        });

        Assert.Equal((last, "The chart, by month"), inReply);
        Assert.False(inEarlier);
        Assert.Equal((last, "The chart, by month"), loaded);
        // Under the words, the caption under the picture, each 4 px apart or more.
        Assert.True(layout.Picture.Height > 0, "the picture isn't shown");
        Assert.True(layout.Picture.Top >= layout.TextBottom + 4, $"picture at {layout.Picture.Top}, text ends at {layout.TextBottom}");
        Assert.True(layout.CaptionTop >= layout.Picture.Bottom + 4, $"caption at {layout.CaptionTop}, picture ends at {layout.Picture.Bottom}");
        Assert.Equal(240, layout.Picture.Width);
    }

    [Fact]
    public void A_run_without_a_capture_or_a_reply_puts_no_picture_anywhere()
    {
        var file = Png("capture-1.png", 64, 40);
        var services = new PreviewServices(null, () => null);
        var started = DateTime.UtcNow;
        var (noCapture, failedCapture, noReply) = HeadlessApp.Run(() =>
        {
            var reply = new AssistantMessageItem { Text = "Done.", IsStreaming = false, At = started.AddSeconds(5) };
            MainWindowViewModel.ShowCaptureInReply([reply], started, services);
            var failed = Capture(file, "{}", started.AddSeconds(1));
            failed.State = ToolItemState.Error;
            var other = new AssistantMessageItem { Text = "Done.", IsStreaming = false, At = started.AddSeconds(5) };
            MainWindowViewModel.ShowCaptureInReply([failed, other], started, services);
            var thoughtOnly = new AssistantMessageItem { Reasoning = "hm", IsStreaming = false, At = started.AddSeconds(5) };
            MainWindowViewModel.ShowCaptureInReply([Capture(file, "{}", started.AddSeconds(1)), thoughtOnly], started, services);
            return (reply.HasCapture, other.HasCapture, thoughtOnly.HasCapture);
        });
        Assert.False(noCapture);
        Assert.False(failedCapture);
        Assert.False(noReply);
        Assert.Null(MainWindowViewModel.ToEntry(new AssistantMessageItem { Text = "x", IsStreaming = false }).Capture);
    }

    /// <summary>The reply in the chat's own templates: where its words end, its picture, and where its caption starts.</summary>
    private static (double TextBottom, Rect Picture, double CaptionTop) Laid(AssistantMessageItem reply)
    {
        // The picture is read in the background.
        for (var i = 0; i < 200 && reply.Capture?.Image is null; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }
        var main = new MainWindow();
        var scroll = main.FindControl<ScrollViewer>("ChatScroll")!;
        ((Panel)scroll.Parent!).Children.Remove(scroll);
        scroll.GetLogicalDescendantsOfType<ItemsControl>().First().ItemsSource = new[] { reply };
        var window = new Window { Width = 900, Height = 900, Content = scroll };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            Rect Box(Control c) => new(c.TranslatePoint(new Point(0, 0), scroll)!.Value, c.Bounds.Size);
            var text = scroll.GetVisualDescendants().OfType<MarkdownView>().Single(m => m.IsEffectivelyVisible);
            var picture = scroll.GetVisualDescendants().OfType<Image>().Single(i => i.Source == reply.Capture!.Image && i.IsEffectivelyVisible);
            var caption = scroll.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == reply.Caption && t.IsEffectivelyVisible);
            return (Box(text).Bottom, Box(picture), Box(caption).Top);
        }
        finally
        {
            window.Close();
        }
    }
}
