using System.Text.Json.Nodes;
using BlackBox.Core.Llm;
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
        Assert.Equal((100, 150), space.ToImage(200, 307));
        Assert.Equal((2558, 1438), space.ToScreen(5000, 5000)); // clamped to the screen
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

        // list_windows says which screen a window is on, and focus_window switches to it.
        var list = await new ListWindowsTool().ExecuteAsync([], ctx, CancellationToken.None);
        Assert.Contains("screen=1", list.Text);
        var focus = await new FocusWindowTool().ExecuteAsync(new JsonObject { ["title"] = "Notepad" }, ctx, CancellationToken.None);
        Assert.Contains("on screen 1", focus.Text);
        Assert.Equal(0, ctx.CurrentScreen);
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
    public async Task Ui_elements_on_another_screen_switch_to_it_and_use_its_coordinates()
    {
        var platform = new FakePlatform
        {
            Screens = [new ScreenRect(0, 0, 200, 100), new ScreenRect(200, 0, 160, 100)],
            Elements =
            [
                new UiElement(1, "Button", "OK", null, new ScreenRect(300, 40, 20, 10), true),      // screen 2
                new UiElement(2, "Edit", "Name", "", new ScreenRect(220, 10, 40, 10), true),         // screen 2
                new UiElement(3, "Button", "Elsewhere", null, new ScreenRect(10, 10, 20, 10), true), // screen 1
            ],
        };
        var ctx = Ctx(platform);
        Assert.Equal(0, ctx.CurrentScreen);

        var result = await new ListUiElementsTool().ExecuteAsync([], ctx, CancellationToken.None);

        Assert.Equal(1, ctx.CurrentScreen);
        Assert.StartsWith("These controls are on screen 2", result.Text);
        // Screen 2 is 160x100, sent as 160x96: x is relative to its left edge, y scaled by 0.96.
        Assert.Contains("#1 Button \"OK\" at (110, 43)", result.Text);
        Assert.Contains("#2 Edit \"Name\" at (40, 14)", result.Text);
        Assert.Contains("#3 Button \"Elsewhere\" at (19, 14) on screen 1", result.Text); // screen 1 is 200x100, sent as 192x96

        // On one screen nothing changes.
        var single = Ctx(new FakePlatform());
        var plain = await new ListUiElementsTool().ExecuteAsync([], single, CancellationToken.None);
        Assert.DoesNotContain("screen", plain.Text);
        Assert.Contains("#1 Button \"Save\" at (115, 58)", plain.Text);
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
    public async Task Zoom_returns_a_magnified_image_with_rulers()
    {
        var result = await new ZoomTool().ExecuteAsync(
            new JsonObject { ["x"] = 10, ["y"] = 10, ["width"] = 50, ["height"] = 40 }, Ctx(new FakePlatform()), CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Equal(0xFF, result.Image![0]);
        Assert.Contains("Zoomed 4x", result.Text);
    }

    [Fact]
    public async Task Ui_elements_are_listed_in_screenshot_coordinates_and_pressed_by_id()
    {
        var platform = new FakePlatform();
        var ctx = Ctx(platform);
        var list = await new ListUiElementsTool().ExecuteAsync(new JsonObject { ["filter"] = "save" }, ctx, CancellationToken.None);
        Assert.Contains("#1 Button \"Save\"", list.Text);
        Assert.DoesNotContain("File name", list.Text);

        var press = await new ClickElementTool().ExecuteAsync(new JsonObject { ["id"] = 1 }, ctx, CancellationToken.None);
        Assert.False(press.IsError);
        Assert.Contains("activate 1", platform.Actions);
        await Assert.ThrowsAsync<ToolArgumentException>(() => new ClickElementTool().ExecuteAsync(new JsonObject { ["id"] = 9 }, ctx, CancellationToken.None));
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
        var screen = platform.Screen.GetPrimaryScreen();
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
        Assert.Equal(capture.GetPrimaryScreen(), screens[0]);
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

        Assert.Equal("Left double click", overlay.Shown[0].Text);
        Assert.Equal((40, 30), overlay.Shown[0].Point);
        Assert.Equal("Keys Ctrl+Shift+S", overlay.Shown[1].Text);
        Assert.Equal("Keys ←", overlay.Shown[2].Text);
        Assert.Equal("Type “hello⏎world, this is a long line that ke…”", overlay.Shown[3].Text);
        Assert.Null(overlay.Shown[3].Point);
        Assert.Equal("Scroll down 3", overlay.Shown[4].Text);
        Assert.Contains("click Left x2", platform.Actions); // the real input still happened
    }
}
