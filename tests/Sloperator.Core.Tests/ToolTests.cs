using System.Text.Json.Nodes;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;
#if WINDOWS
using Native = Sloperator.Platform.Windows.Native;
#endif

namespace Sloperator.Core.Tests;

public class ToolTests
{
    private static ToolContext Ctx(IPlatformServices platform, string? dir = null, AgentSettings? settings = null) => new()
    {
        Platform = platform,
        Settings = settings ?? new AgentSettings { ScreenshotAfterActions = false },
        WorkingDirectory = dir ?? Path.GetTempPath(),
    };

    [Fact]
    public void Screen_space_scales_down_and_maps_back()
    {
        var space = new ScreenSpace(new ScreenRect(0, 0, 2560, 1440), (1280, 720));
        Assert.Equal((1280, 704), (space.ImageWidth, space.ImageHeight)); // 720 snaps to the 32px grid
        Assert.Equal((200, 307), space.ToScreen(100, 150));
        Assert.Equal((100, 150), space.ToModel(200, 307));
        Assert.Equal((2559, 1439), space.ToScreen(5000, 5000)); // clamped to the screen
    }

    [Fact]
    public void Pixel_coordinates_are_described_with_the_image_size()
    {
        // Told only "pixels", DeepSeek V4.1 Flash answered on a 0-1000 grid.
        var space = new ScreenSpace(new ScreenRect(0, 0, 2560, 1080), (2560, 1080));
        Assert.Contains("2560x1088", space.Describe());
    }

    [Fact]
    public void A_point_given_as_fractions_of_the_screenshot_lands_where_it_means()
    {
        // Kilo's free Nemotron answered (0.8711, 0.8833) for a button at (1110, 635) on a 1280x720
        // screenshot, though asked for pixels.
        var pixels = new ScreenSpace(new ScreenRect(0, 0, 1280, 720), null);
        Assert.Equal((1115, 636), pixels.ToScreen(0.8711, 0.8833));
        // Whole numbers are points as they are, however near the corner.
        Assert.Equal((1, 1), pixels.ToScreen(1, 1));
        Assert.Equal((0, 0), pixels.ToScreen(0, 0));
    }

    [Fact]
    public async Task Every_model_points_in_pixels_of_the_screenshot()
    {
        // Told the size, qwen3.8 (q6_k_xl) clicked better in pixels than on its 0-1000 grid: 21/28
        // against 19/28 (2026-10-29), so no model gets the grid.
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1080) };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false, ScreenshotMaxWidth = 1920, ScreenshotMaxHeight = 1080 }, WorkingDirectory = Path.GetTempPath() };
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 960, ["y"] = 400 }, ctx, CancellationToken.None);
        Assert.Equal("move 1280,540", platform.Actions[0]);
        var shot = await new ScreenshotTool().ExecuteAsync([], ctx, CancellationToken.None);
        Assert.Equal("Screenshot 1920x800. Mouse cursor at (960, 400).", shot.Text);

        var prompt = Sloperator.Core.Agent.SystemPrompt.Build("Test OS", ctx.ScreenSpaces(), "C:\\", "");
        Assert.Contains("x/y are pixels of the screenshot, which is 1920x800", prompt);
        Assert.DoesNotContain("grid", prompt);
        var drag = await new MouseDragTool().ExecuteAsync(new JsonObject { ["x"] = 100, ["y"] = 100, ["to_x"] = 200, ["to_y"] = 200 }, ctx, CancellationToken.None);
        Assert.Equal("Dragged 134 x 135 px on screen.", drag.Text);
    }

    [Theory]
    [InlineData(@"Hello from Gemma\n", "Hello from Gemma\n")]
    [InlineData(@"gemma.txt\n", "gemma.txt\n")]
    [InlineData(@"two lines\n\n", "two lines\n\n")]
    [InlineData(@"printf(""hi\n"");", @"printf(""hi\n"");")]
    [InlineData(@"C:\Repos\work\n", @"C:\Repos\work\n")]
    [InlineData("first line\nsecond\\n", "first line\nsecond\\n")]
    public void A_line_break_written_out_at_the_end_presses_Enter(string sent, string typed) =>
        Assert.Equal(typed, TypeTextTool.EnterForEscapedBreaks(sent));

    [Fact]
    public async Task A_click_goes_exactly_to_the_models_x_y()
    {
        // The model's own coordinates, nothing else: a fresh look that re-aimed clicks moved
        // DeepSeek's measured aims one Hierarchy row off, or into the Scene view.
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1440) };
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotMaxWidth = 1280, ScreenshotMaxHeight = 720, ScreenshotAfterActions = false });
        var click = new MouseClickTool();
        var result = await click.ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Equal("move 20,41", platform.Actions[0]);
        Assert.Equal("Clicked.", result.Text);
        Assert.Equal("Click at (10, 20)", click.Describe(new JsonObject { ["x"] = 10, ["y"] = 20 }));
        Assert.Equal(["x", "y"], click.Parameters["required"]!.AsArray().Select(n => (string?)n));
        await Assert.ThrowsAsync<ToolArgumentException>(() => click.ExecuteAsync(new JsonObject { ["x"] = 10 }, ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Zoom_shows_a_sharp_enlarged_region_with_rulers_in_screenshot_coordinates()
    {
        // A 2560x1440 screen sent at 1280x704: a 640x352 region around (640, 352) is 1280x704 screen pixels.
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1440) };
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotMaxWidth = 1280, ScreenshotMaxHeight = 720 });
        var zoom = new ZoomTool();
        var result = await zoom.ExecuteAsync(new JsonObject { ["x"] = 640, ["y"] = 352, ["width"] = 320, ["height"] = 176 }, ctx, CancellationToken.None);
        Assert.Equal(new ScreenRect(960, 540, 640, 360), platform.Captures[^1]);
        Assert.StartsWith("Zoomed in on (480, 264) to (800, 440) of the screenshot, 2x.", result.Text);
        Assert.NotNull(result.ImageCaption);
        using (var codec = SkiaSharp.SKCodec.Create(new MemoryStream(result.Image!)))
            Assert.Equal((1252 + 30, 704 + 30), (codec.Info.Width, codec.Info.Height)); // fits the screenshot's size, with the rulers

        // Never more than 4x, kept inside the screen, a quarter of the screenshot by default.
        result = await zoom.ExecuteAsync(new JsonObject { ["x"] = 5, ["y"] = 5, ["width"] = 20, ["height"] = 20 }, ctx, CancellationToken.None);
        Assert.Equal(new ScreenRect(0, 0, 40, 41), platform.Captures[^1]);
        Assert.Contains(", 4x.", result.Text);
        await zoom.ExecuteAsync(new JsonObject { ["x"] = 1280, ["y"] = 704 }, ctx, CancellationToken.None);
        Assert.Equal(new ScreenRect(1920, 1080, 640, 360), platform.Captures[^1]);

        Assert.Equal(10, ZoomTool.Step(100, 800));
        Assert.Equal(100, ZoomTool.Step(1000, 1280));
        Assert.Equal(1, ZoomTool.Step(5, 1280));
    }

    [Fact]
    public async Task A_click_that_changes_nothing_is_reported_as_a_miss()
    {
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 384, 192), Desktop = new RawImage(384, 192, new byte[384 * 192 * 4]) };
        var ctx = Ctx(platform, settings: new AgentSettings());
        var missed = await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 100, ["y"] = 50 }, ctx, CancellationToken.None);
        Assert.Contains("Nothing on screen changed after this click", missed.Text);
        Assert.Contains("Mouse cursor at (100, 50)", missed.Text);
        Assert.NotNull(missed.Image);

        // A small change right at the click (a checkbox ticking) counts as a reaction.
        platform.OnClick = p => { for (var i = 0; i < 10; i++) p.Desktop!.Bgra[(52 * 384 + 100 + i) * 4 + 1] = 255; };
        var hit = await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 100, ["y"] = 50 }, ctx, CancellationToken.None);
        Assert.DoesNotContain("Nothing on screen changed", hit.Text);

        // So does a big one elsewhere (a dialog); a tiny one elsewhere (a clock) does not.
        platform.OnClick = p => p.Desktop!.Bgra[(180 * 384 + 370) * 4] ^= 255;
        Assert.Contains("Nothing on screen changed", (await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 100, ["y"] = 50 }, ctx, CancellationToken.None)).Text);
        platform.OnClick = p => { for (var y = 120; y < 160; y++) for (var x = 300; x < 340; x++) p.Desktop!.Bgra[(y * 384 + x) * 4] ^= 255; };
        Assert.DoesNotContain("Nothing on screen changed", (await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 100, ["y"] = 50 }, ctx, CancellationToken.None)).Text);
    }

    [Fact]
    public void Small_screens_are_not_upscaled()
    {
        var space = new ScreenSpace(new ScreenRect(0, 0, 1024, 768), (1600, 900));
        Assert.Equal((1024, 768), (space.ImageWidth, space.ImageHeight));
    }

    [Fact]
    public async Task Click_maps_model_coordinates_to_screen_pixels()
    {
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1440) };
        // 2560x1440 screen sent at 1280x720 (704 after snapping to the 32px grid)
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotMaxWidth = 1280, ScreenshotMaxHeight = 720, ScreenshotAfterActions = false });

        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = "20", ["clicks"] = 2 }, ctx, CancellationToken.None);

        Assert.Equal(["move 20,41", "click Left x2"], platform.Actions);
    }

    [Fact]
    public async Task Screenshots_and_clicks_can_target_a_second_screen()
    {
        var platform = new FakePlatform { Screens = [new ScreenRect(0, 0, 200, 100), new ScreenRect(200, 0, 160, 100)] };
        var ctx = Ctx(platform);

        var first = await new ScreenshotTool().ExecuteAsync([], ctx, CancellationToken.None);
        Assert.Contains("screen 1 of 2", first.Text);
        Assert.Equal(new ScreenRect(0, 0, 200, 100), platform.Captures[^1]);

        var second = await new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = 2 }, ctx, CancellationToken.None);
        Assert.Contains("screen 2 of 2, 160x96", second.Text);
        Assert.Contains("cursor is on screen 1", second.Text);
        Assert.Equal(new ScreenRect(200, 0, 160, 100), platform.Captures[^1]);
        Assert.Equal(1, ctx.CurrentScreen);

        // From now on x/y are screen 2 coordinates: 160x100 is sent as 160x96, so y scales by 0.96.
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Contains("move 210,21", platform.Actions);

        await Assert.ThrowsAsync<ToolArgumentException>(() => new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = 3 }, ctx, CancellationToken.None));
    }

    [Fact]
    public async Task Screenshots_fit_the_max_size_and_clicks_land_on_the_real_screen_of_every_monitor()
    {
        // Screens come in physical pixels whatever each one's scaling: a 4K primary at 150%, a Full HD
        // one at 100% to its left, lower down, and an ultrawide on its right.
        var platform = new FakePlatform { Screens = [new ScreenRect(0, 0, 3840, 2160), new ScreenRect(-1920, 400, 1920, 1080), new ScreenRect(3840, 0, 2560, 1080)] };
        var ctx = Ctx(platform);
        Assert.Equal((2560, 1080), ctx.Settings.ScreenshotBox);

        async Task<string> Look(int screen) => (await new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = screen }, ctx, CancellationToken.None)).Text;
        async Task<string> Click(double x, double y)
        {
            await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = x, ["y"] = y }, ctx, CancellationToken.None);
            return platform.Actions.Last(a => a.StartsWith("move ", StringComparison.Ordinal));
        }

        // 4K is halved to 1920x1080 (1088 on the 32px grid); its middle and far corner map back.
        Assert.Contains("1920x1088", await Look(1));
        Assert.Equal("move 1920,1080", await Click(960, 544));
        Assert.Equal("move 3839,2159", await Click(1920, 1088));
        // The other two already fit: not scaled, and offset to where they are.
        Assert.Contains("1920x1088", await Look(2));
        Assert.Equal("move -960,940", await Click(960, 544));
        Assert.Equal("move -1920,400", await Click(0, 0));
        Assert.Contains("2560x1088", await Look(3));
        Assert.Equal("move 4940,496", await Click(1100, 500));

        // A smaller box: 4K fits 1280x720, and a click still lands where it was aimed.
        ctx.Settings.ScreenshotMaxWidth = 1280;
        ctx.Settings.ScreenshotMaxHeight = 720;
        Assert.Contains("1280x704", await Look(1));
        Assert.Equal("move 1920,1080", await Click(640, 352));
        Assert.Equal((1280, 544), (ctx.ScreenSpaces()[2].ImageWidth, ctx.ScreenSpaces()[2].ImageHeight));

        // A box larger than the screen never scales it up, and a hand-edited tiny or huge box is kept in range.
        ctx.Settings.ScreenshotMaxWidth = 99_999;
        ctx.Settings.ScreenshotMaxHeight = 99_999;
        Assert.Equal((3840, 2176), (ctx.ScreenSpaces()[0].ImageWidth, ctx.ScreenSpaces()[0].ImageHeight));
        ctx.Settings.ScreenshotMaxWidth = 0;
        ctx.Settings.ScreenshotMaxHeight = -5;
        Assert.Equal((640, 360), ctx.Settings.ScreenshotBox);
    }

    [Fact]
    public async Task Screens_left_of_and_above_the_primary_are_seen_and_clicked_where_they_are()
    {
        // Windows gives a screen left of or above the primary negative coordinates.
        var left = new ScreenRect(-160, 0, 160, 100);
        var above = new ScreenRect(0, -100, 200, 100);
        var platform = new FakePlatform { Screens = [new ScreenRect(0, 0, 200, 100), left, above] };
        var ctx = Ctx(platform);

        var shot = await new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = 2 }, ctx, CancellationToken.None);
        Assert.Equal(left, platform.Captures[^1]);
        Assert.Contains("screen 2 of 3", shot.Text);
        Assert.Contains("Mouse cursor is on screen 1", shot.Text);
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Contains("move -150,21", platform.Actions); // 160x100 is sent as 160x96
        Assert.Equal(1, ctx.ScreenAt(-150, 21));
        // A zoom near the screen's far edge stays on that screen.
        await new ZoomTool().ExecuteAsync(new JsonObject { ["x"] = 2, ["y"] = 90 }, ctx, CancellationToken.None);
        var zoomed = platform.Captures[^1];
        Assert.True(zoomed.X >= left.X && zoomed.X + zoomed.Width <= left.X + left.Width && zoomed.Y >= left.Y && zoomed.Y + zoomed.Height <= left.Y + left.Height, $"{zoomed}");

        await new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = 3 }, ctx, CancellationToken.None);
        Assert.Equal(above, platform.Captures[^1]);
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Contains("move 10,-79", platform.Actions); // 200x100 is sent as 192x96

        var prompt = Sloperator.Core.Agent.SystemPrompt.Build("Test OS", ctx.ScreenSpaces(), "C:\\", "");
        Assert.Contains("2 (left of screen 1, 160x100", prompt);
        Assert.Contains("3 (above screen 1, 200x100", prompt);
    }

    [Fact]
    public async Task Screens_of_different_sizes_and_scaling_each_map_on_their_own()
    {
        // A 4K laptop panel at 150% beside a 1080p monitor at 100%, lower down: Sloperator is per-monitor
        // DPI aware, so both come in physical pixels, and each screen's x/y map to its own pixels.
        var platform = new FakePlatform { Screens = [new ScreenRect(0, 0, 3840, 2160), new ScreenRect(3840, 600, 1920, 1080)] };
        var ctx = new ToolContext
        {
            Platform = platform,
            Settings = new AgentSettings { ScreenshotAfterActions = false },
            WorkingDirectory = Path.GetTempPath(),
        };
        var first = ctx.ScreenSpaces()[0];
        var second = ctx.ScreenSpaces()[1];

        // The middle of each screen's screenshot is the middle of that screen.
        await new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = 2 }, ctx, CancellationToken.None);
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = second.ImageWidth / 2, ["y"] = second.ImageHeight / 2 }, ctx, CancellationToken.None);
        Assert.Contains("move 4800,1140", platform.Actions);
        await new ScreenshotTool().ExecuteAsync(new JsonObject { ["screen"] = 1 }, ctx, CancellationToken.None);
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = first.ImageWidth / 2, ["y"] = first.ImageHeight / 2 }, ctx, CancellationToken.None);
        Assert.Contains("move 1920,1080", platform.Actions);

        var prompt = Sloperator.Core.Agent.SystemPrompt.Build("Test OS", ctx.ScreenSpaces(), "C:\\", "");
        Assert.Contains($"1 (primary, 3840x2160, screenshots {first.ImageWidth}x{first.ImageHeight})", prompt);
        Assert.Contains($"2 (right of screen 1, 1920x1080, screenshots {second.ImageWidth}x{second.ImageHeight})", prompt);
    }

    [Fact]
    public void Settings_take_away_the_screen_or_only_the_mouse_and_keyboard()
    {
        string[] look = ["screenshot", "zoom", "wait"], act = ["mouse_click", "mouse_move", "mouse_drag", "scroll", "type_text", "press_keys"];
        bool Has(ToolRegistry r, string name) => r.Find(name) is not null;

        var all = ToolRegistry.CreateDefault(includeScreenTools: true);
        Assert.All(look.Concat(act), n => Assert.True(Has(all, n), n));
        var looking = ToolRegistry.CreateDefault(includeScreenTools: true, includeControl: false);
        Assert.All(look, n => Assert.True(Has(looking, n), n));
        Assert.All(act, n => Assert.False(Has(looking, n), n));
        // No clicking blind: without screenshots there are no mouse and keyboard tools either.
        var blind = ToolRegistry.CreateDefault(includeScreenTools: false, includeControl: true);
        Assert.All(look.Concat(act), n => Assert.False(Has(blind, n), n));

        Assert.True(new AgentSettings().Controls);
        Assert.False(new AgentSettings { EnableControl = false }.Controls);
        Assert.False(new AgentSettings { EnableScreenshots = false, EnableControl = true }.Controls);

        ScreenSpace[] screens = [new(new ScreenRect(0, 0, 1920, 1080), (1920, 1080))];
        var lookOnly = Sloperator.Core.Agent.SystemPrompt.Build("Test OS", screens, "C:\\", "", control: false);
        Assert.Contains("not use the mouse or keyboard", lookOnly);
        Assert.DoesNotContain("Look before you click", lookOnly);
        Assert.Contains("Look before you click", Sloperator.Core.Agent.SystemPrompt.Build("Test OS", screens, "C:\\", ""));
    }

    [Fact]
    public void The_system_prompt_lists_the_screens_and_where_they_are()
    {
        var spaces = new List<ScreenSpace>
        {
            new(new ScreenRect(0, 0, 2560, 1080), (1920, 1080)),
            new(new ScreenRect(2560, 0, 1920, 1080), (1920, 1080)),
        };
        var prompt = Sloperator.Core.Agent.SystemPrompt.Build("Test OS", spaces, "C:\\", "");
        Assert.Contains("1 (primary, 2560x1080, screenshots 1920x800)", prompt);
        Assert.Contains("2 (right of screen 1, 1920x1080, screenshots 1920x1088)", prompt);
        Assert.Contains("one screen at a time", prompt);
        Assert.DoesNotContain("Screens:", Sloperator.Core.Agent.SystemPrompt.Build("Test OS", [spaces[0]], "C:\\", ""));
    }


    [Fact]
    public async Task Clicks_and_drags_can_hold_keys_like_a_person_selecting_things()
    {
        var platform = new FakePlatform();
        var click = new MouseClickTool();
        var args = new JsonObject { ["x"] = 10, ["y"] = 20, ["modifiers"] = new JsonArray("ctrl") };
        Assert.Equal("Ctrl+click at (10, 20)", click.Describe(args));

        await click.ExecuteAsync(args, Ctx(platform), CancellationToken.None);
        Assert.Equal(["move 10,21", "hold ctrl", "click Left x1", "release ctrl"], platform.Actions); // 200x100 is sent as 192x96

        platform.Actions.Clear();
        await new MouseDragTool().ExecuteAsync(new JsonObject { ["x"] = 0, ["y"] = 0, ["to_x"] = 50, ["to_y"] = 50, ["modifiers"] = "shift" }, Ctx(platform), CancellationToken.None);
        Assert.Equal("hold shift", platform.Actions[1]);
        Assert.Equal("down Left", platform.Actions[2]);
        Assert.Equal(["up Left", "release shift"], platform.Actions[^2..]);

        // A key name the platform doesn't know is caught before anything moves.
        platform.Actions.Clear();
        await Assert.ThrowsAsync<ToolArgumentException>(() => click.ExecuteAsync(new JsonObject { ["x"] = 1, ["y"] = 1, ["modifiers"] = new JsonArray("nokey") }, Ctx(platform), CancellationToken.None));
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task Press_keys_can_hold_keys_down()
    {
        var platform = new FakePlatform();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = new JsonArray("shift", "w"), ["hold_seconds"] = 0.3 }, Ctx(platform), CancellationToken.None);
        Assert.Equal(["hold shift+w", "release shift+w"], platform.Actions);
        Assert.True(started.ElapsedMilliseconds >= 280, $"held for {started.ElapsedMilliseconds} ms");
        Assert.Equal("Press shift+w, held 0.3s done.", result.Text);

        // Stopped mid-hold: the keys still come up.
        platform.Actions.Clear();
        using var stop = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "w", ["hold_seconds"] = 10 }, Ctx(platform), stop.Token));
        Assert.Equal(["hold w", "release w"], platform.Actions);
    }

    [Fact]
    public async Task Wait_can_stop_as_soon_as_the_screen_changes()
    {
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 320, 160), Desktop = new RawImage(320, 160, new byte[320 * 160 * 4]) };
        var ctx = Ctx(platform);
        var wait = new WaitTool();

        // A message arrives in a chat 300 ms in: a block of new pixels.
        var arrive = Task.Run(async () =>
        {
            await Task.Delay(300);
            for (var y = 100; y < 140; y++) for (var x = 20; x < 220; x++) platform.Desktop!.Bgra[(y * 320 + x) * 4 + 1] = 200;
        }, TestContext.Current.CancellationToken);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var changed = await wait.ExecuteAsync(new JsonObject { ["seconds"] = 60, ["until_change"] = true }, ctx, CancellationToken.None);
        await arrive;
        Assert.StartsWith("The screen changed after", changed.Text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.NotNull(changed.Image);

        // A tiny change (a blinking caret) isn't one.
        var caret = Task.Run(async () => { await Task.Delay(200); platform.Desktop!.Bgra[(10 * 320 + 10) * 4] = 255; }, TestContext.Current.CancellationToken);
        var quiet = await wait.ExecuteAsync(new JsonObject { ["seconds"] = 1.5, ["until_change"] = true }, ctx, CancellationToken.None);
        await caret;
        Assert.StartsWith("Waited 1.5s; nothing on the screen changed.", quiet.Text);

        // A message that arrived while the model was deciding to wait: the screen already differs from
        // the screenshot it decided on, so the wait ends at once rather than sleeping through it.
        ctx.Seen = (platform.ScreenBounds, new RawImage(320, 160, new byte[320 * 160 * 4]));
        clock.Restart();
        var already = await wait.ExecuteAsync(new JsonObject { ["seconds"] = 60, ["until_change"] = true }, ctx, CancellationToken.None);
        Assert.StartsWith("The screen had already changed since your last screenshot", already.Text);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");

        // The same screen as the one it decided on: it waits as before.
        ctx.Seen = (platform.ScreenBounds, platform.Screen.Capture(platform.ScreenBounds, includeCursor: false));
        Assert.StartsWith("Waited 1s; nothing on the screen changed.",
            (await wait.ExecuteAsync(new JsonObject { ["seconds"] = 1, ["until_change"] = true }, ctx, CancellationToken.None)).Text);
    }

    [Fact]
    public async Task Press_keys_accepts_plus_separated_strings()
    {
        var platform = new FakePlatform();
        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "ctrl+shift+esc" }, Ctx(platform), CancellationToken.None);
        Assert.Equal(["keys ctrl+shift+esc"], platform.Actions);
    }

    [Fact]
    public async Task Press_keys_plays_a_sequence_in_one_call()
    {
        var platform = new FakePlatform();
        var args = new JsonObject { ["sequence"] = new JsonArray("left", "down", "ctrl+s"), ["repeat"] = 2, ["gap_ms"] = 0 };
        await new PressKeysTool().ExecuteAsync(args, Ctx(platform), CancellationToken.None);
        Assert.Equal(["keys left", "keys down", "keys ctrl+s", "keys left", "keys down", "keys ctrl+s"], platform.Actions);
    }

    [Fact]
    public async Task Write_file_appends_parts()
    {
        var path = Path.Combine(Path.GetTempPath(), "bb-append-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var tool = new WriteFileTool();
            await tool.ExecuteAsync(new JsonObject { ["path"] = path, ["content"] = "one\n" }, Ctx(new FakePlatform()), CancellationToken.None);
            await tool.ExecuteAsync(new JsonObject { ["path"] = path, ["content"] = "two\n", ["append"] = true }, Ctx(new FakePlatform()), CancellationToken.None);
            await tool.ExecuteAsync(new JsonObject { ["path"] = path, ["content"] = "three\n", ["append"] = "true" }, Ctx(new FakePlatform()), CancellationToken.None);
            Assert.Equal("one\ntwo\nthree\n", File.ReadAllText(path));

            // A typo in the path is an error, not a new file in a new folder.
            var typo = Path.Combine(Path.GetTempPath(), "bb-typo-" + Guid.NewGuid().ToString("N"), "x.txt");
            var result = await tool.ExecuteAsync(new JsonObject { ["path"] = typo, ["content"] = "four\n", ["append"] = true }, Ctx(new FakePlatform()), CancellationToken.None);
            Assert.True(result.IsError);
            Assert.False(Directory.Exists(Path.GetDirectoryName(typo)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Screenshot_returns_jpeg_at_model_resolution()
    {
        var result = await new ScreenshotTool().ExecuteAsync([], Ctx(new FakePlatform()), CancellationToken.None);
        Assert.NotNull(result.Image);
        Assert.Equal(0xFF, result.Image![0]);
        Assert.Equal(0xD8, result.Image[1]);
        Assert.Contains("192x96", result.Text); // 200x100 snapped to the 32px grid
    }

    [Fact]
    public async Task Edit_file_requires_a_unique_match()
    {
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "one two two");
        var ctx = Ctx(new FakePlatform(), dir);
        var tool = new EditFileTool();

        var dup = await tool.ExecuteAsync(new JsonObject { ["path"] = "a.txt", ["old_text"] = "two", ["new_text"] = "2" }, ctx, CancellationToken.None);
        Assert.True(dup.IsError);

        var ok = await tool.ExecuteAsync(new JsonObject { ["path"] = "a.txt", ["old_text"] = "one", ["new_text"] = "1" }, ctx, CancellationToken.None);
        Assert.False(ok.IsError);
        Assert.Equal("1 two two", File.ReadAllText(file));
    }

    [Fact]
    public async Task Edit_file_matches_windows_line_endings_and_keeps_the_encoding()
    {
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var file = Path.Combine(dir, "Game.cs");
        // Saved by Visual Studio or Unity: a UTF-8 signature and \r\n, while the model writes \n.
        File.WriteAllText(file, "class Game\r\n{\r\n    int score;\r\n}\r\n", new System.Text.UTF8Encoding(true));
        var tool = new EditFileTool();

        var result = await tool.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "{\n    int score;\n}", ["new_text"] = "{\n    int score;\n    int best;\n}" }, Ctx(new FakePlatform(), dir), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        var bytes = File.ReadAllBytes(file);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Equal("class Game\r\n{\r\n    int score;\r\n    int best;\r\n}\r\n", System.Text.Encoding.UTF8.GetString(bytes[3..]));
    }

    [Fact]
    public async Task Files_in_the_ansi_code_page_or_utf16_are_read_edited_and_appended_to_as_they_are()
    {
        // Windows PowerShell's Set-Content writes ANSI and Out-File UTF-16: read as UTF-8, é and €
        // turned into U+FFFD all through the file, and edit_file wrote that back.
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        // Set-Content writes the PC's own ANSI code page: 1252 on an English Windows, 1251 on a
        // Russian one, which reads é as й. The first line is one that code page holds.
        var ansi = System.Text.Encoding.GetEncoding(0);
        var first = new[] { "Café 5 €", "Привет 5 €", "Γειά 5 €", "日本語 5" }.FirstOrDefault(s => ansi.GetString(ansi.GetBytes(s)) == s);
        Assert.SkipWhen(first is null, $"None of the sample lines fits code page {ansi.CodePage}.");
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var ctx = Ctx(new FakePlatform(), dir);
        var notes = Path.Combine(dir, "notes.txt");
        File.WriteAllBytes(notes, ansi.GetBytes(first + "\r\nTotal 5\r\n"));

        Assert.Contains(first, (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = notes }, ctx, CancellationToken.None)).Text);
        var edit = await new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = notes, ["old_text"] = "Total 5", ["new_text"] = "Total 6\nPaid" }, ctx, CancellationToken.None);
        Assert.False(edit.IsError, edit.Text);
        // Every other byte as it was, and the new line with the file's own line ending.
        Assert.Equal(ansi.GetBytes(first + "\r\nTotal 6\r\nPaid\r\n"), File.ReadAllBytes(notes));

        var list = Path.Combine(dir, "processes.txt");
        File.WriteAllText(list, "notepad\r\n", new System.Text.UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        await new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = list, ["content"] = "calc\r\n", ["append"] = true }, ctx, CancellationToken.None);
        Assert.Equal("notepad\r\ncalc\r\n", File.ReadAllText(list)); // one UTF-16 text, not UTF-8 bytes after UTF-16 ones
    }

    [Fact]
    public async Task A_file_another_program_is_writing_can_still_be_read()
    {
        // A running server's log: Windows refuses a plain read while another handle can write.
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var log = Path.Combine(dir, "server.log");
        await using var writer = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        await writer.WriteAsync("started on port 5000\n"u8.ToArray(), TestContext.Current.CancellationToken);
        await writer.FlushAsync(TestContext.Current.CancellationToken);

        var result = await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = log }, Ctx(new FakePlatform(), dir), CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("started on port 5000", result.Text);
    }

    [Fact]
    public async Task Edit_file_replaces_every_occurrence_when_asked()
    {
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "speed = speed + 1; // speed");
        var tool = new EditFileTool();

        var once = await tool.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "speed", ["new_text"] = "velocity" }, Ctx(new FakePlatform(), dir), CancellationToken.None);
        Assert.True(once.IsError);
        Assert.Contains("3 times", once.Text);

        var all = await tool.ExecuteAsync(new JsonObject { ["path"] = file, ["old_text"] = "speed", ["new_text"] = "velocity", ["replace_all"] = true }, Ctx(new FakePlatform(), dir), CancellationToken.None);
        Assert.Equal($"Edited {file}: replaced 3 occurrences.", all.Text);
        Assert.Equal("velocity = velocity + 1; // velocity", File.ReadAllText(file));
    }

    [Fact]
    public async Task Read_file_reads_a_part_of_a_long_file_and_turns_down_binary_ones()
    {
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var log = Path.Combine(dir, "big.log");
        File.WriteAllLines(log, Enumerable.Range(1, 100_000).Select(i => $"line {i}"));
        var tool = new ReadFileTool();

        var part = (await tool.ExecuteAsync(new JsonObject { ["path"] = log, ["offset"] = 50_000, ["limit"] = 2 }, Ctx(new FakePlatform(), dir), CancellationToken.None)).Text;
        Assert.Equal("50000\tline 50000\n50001\tline 50001\n[showing lines 50000-50001 of 100000; use offset to read more]\n", part.ReplaceLineEndings("\n"));
        var past = await tool.ExecuteAsync(new JsonObject { ["path"] = log, ["offset"] = 200_000 }, Ctx(new FakePlatform(), dir), CancellationToken.None);
        Assert.Equal("The file has only 100000 lines.", past.Text);

        // A picture is shown to the model (see ReadFileTests); other binaries are turned down.
        var data = Path.Combine(dir, "picture.bin");
        File.WriteAllBytes(data, TestPng);
        var binary = await tool.ExecuteAsync(new JsonObject { ["path"] = data }, Ctx(new FakePlatform(), dir), CancellationToken.None);
        Assert.True(binary.IsError);
        Assert.Contains("not a text file", binary.Text);

        // UTF-16 text has zero bytes too, but it is text.
        var wide = Path.Combine(dir, "wide.txt");
        File.WriteAllText(wide, "hello", System.Text.Encoding.Unicode);
        Assert.Equal("1\thello", (await tool.ExecuteAsync(new JsonObject { ["path"] = wide }, Ctx(new FakePlatform(), dir), CancellationToken.None)).Text.TrimEnd());
    }

    private static readonly byte[] TestPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    [Fact]
    public async Task A_directory_search_stops_on_stop()
    {
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ListDirectoryTool().ExecuteAsync(new JsonObject { ["path"] = dir, ["pattern"] = "*.nothing" }, Ctx(new FakePlatform(), dir), stopped.Token));

        var found = await new ListDirectoryTool().ExecuteAsync(new JsonObject { ["path"] = dir, ["pattern"] = "*.TXT" }, Ctx(new FakePlatform(), dir), CancellationToken.None);
        Assert.Contains("a.txt  (1 bytes)", found.Text);
    }

    [Fact]
    public async Task List_directory_takes_patterns_as_models_write_them_and_says_when_nothing_matches()
    {
        // "**/*.py" was matched against bare file names and never matched; hidden folders (.git)
        // were left out; either way the answer was the folder's name and nothing else.
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "src", "game"));
        File.WriteAllText(Path.Combine(dir, "src", "game", "main.py"), "x");
        var hidden = Directory.CreateDirectory(Path.Combine(dir, ".git"));
        hidden.Attributes |= FileAttributes.Hidden;
        File.WriteAllText(Path.Combine(hidden.FullName, "config"), "x");
        var ctx = Ctx(new FakePlatform(), dir);
        async Task<string> List(string? pattern) => (await new ListDirectoryTool().ExecuteAsync(
            pattern is null ? new JsonObject { ["path"] = dir } : new JsonObject { ["path"] = dir, ["pattern"] = pattern }, ctx, CancellationToken.None)).Text;

        Assert.Contains(Path.Combine("src", "game", "main.py"), await List("**/*.py"));
        Assert.Contains(Path.Combine("src", "game", "main.py"), await List("src/**/*.py"));
        Assert.Contains(".git" + Path.DirectorySeparatorChar, await List(null));
        Assert.Contains("(nothing matches *.rs here or in the folders under it)", await List("*.rs"));
        Assert.Contains(Path.Combine("src", "game", "main.py"), await List("**"));
        Assert.Null(ListDirectoryTool.Glob("  "));
    }

    [Fact]
    public async Task A_folder_the_account_cant_read_isnt_listed_as_empty()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Uses Windows access rules.");
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var locked = Directory.CreateDirectory(Path.Combine(dir, "locked"));
        File.WriteAllText(Path.Combine(locked.FullName, "secret.txt"), "x");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var rules = locked.GetAccessControl();
        rules.AddAccessRule(deny);
        locked.SetAccessControl(rules);
        try
        {
            var ctx = Ctx(new FakePlatform(), dir);
            var listed = await new ListDirectoryTool().ExecuteAsync(new JsonObject { ["path"] = locked.FullName }, ctx, CancellationToken.None);
            Assert.True(listed.IsError);
            Assert.Contains("Permission denied", listed.Text);
            var searched = await new ListDirectoryTool().ExecuteAsync(new JsonObject { ["path"] = locked.FullName, ["pattern"] = "*.txt" }, ctx, CancellationToken.None);
            Assert.Contains("Permission denied", searched.Text);
            // Searching from above it still skips it quietly, as before.
            var above = await new ListDirectoryTool().ExecuteAsync(new JsonObject { ["path"] = dir, ["pattern"] = "*.txt" }, ctx, CancellationToken.None);
            Assert.False(above.IsError);
        }
        finally
        {
            rules.RemoveAccessRule(deny);
            locked.SetAccessControl(rules);
        }
    }

    [Fact]
    public void Settings_from_the_local_model_versions_are_migrated_and_cleaned()
    {
        var root = Directory.CreateTempSubdirectory("bb-settings-").FullName;
        var paths = new AppPaths(root);
        Directory.CreateDirectory(paths.DataDir);
        File.WriteAllText(paths.SettingsFile, """
            {
              "backend": "Remote",
              "local": { "modelPath": "Models/x.gguf", "gpuLayers": 99 },
              "remote": { "baseUrl": "http://host:1234/v1", "model": "qwen", "sendExtras": false, "maxPromptTokens": 65536 },
              "agent": { "enableThinking": false, "temperature": 0.3, "maxSteps": 12, "customInstructions": "be brief", "keepScreenshots": 5, "workingDirectory": "D:\\x", "screenshotMaxPixels": 2000000,
                         "screenshotResolution": "R1280x720", "showInputs": false, "inputLogSeconds": 3, "webSearch": false }
            }
            """);

        var settings = new SettingsStore(paths).Load();

        Assert.Equal("http://host:1234/v1", settings.Server.BaseUrl);
        Assert.Equal("qwen", settings.Server.Model);
        Assert.Equal("be brief", settings.Agent.CustomInstructions);
        // Web access and the click log are no longer settings: always on and shown. The old screenshot
        // presets are gone too, and the size box starts from its default.
        Assert.Equal((2560, 1080), settings.Agent.ScreenshotBox);
        var saved = File.ReadAllText(paths.SettingsFile);
        foreach (var gone in new[] { "backend", "local", "remote", "gpuLayers", "sendExtras", "enableThinking", "temperature", "keepScreenshots", "contextSize", "workingDirectory", "screenshotMaxPixels", "maxSteps", "maxPromptTokens",
                                     "screenshotResolution", "showInputs", "inputLogSeconds", "webSearch" })
            Assert.DoesNotContain(gone, saved);
    }

    [Fact]
    public async Task Real_screen_capture_on_windows()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        IPlatformServices platform = new UnsupportedPlatform();
#else
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
#endif
        var screen = platform.Screen.GetScreens()[0];
        Assert.True(screen.Width >= 640 && screen.Height >= 480);

        var result = await new ScreenshotTool().ExecuteAsync([], Ctx(platform), CancellationToken.None);
        var space = new ScreenSpace(screen, new AgentSettings().ScreenshotBox);
        Assert.Contains($"{space.ImageWidth}x{space.ImageHeight}", result.Text);
        Assert.True(result.Image!.Length > 5_000);
    }

    [Fact]
    public void All_monitors_are_listed_on_windows()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
#else
        var capture = new Sloperator.Platform.Windows.WindowsPlatform().Screen;
        var screens = capture.GetScreens();
        Assert.Equal((0, 0), (screens[0].X, screens[0].Y)); // the primary, which Windows puts at the origin
        // Every monitor can be captured, not just the primary.
        foreach (var screen in screens)
            Assert.Equal(screen.Width * screen.Height * 4, capture.Capture(screen, includeCursor: false).Bgra.Length);
#endif
    }

    [Fact]
    public async Task Pause_or_Break_stops_a_run_but_Esc_other_keys_and_the_agents_own_presses_do_not()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        await Task.CompletedTask;
#else
        var ct = TestContext.Current.CancellationToken;
        using var past = new KeysPastTheStopKey();
        var stops = 0;
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
        // A run is going the whole time: every press of the stop key stops one.
        using var watch = platform.StopKey.Watch(() => { Interlocked.Increment(ref stops); return true; });

        platform.Input.PressKeys(["pause"]); // the agent's own press is tagged and ignored
        UserKeys((Esc, false), (Esc, true), (LWin, false), (LWin, true)); // Esc and the Windows key, the stop keys before, are the user's again
        await Task.Delay(300, ct);
        Assert.Equal(0, Volatile.Read(ref stops));
        Assert.Equal("pause↓ pause↑ esc↓ esc↑ win↓ win↑", past.Take());

        // Pause, and Break (what Windows reports for Ctrl+Pause): each stops the run, and goes no further.
        UserKeys((Pause, false), (Pause, true));
        await Until(() => Volatile.Read(ref stops) == 1, ct);
        UserKeys((Break, false), (Break, true));
        await Until(() => Volatile.Read(ref stops) == 2, ct);
        await Task.Delay(100, ct);
        Assert.Equal("", past.Take());
#endif
    }

    [Fact]
    public async Task A_pause_press_that_stops_a_run_goes_no_further()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        await Task.CompletedTask;
#else
        var ct = TestContext.Current.CancellationToken;
        using var past = new KeysPastTheStopKey();
        var running = 1;
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
        using var watch = platform.StopKey.Watch(() => Interlocked.Exchange(ref running, 0) == 1);

        // Pause during a run stops it, and none of it gets past: not its repeats or its key-up, nor a
        // key pressed while it is held. The agent's own keys still do.
        UserKeys((Pause, false));
        await Task.Delay(100, ct);
        Assert.Equal(0, Volatile.Read(ref running));
        UserKeys((F24, false), (Pause, false), (F24, false));
        platform.Input.PressKeys(["f23"]);
        UserKeys((F24, true), (Pause, true));
        await Task.Delay(100, ct);
        Assert.Equal("f23↓ f23↑", past.Take());

        // With no run going it is the usual Pause.
        UserKeys((Pause, false), (Pause, true));
        await Task.Delay(100, ct);
        Assert.Equal("pause↓ pause↑", past.Take());

        // A stop press whose key-up the hook never sees (focus went to an elevated window, say)
        // holds the keyboard for a moment only; its late key-up still goes no further.
        Interlocked.Exchange(ref running, 1);
        UserKeys((Pause, false));
        await Task.Delay(2700, ct);
        UserKeys((F24, false), (F24, true), (Pause, true));
        await Task.Delay(100, ct);
        Assert.Equal("f24↓ f24↑", past.Take());
#endif
    }

    [Fact]
    public async Task Held_keys_stay_down_on_windows_until_let_go()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        await Task.CompletedTask;
#else
        // F24: on every keyboard layout, and no app reacts to it. Games read the key state, which is what holding sets.
        static bool Down() => (GetAsyncKeyState(0x87) & 0x8000) != 0;
        var input = new Sloperator.Platform.Windows.WindowsPlatform().Input;
        using (input.HoldKeys(["f24"]))
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(Down());
        }
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(Down());
#endif
    }

#if WINDOWS
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private const int Break = 0x03, Esc = 0x1B, Pause = 0x13, LWin = 0x5B, F23 = 0x86, F24 = 0x87;

    /// <summary>Waits up to 3 s for <paramref name="done"/>, and fails if it never is.</summary>
    private static async Task Until(Func<bool> done, CancellationToken ct)
    {
        for (var waited = 0; !done() && waited < 3000; waited += 20)
            await Task.Delay(20, ct);
        Assert.True(done());
    }

    /// <summary>Key events like the user's: injected without Sloperator's tag.</summary>
    private static void UserKeys(params (int Vk, bool Up)[] keys)
    {
        var inputs = keys.Select(k => new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            u = new() { ki = new() { wVk = (ushort)k.Vk, dwFlags = k.Up ? Native.KEYEVENTF_KEYUP : 0 } },
        }).ToArray();
        Native.SendInput((uint)inputs.Length, inputs, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>
    /// Records the test's keys that get past the stop key's hook, and keeps them from every window,
    /// so the tests press nothing on this PC. Create it before the stop key: the newest hook is
    /// called first, so this one comes after the stop key's.
    /// </summary>
    private sealed class KeysPastTheStopKey : IDisposable
    {
        private const uint LLKHF_INJECTED = 0x10;
        private readonly Native.LowLevelKeyboardProc _proc; // kept alive for as long as the hook is installed
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _seen = new();
        private uint _threadId;

        public KeysPastTheStopKey()
        {
            _proc = Callback;
            using var ready = new ManualResetEventSlim();
            new Thread(() =>
            {
                _threadId = Native.GetCurrentThreadId();
                var hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _proc, Native.GetModuleHandle(null), 0);
                ready.Set();
                while (Native.GetMessage(out _, 0, 0, 0) > 0) { }
                Native.UnhookWindowsHookEx(hook);
            }) { IsBackground = true }.Start();
            ready.Wait(TimeSpan.FromSeconds(5));
        }

        private nint Callback(int code, nint wParam, nint lParam)
        {
            if (code >= 0)
            {
                var key = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
                // Only what the tests press: the keyboard itself stays usable.
                if ((key.flags & LLKHF_INJECTED) != 0 && Name((int)key.vkCode) is { } name)
                {
                    _seen.Enqueue(name + ((uint)wParam is Native.WM_KEYUP or Native.WM_SYSKEYUP ? "↑" : "↓"));
                    return 1;
                }
            }
            return Native.CallNextHookEx(0, code, wParam, lParam);
        }

        private static string? Name(int vk) => vk switch
        {
            // Break too: one that reached a console would be Ctrl+Break, which ends what runs in it.
            Break => "break", Esc => "esc", Pause => "pause", LWin => "win", F23 => "f23", F24 => "f24", _ => null,
        };

        /// <summary>What got past since the last call, like "esc↓ esc↑".</summary>
        public string Take()
        {
            var seen = new List<string>();
            while (_seen.TryDequeue(out var s))
                seen.Add(s);
            return string.Join(" ", seen);
        }

        public void Dispose() => Native.PostThreadMessage(_threadId, Native.WM_QUIT, 0, 0);
    }
#endif

    [Fact]
    public async Task Shell_on_windows_returns_utf8_output()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        IPlatformServices platform = new UnsupportedPlatform();
#else
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
#endif
        var result = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Write-Output \"héllo 'quoted' wörld\"; exit 3" }, Ctx(platform), CancellationToken.None);

        Assert.Contains("héllo 'quoted' wörld", result.Text);
        Assert.Contains("Exit code 3", result.Text);
        Assert.True(result.IsError);

        // Errors come back as text, not as PowerShell's CLIXML.
        var failed = await new ShellTool().ExecuteAsync(
            new JsonObject { ["command"] = "Write-Output before; Write-Error 'no such <thing>'; Write-Output after" }, Ctx(platform), CancellationToken.None);
        Assert.DoesNotContain("CLIXML", failed.Text);
        Assert.DoesNotContain("_x000D_", failed.Text);
        Assert.Contains("no such <thing>", failed.Text);
        Assert.Contains("after", failed.Text);

        // Bytes of an image printed to the console come back as a one-line note, not 30,000 characters of noise.
        var binary = await new ShellTool().ExecuteAsync(new JsonObject
        {
            ["command"] = "'before'; $b = New-Object byte[] 20000; (New-Object Random 3).NextBytes($b); $o = [Console]::OpenStandardOutput(); " +
                          "$o.Write($b, 0, $b.Length); $o.Flush(); [Console]::Out.WriteLine(); 'after'",
        }, Ctx(platform), CancellationToken.None);
        Assert.Contains("before", binary.Text);
        Assert.Contains("[binary data left out:", binary.Text);
        Assert.Contains("after", binary.Text);
        Assert.True(binary.Text.Length < 1_000, binary.Text);
    }

    // ffmpeg writes its progress to stderr: taken in with 2>&1, Windows PowerShell 5.1 counted it as an
    // error, and every ffmpeg call that worked came back as "Exit code 1".
    [Fact]
    public async Task A_programs_messages_on_stderr_are_no_failure_and_its_own_exit_code_comes_back()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        IPlatformServices platform = new UnsupportedPlatform();
#else
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
#endif
        async Task<ToolResult> Run(string command) =>
            await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = command }, Ctx(platform), TestContext.Current.CancellationToken);

        var progress = await Run("cmd /c \"echo frame=120 speed=74x 1>&2\" 2>&1 | Select-Object -Last 1");
        Assert.False(progress.IsError, progress.Text);
        Assert.Contains("Exit code 0", progress.Text);
        Assert.Contains("frame=120", progress.Text);

        // A program that failed gives its own code; a cmdlet that failed, 1 as before.
        Assert.Contains("Exit code 5", (await Run("cmd /c \"echo bad input 1>&2 & exit 5\" 2>&1 | Select-Object -Last 1")).Text);
        Assert.Contains("Exit code 128", (await Run("cmd /c exit 128")).Text);
        var cmdlet = await Run("cmd /c \"echo fine 1>&2\" 2>&1; Get-Item 'C:\\no such folder here'");
        Assert.True(cmdlet.IsError, cmdlet.Text);
        Assert.Contains("Exit code 1", cmdlet.Text);
    }

    [Fact]
    public async Task A_powershell_script_written_with_write_file_runs_in_windows_powershell()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        IPlatformServices platform = new UnsupportedPlatform();
#else
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
#endif
        // Without a signature, Windows PowerShell reads a script in the ANSI code page, where the
        // em dash's last byte is a closing quote: "The string is missing the terminator".
        var dir = Directory.CreateTempSubdirectory("bb-ps1-").FullName;
        try
        {
            var ctx = Ctx(platform, dir);
            Assert.False((await new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = "done.ps1", ["content"] = "Write-Output \"Done — copied ✓\"\n" }, ctx, TestContext.Current.CancellationToken)).IsError);
            var run = await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{Path.Combine(dir, "done.ps1")}\"" }, ctx, TestContext.Current.CancellationToken);
            Assert.False(run.IsError, run.Text);
            Assert.Contains("Done — copied ✓", run.Text);

            // A script begun in ASCII gets the signature once an edit or a part added takes it past ASCII.
            await File.WriteAllTextAsync(Path.Combine(dir, "plain.ps1"), "Write-Output 'start'\n", TestContext.Current.CancellationToken);
            Assert.False((await new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = "plain.ps1", ["old_text"] = "start", ["new_text"] = "start — now" }, ctx, TestContext.Current.CancellationToken)).IsError);
            Assert.Equal([0xEF, 0xBB, 0xBF], (await File.ReadAllBytesAsync(Path.Combine(dir, "plain.ps1"), TestContext.Current.CancellationToken))[..3]);
            await File.WriteAllTextAsync(Path.Combine(dir, "parts.ps1"), "Write-Output 'one'\n", TestContext.Current.CancellationToken);
            Assert.False((await new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = "parts.ps1", ["content"] = "Write-Output 'two — ✓'\n", ["append"] = true }, ctx, TestContext.Current.CancellationToken)).IsError);
            var parts = await File.ReadAllBytesAsync(Path.Combine(dir, "parts.ps1"), TestContext.Current.CancellationToken);
            Assert.Equal([0xEF, 0xBB, 0xBF], parts[..3]);
            Assert.Equal("Write-Output 'one'\nWrite-Output 'two — ✓'\n", System.Text.Encoding.UTF8.GetString(parts, 3, parts.Length - 3));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task A_command_too_long_for_a_windows_command_line_runs_all_the_same()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        IPlatformServices platform = new UnsupportedPlatform();
#else
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
#endif
        // Encoded for -EncodedCommand, a script of 20,000 characters is over the 32,767 Windows
        // takes on a command line: it failed with "The filename or extension is too long".
        var filler = "$x = '" + new string('a', 20_000) + "'\n";
        var before = Directory.GetFiles(Path.GetTempPath(), "sloperator-command-*.ps1").Length;
        var result = await new ShellTool().ExecuteAsync(new JsonObject
        {
            ["command"] = filler + "Write-Output \"length $($x.Length), wörld\"; exit 3",
        }, Ctx(platform), TestContext.Current.CancellationToken);
        Assert.Contains("length 20000, wörld", result.Text);
        Assert.Contains("Exit code 3", result.Text);
        // Its last command failing fails it, as with -Command; the file it ran from is gone.
        var failed = await new ShellTool().ExecuteAsync(new JsonObject
        {
            ["command"] = filler + "Get-Item 'C:\\no such folder here'",
        }, Ctx(platform), TestContext.Current.CancellationToken);
        Assert.True(failed.IsError, failed.Text);
        Assert.Contains("Exit code 1", failed.Text);
        var ok = await new ShellTool().ExecuteAsync(new JsonObject { ["command"] = filler + "'fine'" }, Ctx(platform), TestContext.Current.CancellationToken);
        Assert.False(ok.IsError, ok.Text);
        Assert.Equal(before, Directory.GetFiles(Path.GetTempPath(), "sloperator-command-*.ps1").Length);
    }

    [Fact]
    public void A_path_that_starts_with_a_tilde_is_in_the_home_folder()
    {
        // "~/Desktop/report.txt" made a folder named "~" in the working directory.
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(home, "Desktop", "report.txt"), ctx.ResolvePath("~/Desktop/report.txt"));
        Assert.Equal(Path.Combine(home, "notes.txt"), ctx.ResolvePath(@"~\notes.txt"));
        Assert.Equal(Path.GetFullPath(home), ctx.ResolvePath("~"));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "~notes.txt"), ctx.ResolvePath("~notes.txt"));
    }

    [Fact]
    public void Long_output_keeps_its_beginning_and_its_end_and_long_lines_are_cut()
    {
        // npm or pip print pages of progress and then the error, which was the part cut off.
        var output = new ShellTool.OutputCapture(30_000);
        for (var i = 1; i <= 3000; i++)
            output.Add($"progress line {i} of the install");
        output.Add("ERROR: the build failed");
        var text = output.ToString();
        Assert.StartsWith("progress line 1 of the install\n", text);
        Assert.EndsWith("ERROR: the build failed\n", text);
        Assert.Contains("lines (", text);
        Assert.True(text.Length < 31_000, $"{text.Length} characters");

        // A minified file or a base64 dump on one line came back whole and filled the context.
        var one = new ShellTool.OutputCapture(30_000);
        one.Add(new string('x', 200_000));
        Assert.True(one.ToString().Length < 5_000, $"{one.ToString().Length} characters");
        Assert.Contains("characters of this line left out", one.ToString());
    }

    [Fact]
    public async Task A_command_that_leaves_a_program_running_returns_and_leaves_it_running()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        IPlatformServices platform = new UnsupportedPlatform();
#else
        var platform = new Sloperator.Platform.Windows.WindowsPlatform();
#endif
        // Start-Process -NoNewWindow hands the output pipes on: the command waited out its timeout,
        // said it had timed out, and killed the program it had started.
        var marker = Path.Combine(Path.GetTempPath(), $"bb-left-running-{Guid.NewGuid():N}.txt");
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = await new ShellTool().ExecuteAsync(new JsonObject
        {
            ["command"] = "Start-Process powershell -NoNewWindow -ArgumentList '-NoProfile','-Command',\"Start-Sleep 12; Set-Content '" + marker + "' done\"; 'started'",
            ["timeout_seconds"] = 10,
        }, Ctx(platform), TestContext.Current.CancellationToken);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}");
        Assert.Contains("started", result.Text);
        Assert.DoesNotContain("Timed out", result.Text);
        // What it started finishes its own work.
        for (var i = 0; i < 30 && !File.Exists(marker); i++)
            await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(marker), "the program the command started was killed");
        File.Delete(marker);
    }
}

public class OverlayInputTests
{
    private sealed class RecordingOverlay : IInputOverlay
    {
        public bool Enabled { get; set; } = true;
        public TimeSpan Duration { get; set; }
        public (byte R, byte G, byte B) MarkerColor { get; set; }
        public List<(string Text, (int X, int Y)? Point)> Shown { get; } = [];
        public void Show(string text, (int X, int Y)? point = null) => Shown.Add((text, point));
    }

    [Fact]
    public void Inputs_are_described_in_plain_words_with_click_positions()
    {
        var platform = new FakePlatform();
        var overlay = new RecordingOverlay();
        var input = new OverlayInputController(platform, overlay);

        input.MoveMouse(40, 30);
        input.Click(MouseButton.Left, 2);
        input.PressKeys(["ctrl", "shift", "s"]);
        input.PressKeys(["left"]);
        input.TypeText("hello\nworld, this is a long line that keeps going and going");
        input.Scroll(0, 3);
        input.HoldKeys(["ctrl"]).Dispose();

        Assert.Equal("Left double click", overlay.Shown[0].Text);
        Assert.Equal((40, 30), overlay.Shown[0].Point);
        Assert.Equal("Keys Ctrl+Shift+S", overlay.Shown[1].Text);
        Assert.Equal("Keys ←", overlay.Shown[2].Text);
        Assert.Equal("Type “hello⏎world, this is a long line that ke…”", overlay.Shown[3].Text);
        Assert.Null(overlay.Shown[3].Point);
        Assert.Equal("Scroll down 3", overlay.Shown[4].Text);
        Assert.Equal("Hold Ctrl", overlay.Shown[5].Text);
        Assert.Contains("click Left x2", platform.Actions); // the real input still happened
        Assert.Equal(["hold ctrl", "release ctrl"], platform.Actions[^2..]);
    }
}
