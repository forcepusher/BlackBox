using System.Text.Json.Nodes;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;
#if WINDOWS
using Native = BlackBox.Platform.Windows.Native;
#endif

namespace BlackBox.Core.Tests;

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
    public void A_point_given_as_fractions_of_the_screenshot_lands_where_it_means()
    {
        // Kilo's free Nemotron answered (0.8711, 0.8833) for a button at (1110, 635) on a 1280x720
        // screenshot, though asked for pixels; and fractions on the grid too.
        var pixels = new ScreenSpace(new ScreenRect(0, 0, 1280, 720), null);
        Assert.Equal((1115, 636), pixels.ToScreen(0.8711, 0.8833));
        var grid = new ScreenSpace(new ScreenRect(0, 0, 2560, 1080), null, grid: 1000);
        Assert.Equal((1456, 199), grid.ToScreen(0.5688, 0.1843));
        // Whole numbers are points as they are, however near the corner.
        Assert.Equal((1, 1), pixels.ToScreen(1, 1));
        Assert.Equal((0, 0), pixels.ToScreen(0, 0));
    }

    [Fact]
    public async Task Qwen3_models_point_on_a_0_to_1000_grid()
    {
        Assert.Equal(1000, ModelCoordinates.GridFor("qwen3.8-27b@q6_k_xl"));
        Assert.Equal(1000, ModelCoordinates.GridFor("Qwen/Qwen3-VL-8B-Instruct"));
        Assert.Null(ModelCoordinates.GridFor("qwen2.5-vl-7b"));
        // Gemma 4 points on the same grid (its Save As clicks matched it to within 15 steps).
        Assert.Equal(1000, ModelCoordinates.GridFor("gemma-4-31b-it"));
        Assert.Equal(1000, ModelCoordinates.GridFor("google/gemma-4-12b-it-qat"));
        Assert.Null(ModelCoordinates.GridFor("gemma-3-27b-it"));
        Assert.Null(ModelCoordinates.GridFor("llava-1.6-mistral-7b"));
        // So does Nemotron: Kilo's free Super VL hit 12 of 16 targets on the grid, 0 of 4 asked for pixels.
        Assert.Equal(1000, ModelCoordinates.GridFor("nvidia/nemotron-3.5-super-vl:free"));

        // The grid spans the whole screen whatever the image size: 500,500 is the middle of a 2560x1080 screen.
        var space = new ScreenSpace(new ScreenRect(0, 0, 2560, 1080), (1920, 1080), grid: 1000);
        Assert.Equal((1920, 800), (space.ImageWidth, space.ImageHeight));
        Assert.Equal((1280, 540), space.ToScreen(500, 500));
        Assert.Equal((500, 500), space.ToModel(1280, 540));
        Assert.Equal((2559, 1079), space.ToScreen(1000, 1000));

        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1080) };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = Path.GetTempPath(), CoordinateGrid = 1000 };
        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 250, ["y"] = 100 }, ctx, CancellationToken.None);
        Assert.Equal("move 640,108", platform.Actions[0]);
        var shot = await new ScreenshotTool().ExecuteAsync([], ctx, CancellationToken.None);
        Assert.Equal("Screenshot. Mouse cursor at (250, 100).", shot.Text);

        var prompt = BlackBox.Core.Agent.SystemPrompt.Build("Test OS", ctx.ScreenSpaces(), "C:\\", "");
        Assert.Contains("0-1000 grid", prompt);
        Assert.DoesNotContain("1920x800", prompt);
        // Grid steps aren't square: a circle drawn with as many x as y steps came out an oval.
        Assert.Contains("one x step is 2.56 px and one y step 1.08 px, so something as tall as it is wide spans 2.37 times as many y steps as x steps", prompt);
        var drag = await new MouseDragTool().ExecuteAsync(new JsonObject { ["x"] = 100, ["y"] = 100, ["to_x"] = 200, ["to_y"] = 200 }, ctx, CancellationToken.None);
        Assert.Equal("Dragged 256 x 108 px on screen.", drag.Text);
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
    public async Task A_click_goes_where_a_fresh_look_finds_the_target()
    {
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1440) };
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotResolution = ScreenshotResolution.R1280x720, ScreenshotAfterActions = false });
        string? looked = null;
        (double X, double Y)? found = (300, 120);
        ctx.Locate = (_, _, target, _) => { looked = target; return Task.FromResult(found); };
        var click = new MouseClickTool();

        // Found: the model's own x/y are ignored, and it is told where the click went instead.
        var result = await click.ExecuteAsync(new JsonObject { ["target"] = "Save button", ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Equal("Save button", looked);
        Assert.Equal("move 600,245", platform.Actions[0]);
        Assert.StartsWith("Clicked.\nA fresh look found the Save button at (300, 120), not where you aimed (10, 20), and the click went there.", result.Text);
        Assert.Contains("click again with target left out", result.Text);

        // Not found (or off the screen): the model's x/y, and it is told.
        found = null;
        result = await click.ExecuteAsync(new JsonObject { ["target"] = "Save button", ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Equal("move 20,41", platform.Actions[2]);
        Assert.StartsWith("BlackBox could not find the Save button on the screen by name, so it clicked at your x, y.", result.Text);
        found = (5000, 20);
        await click.ExecuteAsync(new JsonObject { ["target"] = "Save button", ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Equal("move 20,41", platform.Actions[4]);
        // Not found and no x/y: an error for the model, not an argument problem (no parameter list).
        await Assert.ThrowsAsync<InvalidOperationException>(() => click.ExecuteAsync(new JsonObject { ["target"] = "Save button" }, ctx, CancellationToken.None));

        // No target: exactly x/y, without a look.
        looked = null;
        await click.ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Null(looked);
        Assert.Equal("move 20,41", platform.Actions[6]);

        Assert.Equal((412, 87.5), BlackBox.Core.Agent.FreshLook.Parse("The centre is at (412, 87.5)."));
        Assert.Null(BlackBox.Core.Agent.FreshLook.Parse("none"));
    }

    /// <summary>
    /// A look that finds the target at a screen point when the picture it is shown covers it, and
    /// answers in that picture's coordinates, as the model does; it says none otherwise.
    /// </summary>
    private static Func<byte[], ScreenSpace, string, CancellationToken, Task<(double X, double Y)?>> LookFinds(Func<(int X, int Y)?> at, List<(ScreenSpace Space, byte[] Image)>? shown = null) =>
        (image, space, _, _) =>
        {
            shown?.Add((space, image));
            var s = space.Screen;
            return Task.FromResult<(double X, double Y)?>(at() is { } p && s.Contains(p.X, p.Y)
                ? ((p.X - s.X) * (double)space.ModelWidth / s.Width, (p.Y - s.Y) * (double)space.ModelHeight / s.Height)
                : null);
        };

    [Fact]
    public async Task A_click_the_look_moved_away_from_the_aim_says_where_it_went()
    {
        // Blender's sidebar tabs on the 0-1000 grid of a 2560x1080 screen: the model aimed at the
        // "MCP for Blender" tab, the look found the Animation tab above it (76 px higher).
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1080) };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = Path.GetTempPath(), CoordinateGrid = 1000 };
        (int X, int Y)? found = (2084, 254);
        ctx.Locate = LookFinds(() => found);
        var click = new MouseClickTool();
        var args = new JsonObject { ["target"] = "MCP for Blender tab in sidebar", ["x"] = 811, ["y"] = 305 };

        var result = await click.ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Equal("move 2084,254", platform.Actions[0]);
        Assert.Equal("Clicked.\nA fresh look found the MCP for Blender tab in sidebar at (814, 235), not where you aimed (811, 305), and the click went there. " +
                     "If the mouse pointer in the screenshot is not on what you meant, click again with target left out and x, y on it: a look would find the same thing again.",
            result.Text);

        // A small correction (a few pixels within the same button) is just a click.
        found = (2079, 335);
        Assert.Equal("Clicked.", (await click.ExecuteAsync(args, ctx, CancellationToken.None)).Text);

        // A click that changed nothing gets the miss note, which already says to aim without the look.
        var still = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1080), Desktop = new RawImage(2560, 1080, new byte[2560 * 1080 * 4]) };
        ctx = new ToolContext { Platform = still, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), CoordinateGrid = 1000 };
        found = (2084, 254);
        ctx.Locate = LookFinds(() => found);
        var missed = await click.ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Contains("Nothing on screen changed after this click", missed.Text);
        Assert.DoesNotContain("A fresh look found", missed.Text);
    }

    [Fact]
    public async Task A_look_is_shown_the_quarter_of_the_screen_around_the_aim_first()
    {
        // Reading a whole 2560x1080 screenshot took a 27B model 9 s; a quarter of it 3 s.
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1080) };
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings { ScreenshotAfterActions = false }, WorkingDirectory = Path.GetTempPath(), CoordinateGrid = 1000 };
        var shown = new List<(ScreenSpace Space, byte[] Image)>();
        (int X, int Y)? target = (2084, 254);
        ctx.Locate = LookFinds(() => target, shown);
        var click = new MouseClickTool();

        await click.ExecuteAsync(new JsonObject { ["target"] = "MCP for Blender tab", ["x"] = 811, ["y"] = 305 }, ctx, CancellationToken.None);
        var (part, image) = Assert.Single(shown);
        Assert.Equal(new ScreenRect(1280, 59, 1280, 540), part.Screen); // centred on the aim, (2076, 329), kept inside the screen
        using (var codec = SkiaSharp.SKCodec.Create(new MemoryStream(image)))
            Assert.Equal((1280, 544), (codec.Info.Width, codec.Info.Height));
        Assert.Equal("move 2084,254", platform.Actions[0]);

        // Not around the aim (the model's x, y were far off): the whole screen is looked at next.
        shown.Clear();
        platform.Actions.Clear();
        target = (100, 30);
        await click.ExecuteAsync(new JsonObject { ["target"] = "File menu", ["x"] = 811, ["y"] = 305 }, ctx, CancellationToken.None);
        Assert.Equal([new ScreenRect(1280, 59, 1280, 540), new ScreenRect(0, 0, 2560, 1080)], shown.Select(s => s.Space.Screen));
        Assert.Equal("move 100,30", platform.Actions[0]);

        // A scaled-down screen keeps its scale in the part: 2560x1440 in a 1280x720 box shows a 1280x720 part as 640x352.
        var scaled = new ScreenSpace(new ScreenRect(0, 0, 2560, 1440), (1280, 720));
        var quarter = scaled.Part(scaled.Around(2500, 30));
        Assert.Equal(new ScreenRect(1280, 0, 1280, 720), quarter.Screen);
        Assert.Equal((640, 352), (quarter.ImageWidth, quarter.ImageHeight));
        Assert.Equal((1920, 360), quarter.ToScreen(320, 176));

        // The part's pixels are cut out of the screen's capture.
        var frame = new RawImage(4, 3, Enumerable.Range(0, 48).Select(i => (byte)i).ToArray());
        Assert.Equal([20, 21, 22, 23, 24, 25, 26, 27, 36, 37, 38, 39, 40, 41, 42, 43], frame.Crop(1, 1, 2, 2).Bgra);
    }

    [Fact]
    public async Task A_look_skips_the_thinking_of_Qwen_models()
    {
        // In LM Studio no switch turned qwen3.8's thinking off, but a reply begun with an empty
        // think block skips it: a look took 3 s instead of 15, and found 19 of 19 Unity targets.
        var server = new ScriptedServer(Sse.Text("(412, 87)"), Sse.Text("(1, 2)"), Sse.Text("(3, 4)"), Sse.Text("(5, 6)"));
        var client = new BlackBox.Core.Llm.OpenAiChatClient(new HttpClient(server), "http://test", null);
        var look = new BlackBox.Core.Agent.FreshLook(client, new BlackBox.Core.Llm.ChatRequestOptions { Model = "qwen3.8-27b@q6_k_xl" });
        Assert.Equal((412, 87), await look.LocateAsync([1, 2, 3], "x/y are pixels of the screenshot, origin top-left.", "Save button", CancellationToken.None));
        var last = server.Requests[0]["messages"]!.AsArray()[^1]!;
        Assert.Equal("assistant", (string?)last["role"]);
        Assert.Equal("<think>\n\n</think>\n\n", (string?)last["content"]);

        // Other models' replies are theirs to begin.
        var gemma = new BlackBox.Core.Agent.FreshLook(client, new BlackBox.Core.Llm.ChatRequestOptions { Model = "gemma-4-31b-it" });
        Assert.Equal((1, 2), await gemma.LocateAsync([1, 2, 3], "", "Save button", CancellationToken.None));
        Assert.Equal("user", (string?)server.Requests[1]["messages"]!.AsArray()[^1]!["role"]);

        // A server that takes no begun reply says so with an error: the look is asked again without one, and so are the next.
        server.Failures.Enqueue((System.Net.HttpStatusCode.BadRequest, """{"error":"The last message must be from the user"}"""));
        Assert.Equal((3, 4), await look.LocateAsync([1, 2, 3], "", "Save button", CancellationToken.None));
        Assert.Equal(["assistant", "user"], server.Requests.Skip(2).Select(r => (string?)r["messages"]!.AsArray()[^1]!["role"]));
        Assert.Equal((5, 6), await look.LocateAsync([1, 2, 3], "", "Save button", CancellationToken.None));
        Assert.Equal("user", (string?)server.Requests[^1]["messages"]!.AsArray()[^1]!["role"]);
    }

    [Fact]
    public async Task A_click_is_held_back_when_what_the_model_aimed_at_has_gone()
    {
        // The model saw a notification in the bottom right corner and aims at its close button; by
        // the time of the click the notification has closed, and the look finds the close button of
        // the window under it, far away (a chat closed by mistake in a chat run).
        var screen = new ScreenRect(0, 0, 384, 192);
        var platform = new FakePlatform { ScreenBounds = screen, Desktop = new RawImage(384, 192, new byte[384 * 192 * 4]) };
        var ctx = Ctx(platform);
        var seen = new RawImage(384, 192, new byte[384 * 192 * 4]);
        for (var y = 128; y < 192; y++) for (var x = 256; x < 384; x++) seen.Bgra[(y * 384 + x) * 4] = 200;
        (double X, double Y)? found = (370, 10);
        ctx.Locate = (_, _, _, _) => Task.FromResult(found);
        var click = new MouseClickTool();
        var args = new JsonObject { ["target"] = "Close button of the notification", ["x"] = 370, ["y"] = 140 };

        ctx.Seen = (screen, seen);
        var held = await click.ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.StartsWith("Nothing was clicked", held.Text);
        Assert.Empty(platform.Actions);

        // Asked again at the same spot after seeing the new screen: it goes ahead.
        ctx.Seen = (screen, seen);
        Assert.StartsWith("Clicked.\nA fresh look found the Close button of the notification at (370, 10)", (await click.ExecuteAsync(args, ctx, CancellationToken.None)).Text);
        Assert.Equal("move 370,10", platform.Actions[0]);

        // The look finds the target close to the aim: a correction, clicked even though the screen changed there.
        platform.Actions.Clear();
        found = (362, 134);
        ctx.Seen = (screen, seen);
        await click.ExecuteAsync(args, ctx, CancellationToken.None);
        Assert.Equal("move 362,134", platform.Actions[0]);

        // Nothing changed where the model aimed: the look is trusted however far it moves the click.
        platform.Actions.Clear();
        found = (370, 10);
        ctx.Seen = (screen, platform.Desktop!);
        await click.ExecuteAsync(new JsonObject { ["target"] = "Close button", ["x"] = 100, ["y"] = 140 }, ctx, CancellationToken.None);
        Assert.Equal("move 370,10", platform.Actions[0]);

        // The model's earlier screen is out of date once an action has run: a second click in the same reply isn't held back.
        Assert.Null(ctx.Seen);
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

        // What changes while the model looks for the target (a dialog closing elsewhere) is no reaction to the click.
        platform.OnClick = null;
        ctx.Locate = (_, _, _, _) =>
        {
            for (var y = 120; y < 160; y++) for (var x = 300; x < 340; x++) platform.Desktop!.Bgra[(y * 384 + x) * 4] ^= 255;
            return Task.FromResult<(double X, double Y)?>((100, 50));
        };
        Assert.Contains("Nothing on screen changed", (await new MouseClickTool().ExecuteAsync(new JsonObject { ["target"] = "OK button", ["x"] = 100, ["y"] = 50 }, ctx, CancellationToken.None)).Text);
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
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotResolution = ScreenshotResolution.R1280x720, ScreenshotAfterActions = false });

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
    public void The_system_prompt_lists_the_screens_and_where_they_are()
    {
        var spaces = new List<ScreenSpace>
        {
            new(new ScreenRect(0, 0, 2560, 1080), (1920, 1080)),
            new(new ScreenRect(2560, 0, 1920, 1080), (1920, 1080)),
        };
        var prompt = BlackBox.Core.Agent.SystemPrompt.Build("Test OS", spaces, "C:\\", "");
        Assert.Contains("1 (primary, 2560x1080, screenshots 1920x800)", prompt);
        Assert.Contains("2 (right of screen 1, 1920x1080, screenshots 1920x1088)", prompt);
        Assert.Contains("one screen at a time", prompt);
        Assert.DoesNotContain("Screens:", BlackBox.Core.Agent.SystemPrompt.Build("Test OS", [spaces[0]], "C:\\", ""));
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
        var ansi = System.Text.Encoding.GetEncoding(1252);
        var dir = Directory.CreateTempSubdirectory("bb-test-").FullName;
        var ctx = Ctx(new FakePlatform(), dir);
        var notes = Path.Combine(dir, "notes.txt");
        File.WriteAllBytes(notes, ansi.GetBytes("Café 5 €\r\nTotal 5\r\n"));

        Assert.Contains("Café 5 €", (await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = notes }, ctx, CancellationToken.None)).Text);
        var edit = await new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = notes, ["old_text"] = "Total 5", ["new_text"] = "Total 6\nPaid" }, ctx, CancellationToken.None);
        Assert.False(edit.IsError, edit.Text);
        // Every other byte as it was, and the new line with the file's own line ending.
        Assert.Equal(ansi.GetBytes("Café 5 €\r\nTotal 6\r\nPaid\r\n"), File.ReadAllBytes(notes));

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
        // Screenshot size, web access and the click log are no longer settings: always native, on, and shown.
        Assert.Equal(ScreenshotResolution.Native, settings.Agent.ScreenshotResolution);
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
        var platform = new BlackBox.Platform.Windows.WindowsPlatform();
#endif
        var screen = platform.Screen.GetScreens()[0];
        Assert.True(screen.Width >= 640 && screen.Height >= 480);

        var result = await new ScreenshotTool().ExecuteAsync([], Ctx(platform), CancellationToken.None);
        var space = new ScreenSpace(screen, new AgentSettings().ScreenshotResolution.Box());
        Assert.Contains($"{space.ImageWidth}x{space.ImageHeight}", result.Text);
        Assert.True(result.Image!.Length > 5_000);
    }

    [Fact]
    public void All_monitors_are_listed_on_windows()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
#else
        var capture = new BlackBox.Platform.Windows.WindowsPlatform().Screen;
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
        var platform = new BlackBox.Platform.Windows.WindowsPlatform();
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
        var platform = new BlackBox.Platform.Windows.WindowsPlatform();
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
        var input = new BlackBox.Platform.Windows.WindowsPlatform().Input;
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

    /// <summary>Key events like the user's: injected without BlackBox's tag.</summary>
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
        var platform = new BlackBox.Platform.Windows.WindowsPlatform();
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
        var platform = new BlackBox.Platform.Windows.WindowsPlatform();
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
