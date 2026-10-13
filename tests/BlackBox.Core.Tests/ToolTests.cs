using System.Text.Json.Nodes;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

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
    public async Task Qwen3_models_point_on_a_0_to_1000_grid()
    {
        Assert.Equal(1000, ModelCoordinates.GridFor("qwen3.8-27b@q6_k_xl"));
        Assert.Equal(1000, ModelCoordinates.GridFor("Qwen/Qwen3-VL-8B-Instruct"));
        Assert.Null(ModelCoordinates.GridFor("qwen2.5-vl-7b"));
        Assert.Null(ModelCoordinates.GridFor("gemma-4-31b-it"));

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
    }

    [Fact]
    public async Task A_click_goes_where_a_fresh_look_finds_the_target()
    {
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 2560, 1440) };
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotResolution = ScreenshotResolution.R1280x720, ScreenshotAfterActions = false });
        string? looked = null;
        (double X, double Y)? found = (300, 120);
        ctx.Locate = (_, target, _) => { looked = target; return Task.FromResult(found); };
        var click = new MouseClickTool();

        // Found: the model's own x/y are ignored.
        var result = await click.ExecuteAsync(new JsonObject { ["target"] = "Save button", ["x"] = 10, ["y"] = 20 }, ctx, CancellationToken.None);
        Assert.Equal("Save button", looked);
        Assert.Equal("move 600,245", platform.Actions[0]);
        Assert.Equal("Clicked.", result.Text);

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
        ctx.Locate = (_, _, _) =>
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

        var png = Path.Combine(dir, "picture.png");
        File.WriteAllBytes(png, TestPng);
        var binary = await tool.ExecuteAsync(new JsonObject { ["path"] = png }, Ctx(new FakePlatform(), dir), CancellationToken.None);
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
              "agent": { "enableThinking": false, "temperature": 0.3, "maxSteps": 12, "customInstructions": "be brief", "keepScreenshots": 5, "workingDirectory": "D:\\x", "screenshotMaxPixels": 2000000 }
            }
            """);

        var settings = new SettingsStore(paths).Load();

        Assert.Equal("http://host:1234/v1", settings.Server.BaseUrl);
        Assert.Equal("qwen", settings.Server.Model);
        Assert.Equal("be brief", settings.Agent.CustomInstructions);
        Assert.Equal(ScreenshotResolution.R1920x1080, settings.Agent.ScreenshotResolution); // nearest to the old 2 MP budget
        var saved = File.ReadAllText(paths.SettingsFile);
        foreach (var gone in new[] { "backend", "local", "remote", "gpuLayers", "sendExtras", "enableThinking", "temperature", "keepScreenshots", "contextSize", "workingDirectory", "screenshotMaxPixels", "maxSteps", "maxPromptTokens" })
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
    public async Task Break_key_stops_a_run_but_the_agents_own_presses_do_not()
    {
#if !WINDOWS
        Assert.Skip("Windows only");
        await Task.CompletedTask;
#else
        var pressed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new BlackBox.Platform.Windows.WindowsPlatform();
        using var watch = platform.StopKey.Watch(() => pressed.TrySetResult(true));

        platform.Input.PressKeys(["pause"]); // the agent's own press is tagged and ignored
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(pressed.Task.IsCompleted);

        // A press like the user's: injected without the tag.
        BlackBox.Platform.Windows.Native.INPUT[] inputs =
        [
            new() { type = BlackBox.Platform.Windows.Native.INPUT_KEYBOARD, u = new() { ki = new() { wVk = 0x13 } } },
            new() { type = BlackBox.Platform.Windows.Native.INPUT_KEYBOARD, u = new() { ki = new() { wVk = 0x13, dwFlags = BlackBox.Platform.Windows.Native.KEYEVENTF_KEYUP } } },
        ];
        BlackBox.Platform.Windows.Native.SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<BlackBox.Platform.Windows.Native.INPUT>());
        Assert.Same(pressed.Task, await Task.WhenAny(pressed.Task, Task.Delay(3000, TestContext.Current.CancellationToken)));
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
