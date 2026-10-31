using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

public class CaptureResultTests : IDisposable
{
    private readonly string _chat = Path.Combine(Path.GetTempPath(), "sloperator-capture-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_chat, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    /// <summary>A 1600x896 screen goes to the model as it is (896 is on the 32 px grid); a 2000x1120 one at 80%, 1.25 screen pixels a screenshot pixel.</summary>
    private static readonly ScreenRect Plain = new(0, 0, 1600, 896), Scaled = new(1600, 0, 2000, 1120);

    private ToolContext Ctx(FakePlatform platform, int? maxSide = null) => new()
    {
        Platform = platform,
        Settings = new AgentSettings { ScreenshotMaxHeight = 896 },
        WorkingDirectory = Path.GetTempPath(),
        ChatDirectory = _chat,
        MaxScreenshotSide = maxSide,
    };

    private static Task<ToolResult> Capture(ToolContext ctx, JsonObject args) => new CaptureResultTool().ExecuteAsync(args, ctx, CancellationToken.None);

    private static JsonObject Box(double x, double y, double width, double height) => new() { ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height };

    private static (int Width, int Height) SizeOf(byte[] picture)
    {
        using var codec = SKCodec.Create(new MemoryStream(picture));
        return (codec.Info.Width, codec.Info.Height);
    }

    [Fact]
    public async Task A_box_in_screenshot_coordinates_is_captured_in_screen_pixels()
    {
        var platform = new FakePlatform { Screens = [Plain, Scaled] };
        var ctx = Ctx(platform);

        // 100%: a screenshot pixel is a screen pixel.
        await Capture(ctx, Box(100, 40, 300, 200));
        Assert.Equal(new ScreenRect(100, 40, 300, 200), platform.Captures[^1]);

        // Cut to the screen where it runs past its edge.
        await Capture(ctx, Box(1500, 800, 400, 400));
        Assert.Equal(new ScreenRect(1500, 800, 100, 96), platform.Captures[^1]);

        // On the second screen, shown to the model at 80%: from that screen's corner, 1.25 times as large.
        ctx.CurrentScreen = 1;
        Assert.Equal((1600, 896), (ctx.GetScreenSpace().ImageWidth, ctx.GetScreenSpace().ImageHeight));
        await Capture(ctx, Box(100, 40, 300, 200));
        Assert.Equal(new ScreenRect(1600 + 125, 50, 375, 250), platform.Captures[^1]);
    }

    [Fact]
    public async Task A_window_is_captured_by_its_bounds_cut_to_the_screen_it_is_mostly_on()
    {
        var platform = new FakePlatform
        {
            Screens = [Plain, Scaled],
            Windows =
            [
                new("Paint settings", new ScreenRect(40, 40, 200, 100)),
                new("Untitled - Paint", new ScreenRect(-50, 600, 500, 400)),
                new("Budget.xlsx - Excel", new ScreenRect(1500, 100, 800, 600)),
            ],
        };
        var ctx = Ctx(platform);

        // The frontmost window that has the words in its title, cut to the screen.
        var result = await Capture(ctx, new JsonObject { ["window"] = "untitled" });
        Assert.Equal(new ScreenRect(0, 600, 450, 296), platform.Captures[^1]);
        Assert.Contains("the window \"Untitled - Paint\", 450x296", result.Text);

        // A title that is the whole of one goes before one that only has it in it.
        platform.Windows.Insert(0, new("Paint 3D", new ScreenRect(10, 10, 100, 100)));
        platform.Windows.Add(new("paint", new ScreenRect(300, 300, 120, 80)));
        await Capture(ctx, new JsonObject { ["window"] = "Paint" });
        Assert.Equal(new ScreenRect(300, 300, 120, 80), platform.Captures[^1]);

        // Mostly on the second screen: the part on it, whatever screen the model looks at.
        await Capture(ctx, new JsonObject { ["window"] = "Excel" });
        Assert.Equal(new ScreenRect(1600, 100, 700, 600), platform.Captures[^1]);
        Assert.Equal(0, ctx.CurrentScreen);
    }

    [Fact]
    public async Task The_capture_leaves_the_pointer_out_and_goes_to_the_user_at_full_size()
    {
        var white = new byte[Plain.Width * Plain.Height * 4];
        Array.Fill(white, (byte)255);
        var platform = new FakePlatform { Screens = [Plain], Desktop = new RawImage(Plain.Width, Plain.Height, white), Cursor = (60, 60) };
        var ctx = Ctx(platform);

        var result = await Capture(ctx, Box(50, 50, 100, 80));
        var first = Assert.Single(result.Files!);
        Assert.Equal(Path.Combine(_chat, "capture-1.png"), first);

        // A lossless PNG at the screen's resolution, with no pointer over the work (a screenshot shows a dark arrow at the cursor).
        using (var png = SKBitmap.Decode(first))
        {
            Assert.Equal((100, 80), (png.Width, png.Height));
            for (var y = 0; y < png.Height; y++)
                for (var x = 0; x < png.Width; x++)
                    Assert.Equal(SKColors.White, png.GetPixel(x, y));
        }
        // The model sees the same crop, to check it.
        Assert.Equal((100, 80), SizeOf(result.Image!));
        Assert.Equal("image/jpeg", result.ImageMime);
        Assert.Contains("now sees", result.ImageCaption);

        // Each capture is a file of its own, kept with the chat.
        var second = await Capture(ctx, new JsonObject { ["caption"] = "The finished chart" });
        Assert.Equal(Path.Combine(_chat, "capture-2.png"), Assert.Single(second.Files!));
        Assert.Contains("captioned \"The finished chart\"", second.Text);
        Assert.Equal(Plain, platform.Captures[^1]);
    }

    [Fact]
    public async Task The_picture_the_model_gets_is_no_larger_than_a_screenshot()
    {
        // A 4K screen: the user gets every pixel, the model what a screenshot of it would be.
        var platform = new FakePlatform { Screens = [new ScreenRect(0, 0, 3840, 2160)] };
        var ctx = Ctx(platform);
        var result = await Capture(ctx, []);
        var space = ctx.GetScreenSpace();
        var (width, height) = SizeOf(result.Image!);
        Assert.True(width <= space.ImageWidth && height <= space.ImageHeight, $"{width}x{height}");
        Assert.Equal(height, space.ImageHeight);
        using (var png = SKCodec.Create(result.Files![0]))
            Assert.Equal((3840, 2160), (png.Info.Width, png.Info.Height));

        // Within a model's own limit (Claude's longest side), whatever the setting.
        ctx = Ctx(platform, maxSide: 1568);
        (width, height) = SizeOf((await Capture(ctx, [])).Image!);
        Assert.True(Math.Max(width, height) <= 1568, $"{width}x{height}");

        // A small crop is never enlarged.
        Assert.Equal((50, 25), SizeOf((await Capture(Ctx(new FakePlatform { Screens = [Plain] }), Box(0, 0, 50, 25))).Image!));
    }

    [Fact]
    public async Task Arguments_it_cannot_use_are_explained()
    {
        var platform = new FakePlatform
        {
            Screens = [Plain],
            Windows = [new("Untitled - Paint", new ScreenRect(0, 0, 400, 300)), new("Inbox - Mail", new ScreenRect(0, 0, 400, 300))],
        };
        var ctx = Ctx(platform);

        var missing = await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["window"] = "Blender" }));
        Assert.Contains("\"Blender\"", missing.Message);
        Assert.Contains("\"Untitled - Paint\", \"Inbox - Mail\"", missing.Message);

        platform.Windows = [new("Far away", new ScreenRect(5000, 5000, 300, 200))];
        Assert.Contains("off the screen", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["window"] = "far" }))).Message);

        Assert.Contains("1600x896", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, Box(1700, 100, 100, 100)))).Message);
        Assert.Contains("off the screen", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, Box(-300, 10, 200, 100)))).Message);
        await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, Box(10, 10, 0, 100)));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["x"] = 10, ["y"] = 10 }));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["window"] = "Paint", ["x"] = 1, ["y"] = 1, ["width"] = 10, ["height"] = 10 }));
        Assert.Empty(platform.Captures);
    }

    [Fact]
    public void It_is_offered_with_the_screen_and_asked_for_in_the_prompt()
    {
        Assert.NotNull(ToolRegistry.CreateDefault(includeScreenTools: true, includeControl: false).Find("capture_result"));
        Assert.Null(ToolRegistry.CreateDefault(includeScreenTools: false).Find("capture_result"));
        Assert.Equal(ToolRisk.ReadOnly, new CaptureResultTool().Risk);

        var screens = new[] { new ScreenSpace(Plain, (1600, 896)) };
        Assert.Contains("capture it with capture_result, cropped to what you made", SystemPrompt.Build("Test OS", screens, "C:\\", ""));
        Assert.Contains("capture it with capture_result", SystemPrompt.Build("Test OS", screens, "C:\\", "", control: false));
        Assert.DoesNotContain("capture_result", SystemPrompt.Build("Test OS", null, "C:\\", ""));
    }

    [Fact]
    public async Task A_done_claim_with_a_capture_still_takes_it()
    {
        // "Complete" with only a look ends the turn (small models re-check forever); the picture for the user is taken.
        var claim = Sse.Text("The chart is complete.").Replace("data: [DONE]\n\n", "") + Sse.ToolCall("c1", "capture_result", "{}");
        var server = new ScriptedServer(claim, Sse.Text("Made the chart."));
        var platform = new FakePlatform { Screens = [Plain] };
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null),
            new ChatRequestOptions(), ToolRegistry.CreateDefault(true), Ctx(platform), "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("make a chart", observer, CancellationToken.None);

        Assert.Equal(["capture_result"], observer.ToolsRun);
        Assert.Equal(2, server.Requests.Count);
    }
}
