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
        var space = new ScreenSpace(new ScreenRect(0, 0, 2560, 1440), 1280 * 720);
        Assert.Equal((1280, 704), (space.ImageWidth, space.ImageHeight)); // 720 snaps to the 32px grid
        Assert.Equal((200, 307), space.ToScreen(100, 150));
        Assert.Equal((100, 150), space.ToImage(200, 307));
        Assert.Equal((2558, 1438), space.ToScreen(5000, 5000)); // clamped to the screen
    }

    [Fact]
    public void Small_screens_are_not_upscaled()
    {
        var space = new ScreenSpace(new ScreenRect(0, 0, 1024, 768), 1_300_000);
        Assert.Equal((1024, 768), (space.ImageWidth, space.ImageHeight));
    }

    [Fact]
    public async Task Click_maps_model_coordinates_to_screen_pixels()
    {
        var platform = new FakePlatform();
        // 200x100 fake screen with a 100x100 box => scale 0.5
        var ctx = Ctx(platform, settings: new AgentSettings { ScreenshotMaxPixels = 64 * 32, ScreenshotAfterActions = false, FailsafeCorner = false });

        await new MouseClickTool().ExecuteAsync(new JsonObject { ["x"] = 10, ["y"] = "20", ["clicks"] = 2 }, ctx, CancellationToken.None);

        Assert.Equal(["move 31,62", "click Left x2"], platform.Actions); // 200x100 screen seen as 64x32
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
    public void Llama_arguments_include_tuned_defaults()
    {
        var s = new LocalModelSettings { ExtraArgs = "--temp 0.7 --override-kv \"a b\"" };
        var args = LlamaServerHost.BuildArguments(s, "m.gguf", "proj.gguf");
        var line = string.Join(' ', args);

        Assert.Contains("--jinja", args);
        Assert.Contains("--n-gpu-layers 99", line);
        Assert.Contains("--ctx-size 81920", line);
        Assert.Contains("--ctx-size 32768", string.Join(' ', LlamaServerHost.BuildArguments(s, "m.gguf", null, 32768)));
        Assert.Contains("--cache-type-k q8_0", line);
        Assert.Contains("--flash-attn on", line);
        Assert.Contains("--mmproj proj.gguf", line);
        Assert.Equal("a b", args[^1]);
    }

    [Fact]
    public void Context_steps_down_when_it_does_not_fit() =>
        Assert.Equal([81920, 65536, 49152, 32768, 16384, 8192], LlamaServerHost.ContextLadder(81920));

    [Fact]
    public void Portable_paths_inside_the_app_folder_are_stored_relative()
    {
        var root = Path.Combine(Path.GetTempPath(), "bb-root");
        var paths = new AppPaths(root);
        Assert.Equal(Path.Combine("Models", "x.gguf"), paths.ToPortable(Path.Combine(root, "Models", "x.gguf")));
        Assert.Equal(Path.Combine(root, "Models", "x.gguf"), paths.Resolve("Models/x.gguf").Replace('/', Path.DirectorySeparatorChar));
        var outside = Path.GetFullPath(Path.Combine(root, "..", "elsewhere", "y.gguf"));
        Assert.Equal(outside, paths.ToPortable(outside));
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
        var space = new ScreenSpace(screen, new AgentSettings().ScreenshotMaxPixels);
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
