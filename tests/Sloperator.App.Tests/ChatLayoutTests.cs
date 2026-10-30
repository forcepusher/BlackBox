using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.App.ViewModels;
using Sloperator.App.Views;
using Xunit;

namespace Sloperator.App.Tests;

/// <summary>The app as the tests start it: no screen, the real fonts.</summary>
public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont();

    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp), AvaloniaTestIsolationLevel.PerAssembly));

    public static T Run<T>(Func<T> action) => Session.Value.Dispatch(action, CancellationToken.None).GetAwaiter().GetResult();
}

/// <summary>
/// The chat's messages as the window lays them out: every line of a message's text is shown.
/// A bubble as wide as its words took its sides off a negative margin, and Avalonia cut the
/// measured width to what the bubble offered before taking the margin off: the text came back
/// narrower than measured, wrapped once more, and its last line went under the bubble's edge.
/// </summary>
public sealed class ChatLayoutTests
{
    private const string Long = "See those projects. I need to make a local testing setup for the backend, to basically spin up all the needed servers and such to test the stability of the new features I add to the game.";
    private const string Second = "Test need to be able to work both from HOC-Web-Server and from Unity test runner.";
    private const string Last = "Analyze and tell me what do you think is the best approach for it?";

    /// <summary>The user's two prompts that lost their last line (2026-10-26), and texts around them.</summary>
    public static TheoryData<string> Texts()
    {
        var data = new TheoryData<string>
        {
            Long + "\r\n" + Second + "\r\n\r\n" + Last,
            Long + "\r\n\n" + Second,
            Long + "\n" + Second,
            Long,
            "short",
            "a\n\n\n\nb",
            "\n\nstarts and ends with empty lines\n\n",
            "\tindented with a tab\n    and with spaces\n- a dash\n# a hash\n> a quote\n```\ncode\n```",
            new string('x', 300),
            "emoji 😀🎉 and accents éàü " + Long,
        };
        // Lengths that put a line's end at every place across the bubble's width.
        for (var n = 60; n <= 260; n += 7)
            data.Add(string.Join(' ', Enumerable.Repeat("word", n / 5)) + "\nnext line\n\nafter an empty one");
        return data;
    }

    [Theory]
    [MemberData(nameof(Texts))]
    public void A_user_message_shows_all_its_lines(string text) =>
        AssertAllShown(new UserMessageItem(text));

    [Theory]
    [MemberData(nameof(Texts))]
    public void A_user_message_with_files_shows_all_its_lines(string text) =>
        AssertAllShown(new UserMessageItem(text, [@"E:\Repos\HoC-Web-Server", @"E:\Repos\Heroes-of-the-Chests"]));

    [Theory]
    [MemberData(nameof(Texts))]
    public void A_notice_shows_all_its_lines(string text) =>
        AssertAllShown(new NoticeItem(text, isError: true));

    [Theory]
    [MemberData(nameof(Texts))]
    public void A_notice_with_continue_shows_all_its_lines(string text) =>
        AssertAllShown(new NoticeItem(text, isError: false, carryOn: () => { }));

    private static void AssertAllShown(ChatItemViewModel item)
    {
        foreach (var width in new[] { 1000, 760, 520 })
        {
            var problems = HeadlessApp.Run(() => Problems(item, width));
            Assert.True(problems.Count == 0, $"At a window {width} wide: {string.Join("; ", problems)}");
        }
    }

    /// <summary>The chat's own scroller and templates, out of a main window, showing one item.</summary>
    private static List<string> Problems(ChatItemViewModel item, int width)
    {
        var main = new MainWindow();
        var scroll = main.FindControl<ScrollViewer>("ChatScroll")!;
        ((Panel)scroll.Parent!).Children.Remove(scroll);
        var items = scroll.GetLogicalDescendantsOfType<ItemsControl>().First();
        items.ItemsSource = new[] { item };
        var window = new Window { Width = width, Height = 900, Content = scroll };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var problems = new List<string>();
        var blocks = scroll.GetVisualDescendants().OfType<SelectableTextBlock>().Where(b => b.IsEffectivelyVisible && b.Inlines?.Count > 0).ToList();
        if (blocks.Count == 0)
            problems.Add("no text shown");
        foreach (var block in blocks)
        {
            var layout = block.TextLayout;
            var shown = layout.TextLines.Sum(l => l.Length);
            var all = block.Inlines!.Text!.Length;
            if (shown < all)
                problems.Add($"{shown} of {all} characters laid out");
            var room = block.Bounds.Height - block.Padding.Top - block.Padding.Bottom;
            if (layout.Height > room + 0.5)
                problems.Add($"text {layout.Height:F1} high in {room:F1}");
        }
        window.Close();
        return problems;
    }
}

internal static class LogicalExtensions
{
    public static IEnumerable<T> GetLogicalDescendantsOfType<T>(this Control control) where T : Control =>
        Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(control).OfType<T>();
}
