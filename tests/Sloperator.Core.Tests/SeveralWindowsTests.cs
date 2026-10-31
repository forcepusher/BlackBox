using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Several Sloperator windows at once, in one process with one Data folder: the settings each one
/// changes, and the mouse and keyboard their agents take turns with.
/// </summary>
public class SeveralWindowsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bb-windows-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void File_changes_wrap_and_watch_are_on_until_turned_off_and_stay_off()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        File.WriteAllText(paths.SettingsFile, """{"server":{"model":"qwen"}}""");
        var store = new SettingsStore(paths);
        var settings = store.Load();
        Assert.True(settings.Panels.WrapDiff);
        Assert.True(settings.Panels.WatchChanges);

        settings.Panels.WrapDiff = false;
        store.Save(settings);

        var again = new SettingsStore(paths).Load();
        Assert.False(again.Panels.WrapDiff);
        Assert.True(again.Panels.WatchChanges);
        Assert.Equal("qwen", again.Server.Model);
    }

    [Fact]
    public void Notify_me_when_done_is_off_until_turned_on_and_another_windows_save_keeps_it()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        // A settings.json from before the setting, or one with "notify": null typed in by hand.
        File.WriteAllText(paths.SettingsFile, """{"server":{"model":"qwen"},"notify":null}""");
        var (one, two) = (new SettingsStore(paths), new SettingsStore(paths));
        var inOne = one.Load();
        var inTwo = two.Load();
        Assert.False(inOne.Notify.WhenDone);

        inOne.Notify.WhenDone = true;
        one.Save(inOne);
        // The other window, which still has it off, saves its place.
        inTwo.Window = new WindowPlacement { X = 10, Y = 20, Width = 900, Height = 700 };
        two.Save(inTwo);

        Assert.True(new SettingsStore(paths).Load().Notify.WhenDone);
    }

    [Fact]
    public void Each_window_saves_only_what_it_changed()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        var first = new SettingsStore(paths);
        first.Save(new AppSettings { Server = { BaseUrl = "http://localhost:1234/v1", Model = "qwen" } });

        // Two windows open with the same settings.
        var (one, two) = (new SettingsStore(paths), new SettingsStore(paths));
        var inOne = one.Load();
        var inTwo = two.Load();

        // The first picks another model; the second, which still has the old one, saves where it was put.
        inOne.Server.Model = "gemma";
        one.Save(inOne);
        inTwo.Window = new WindowPlacement { X = 10, Y = 20, Width = 900, Height = 700 };
        inTwo.Agent.CustomInstructions = "Be brief.";
        two.Save(inTwo);

        var saved = new SettingsStore(paths).Load();
        Assert.Equal("gemma", saved.Server.Model);
        Assert.Equal("http://localhost:1234/v1", saved.Server.BaseUrl);
        Assert.Equal(10, saved.Window?.X);
        Assert.Equal("Be brief.", saved.Agent.CustomInstructions);

        // Changed back in the first window: what it changes again is written again.
        inOne.Server.Model = "qwen";
        one.Save(inOne);
        Assert.Equal("qwen", new SettingsStore(paths).Load().Server.Model);
        Assert.Equal("Be brief.", new SettingsStore(paths).Load().Agent.CustomInstructions);
    }

    [Fact]
    public void A_key_one_window_removed_goes_and_one_it_never_had_stays()
    {
        var before = JsonNode.Parse("""{"a":1,"b":{"x":1,"y":2},"gone":true}""")!.AsObject();
        var mine = JsonNode.Parse("""{"a":1,"b":{"x":5,"y":2}}""")!.AsObject();
        var theirs = JsonNode.Parse("""{"a":2,"b":{"x":1,"y":3},"gone":true,"new":"kept"}""")!.AsObject();

        var merged = SettingsStore.Merge(before, mine, theirs);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"a":2,"b":{"x":5,"y":3},"new":"kept"}"""), merged), merged.ToJsonString());
    }

    [Fact]
    public void A_settings_dialog_saves_only_what_was_changed_in_it_over_what_another_window_saved()
    {
        var window = new AppSettings();
        var opened = window.Clone(); // the dialog's copy
        var dialog = opened.Clone();
        dialog.Agent.CustomInstructions = "Answer in French.";
        // Meanwhile another window's Settings set "Approval" to ask, and this window took it.
        window.Agent.Approval = ApprovalMode.AskAll;

        var saved = window.With(opened, dialog);

        Assert.Equal("Answer in French.", saved.Agent.CustomInstructions);
        Assert.Equal(ApprovalMode.AskAll, saved.Agent.Approval);
    }

    [Fact]
    public void Windows_saving_at_once_keep_each_others_changes()
    {
        var paths = new AppPaths(_root);
        paths.EnsureCreated();
        new SettingsStore(paths).Save(new AppSettings());
        var stores = Enumerable.Range(0, 8).Select(_ => new SettingsStore(paths)).ToList();
        var settings = stores.Select(s => s.Load()).ToList();

        Parallel.For(0, stores.Count, i =>
        {
            settings[i].Agent.CustomInstructions = i == 0 ? "Be brief." : settings[i].Agent.CustomInstructions;
            settings[i].Server.Model = i == 1 ? "gemma" : settings[i].Server.Model;
            settings[i].Window = i == 2 ? new WindowPlacement { X = 5, Y = 5, Width = 900, Height = 700 } : settings[i].Window;
            stores[i].Save(settings[i]);
        });

        var saved = new SettingsStore(paths).Load();
        Assert.Equal("Be brief.", saved.Agent.CustomInstructions);
        Assert.Equal("gemma", saved.Server.Model);
        Assert.Equal(5, saved.Window?.X);
    }

    // ---- the mouse and keyboard ----------------------------------------------------------

    private long _now;

    private DesktopTurn NewTurn() => new(() => _now);

    [Fact]
    public async Task One_agent_at_a_time_has_the_mouse_and_keyboard()
    {
        var turn = NewTurn();
        object a = new(), b = new();

        Assert.False(await turn.TakeAsync(a, TimeSpan.Zero, null, CancellationToken.None));
        // In the middle of a's action, however long: b doesn't get them.
        _now += 10 * 60_000;
        Assert.Null(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));

        // Between two of a's actions, for less than the handover time: still a's.
        turn.Done(a, acted: true);
        _now += (long)DesktopTurn.IdleHandover.TotalMilliseconds - 1;
        Assert.Null(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));
        Assert.False(await turn.TakeAsync(a, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(a, acted: true);

        // Left alone for longer: b's.
        _now += (long)DesktopTurn.IdleHandover.TotalMilliseconds;
        Assert.NotNull(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));
        Assert.Null(await turn.TakeAsync(a, TimeSpan.Zero, null, CancellationToken.None));
    }

    [Fact]
    public async Task A_task_that_ends_hands_them_over_at_once()
    {
        var turn = NewTurn();
        object a = new(), b = new();
        Assert.False(await turn.TakeAsync(a, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(a, acted: true);

        turn.Release(a);

        Assert.NotNull(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_agent_hears_when_another_acted_since_it_last_saw_the_screen()
    {
        var turn = NewTurn();
        object a = new(), b = new();
        turn.Looked(b);

        // a acts and its task ends; b, which looked before, would aim at what may have moved.
        Assert.False(await turn.TakeAsync(a, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(a, acted: true);
        turn.Release(a);
        Assert.True(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(b, acted: false);

        // Shown the screen as it is now, it goes on; its own actions don't count against it.
        turn.Looked(b);
        Assert.False(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(b, acted: true);
        Assert.False(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(b, acted: true);
        turn.Release(b);

        // An action held back for a look again, since b acted, is no action b has to hear of.
        turn.Looked(b);
        Assert.True(await turn.TakeAsync(a, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(a, acted: false);
        turn.Looked(a);
        turn.Release(a);
        Assert.False(await turn.TakeAsync(b, TimeSpan.Zero, null, CancellationToken.None));
    }

    [Fact]
    public async Task An_agent_that_waited_for_another_is_told_the_screen_changed()
    {
        var turn = NewTurn();
        object a = new(), b = new();
        Assert.False(await turn.TakeAsync(a, TimeSpan.Zero, null, CancellationToken.None));

        var waits = new List<TimeSpan>();
        var waiting = turn.TakeAsync(b, TimeSpan.FromSeconds(30), waits.Add, CancellationToken.None);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        Assert.NotEmpty(waits);

        turn.Done(a, acted: true);
        turn.Release(a);
        Assert.True(await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_action_waits_for_its_turn_and_looks_again_when_another_window_acted()
    {
        var turn = new DesktopTurn();
        var platform = new FakePlatform();
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), DesktopTurn = turn };
        ScreenshotTool.Take(ctx);
        // Another window's agent clicked meanwhile, and its task ended.
        var other = new object();
        Assert.NotNull(await turn.TakeAsync(other, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(other, acted: true);
        turn.Release(other);

        var result = await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);

        Assert.Empty(platform.Actions); // nothing pressed on a screen the model hasn't seen
        Assert.StartsWith("Nothing was done: another Sloperator window", result.Text);
        Assert.NotNull(result.Image);
        // Nor the rest of the same reply, decided on the same old screen: typed into whatever the
        // other agent left in front, it would land in the wrong program.
        result = await new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = "John" }, ctx, CancellationToken.None);
        Assert.Empty(platform.Actions);
        Assert.StartsWith("Nothing was done: an action before this one in your reply was held back", result.Text);

        // Asked again in the next reply, with the screen now seen: done.
        ctx.ScreenStale = false;
        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        Assert.NotEmpty(platform.Actions);
        await ctx.FinishRunAsync();
    }

    [Fact]
    public async Task A_step_held_back_holds_back_the_rest_of_it_and_the_next_step_acts()
    {
        var turn = new DesktopTurn();
        var platform = new FakePlatform();
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = _root, DesktopTurn = turn };
        ScreenshotTool.Take(ctx);
        var other = new object(); // another window's agent acts after this one looked
        Assert.NotNull(await turn.TakeAsync(other, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(other, acted: true);
        turn.Release(other);
        var server = new ScriptedServer(
            Sse.ToolCalls(("c1", "press_keys", """{"keys":"enter"}"""), ("c2", "type_text", """{"text":"John"}""")),
            Sse.ToolCall("c3", "type_text", """{"text":"John"}"""),
            Sse.Text("Typed."));
        var session = new AgentSession(new Llm.OpenAiChatClient(new HttpClient(server), "http://test", null), new Llm.ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: true), ctx, "system prompt");

        await session.RunAsync("type John", new RecordingObserver(), CancellationToken.None);

        string Result(string id) => session.History.Single(m => m.ToolCallId == id).Content!;
        Assert.StartsWith("Nothing was done: another Sloperator window", Result("c1"));
        Assert.StartsWith("Nothing was done: an action before this one", Result("c2"));
        Assert.DoesNotContain("Nothing was done", Result("c3")); // the next step, on the screen it was shown, acts
        Assert.Single(platform.Actions, a => a.Contains("John"));
    }

    [Fact]
    public async Task A_windows_next_task_hears_what_another_window_did_since_its_last_look()
    {
        var turn = new DesktopTurn();
        var window = new object();
        var platform = new FakePlatform();
        ToolContext Run() => new() { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), DesktopTurn = turn, DesktopSeat = window };
        var first = Run();
        ScreenshotTool.Take(first);
        await first.FinishRunAsync();
        // Another window's agent acted after this window's task ended.
        var other = new object();
        Assert.NotNull(await turn.TakeAsync(other, TimeSpan.Zero, null, CancellationToken.None));
        turn.Done(other, acted: true);
        turn.Release(other);

        // "Press Enter" in this window's next message: the chat's last screenshot is no longer the screen.
        var next = Run();
        var result = await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, next, CancellationToken.None);
        Assert.Empty(platform.Actions);
        Assert.StartsWith("Nothing was done: another Sloperator window", result.Text);
        await next.FinishRunAsync();

        // A window whose agent never looked, after another acted, looks first too.
        var fresh = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), DesktopTurn = turn, DesktopSeat = new object() };
        result = await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, fresh, CancellationToken.None);
        Assert.Empty(platform.Actions);
        Assert.StartsWith("Nothing was done", result.Text);
        await fresh.FinishRunAsync();
    }

    [Fact]
    public async Task An_action_whose_turn_never_came_does_nothing_and_says_why()
    {
        var turn = new DesktopTurn();
        Assert.NotNull(await turn.TakeAsync(new object(), TimeSpan.Zero, null, CancellationToken.None)); // another window's agent, mid-action
        var platform = new FakePlatform();
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), DesktopTurn = turn };
        var wait = DesktopActionTool.MaxTurnWait;
        DesktopActionTool.MaxTurnWait = TimeSpan.Zero;
        try
        {
            var result = await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = 10 }, ctx, CancellationToken.None);

            Assert.Empty(platform.Actions);
            Assert.Contains("Do what doesn't need them meanwhile", result.Text);
        }
        finally
        {
            DesktopActionTool.MaxTurnWait = wait;
        }
    }

    [Fact]
    public async Task The_turn_is_let_go_of_when_the_task_ends()
    {
        var turn = new DesktopTurn();
        var platform = new FakePlatform();
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = Path.GetTempPath(), DesktopTurn = turn };

        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "enter" }, ctx, CancellationToken.None);
        Assert.NotEmpty(platform.Actions);
        var other = new object();
        Assert.Null(await turn.TakeAsync(other, TimeSpan.Zero, null, CancellationToken.None)); // left alone only a moment

        await ctx.FinishRunAsync();
        Assert.NotNull(await turn.TakeAsync(other, TimeSpan.Zero, null, CancellationToken.None));
    }
}
