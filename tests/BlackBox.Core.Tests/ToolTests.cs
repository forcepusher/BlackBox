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
        Settings = settings ?? new AgentSettings { ScreenshotAfterActions = false, FailsafeCorner = false },
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
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotResolution = ScreenshotResolution.R1280x720, ScreenshotAfterActions = false, FailsafeCorner = false });

        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = "20", ["clicks"] = 2 }, ctx, CancellationToken.None);

        Assert.Equal(["move 20,41", "click Left x2"], platform.Actions);
    }

    [Fact]
    public async Task Failsafe_corner_stops_desktop_actions()
    {
        var platform = new FakePlatform { Cursor = (0, 0) };
        var ctx = Ctx(platform, settings: new AgentSettings { FailsafeCorner = true });
        await Assert.ThrowsAsync<FailsafeTriggeredException>(() =>
            new TypeTextTool().ExecuteAsync(new JsonObject { ["text"] = "x" }, ctx, CancellationToken.None));
        Assert.Empty(platform.Actions);
    }

    [Fact]
    public async Task Press_keys_accepts_plus_separated_strings()
    {
        var platform = new FakePlatform();
        await new PressKeysTool().ExecuteAsync(new JsonObject { ["keys"] = "ctrl+shift+esc" }, Ctx(platform), CancellationToken.None);
        Assert.Equal(["keys ctrl+shift+esc"], platform.Actions);
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
