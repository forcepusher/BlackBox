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
        Assert.Contains("the window \"Untitled - Paint\" as capture 1, 450x296", result.Text);

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
        Assert.Contains("capture it with capture_result before your first change: you'll pass it in compare with the result at the end", SystemPrompt.Build("Test OS", screens, "C:\\", ""));
        Assert.DoesNotContain("before your first change", SystemPrompt.Build("Test OS", screens, "C:\\", "", control: false));
        Assert.Contains("capture it with capture_result", SystemPrompt.Build("Test OS", screens, "C:\\", "", control: false));
        Assert.DoesNotContain("capture_result", SystemPrompt.Build("Test OS", null, "C:\\", ""));
    }

    /// <summary>A desktop of one colour, for a capture to tell before from after by.</summary>
    private static RawImage Filled(SKColor color)
    {
        var pixels = new byte[Plain.Width * Plain.Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (color.Blue, color.Green, color.Red, 255);
        return new RawImage(Plain.Width, Plain.Height, pixels);
    }

    [Fact]
    public async Task An_earlier_capture_in_compare_goes_beside_the_new_one_under_labels()
    {
        var platform = new FakePlatform { Screens = [Plain], Desktop = Filled(SKColors.Red) };
        var ctx = Ctx(platform);
        var first = await Capture(ctx, Box(100, 100, 300, 200));
        Assert.Contains("as capture 1, 300x200", first.Text);
        Assert.Contains("pass compare=[1]", first.Text);

        platform.Desktop = Filled(SKColors.Blue);
        var result = await Capture(ctx, new JsonObject { ["x"] = 100, ["y"] = 100, ["width"] = 300, ["height"] = 200, ["compare"] = new JsonArray(1), ["caption"] = "Red to blue" });

        // The new one alone is kept too, as capture 2; the user gets the two together.
        Assert.True(File.Exists(Path.Combine(_chat, "capture-2.png")));
        var both = Assert.Single(result.Files!);
        Assert.Equal(Path.Combine(_chat, "capture-2-compared.png"), both);
        Assert.Contains("after capture 1", result.Text);
        Assert.Contains("labelled \"Before\", \"After\"", result.Text);
        using (var png = SKBitmap.Decode(both))
        {
            // Two 300x200 pictures: side by side (616x240) is nearer 4:3 than one over the other (300x496).
            Assert.Equal((616, 240), (png.Width, png.Height));
            Assert.Equal(SKColors.Red, png.GetPixel(150, 140));
            Assert.Equal(SKColors.Blue, png.GetPixel(316 + 150, 140));
            // Labels in the band over each.
            Assert.Contains(Enumerable.Range(0, 300), x => Enumerable.Range(0, 40).Any(y => png.GetPixel(x, y).Red > 200 && png.GetPixel(x, y).Blue > 200));
            Assert.Contains(Enumerable.Range(316, 300), x => Enumerable.Range(0, 40).Any(y => png.GetPixel(x, y).Red > 200 && png.GetPixel(x, y).Blue > 200));
        }
        // The model sees the comparison too.
        Assert.Equal((616, 240), SizeOf(result.Image!));
        Assert.Equal("Capture the result at (100, 100), 300x200, beside capture 1",
            new CaptureResultTool().Describe(new JsonObject { ["x"] = 100, ["y"] = 100, ["width"] = 300, ["height"] = 200, ["compare"] = "capture 1" }));
    }

    /// <summary>A white picture of the size, as a capture's pixels.</summary>
    private static RawImage White(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        return new RawImage(width, height, pixels);
    }

    private static (int Width, int Height) Composed(int? columns, params RawImage[] pictures)
    {
        using var image = CaptureCompare.Compose(pictures.Select((p, i) => (p, $"{i + 1}")).ToList(), columns: columns);
        return (image.Width, image.Height);
    }

    [Fact]
    public void Pictures_go_in_the_grid_nearest_4_to_3_each_at_its_own_pixels()
    {
        // Each picture under a 40 px label band, 16 px apart. Two wide ones: one over the other (800x496), not a strip (1616x240).
        Assert.Equal((800, 496), Composed(null, White(800, 200), White(800, 200)));
        // Sizes differ: neither is scaled, an upscale shows its detail.
        Assert.Equal((616, 340), Composed(null, White(400, 300), White(200, 150)));
        // Three and four: two across.
        Assert.Equal((616, 496), Composed(null, White(300, 200), White(300, 200), White(300, 200)));
        Assert.Equal((616, 496), Composed(null, White(300, 200), White(300, 200), White(300, 200), White(300, 200)));
        // Nine: three by three, however many there are.
        Assert.Equal((932, 752), Composed(null, Enumerable.Range(0, 9).Select(_ => White(300, 200)).ToArray()));
    }

    [Fact]
    public async Task The_model_can_choose_how_many_go_in_a_row()
    {
        Assert.Equal((300, 752), Composed(1, White(300, 200), White(300, 200), White(300, 200)));
        Assert.Equal((932, 240), Composed(3, White(300, 200), White(300, 200), White(300, 200)));

        var platform = new FakePlatform { Screens = [Plain], Desktop = Filled(SKColors.Red) };
        var ctx = Ctx(platform);
        await Capture(ctx, Box(0, 0, 300, 200));
        await Capture(ctx, Box(0, 0, 300, 200));
        // More columns than pictures: all in one row.
        var row = await Capture(ctx, new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 300, ["height"] = 200, ["compare"] = new JsonArray(1, 2), ["columns"] = 10 });
        Assert.Equal((932, 240), SizeOf(File.ReadAllBytes(row.Files![0])));
        var stacked = await Capture(ctx, new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 300, ["height"] = 200, ["compare"] = new JsonArray(1, 2), ["columns"] = 1 });
        Assert.Equal((300, 752), SizeOf(File.ReadAllBytes(stacked.Files![0])));
        Assert.Contains("1 or more", (await Assert.ThrowsAsync<ToolArgumentException>(() =>
            Capture(ctx, new JsonObject { ["x"] = 0, ["y"] = 0, ["width"] = 300, ["height"] = 200, ["compare"] = new JsonArray(1), ["columns"] = 0 }))).Message);
    }

    [Fact]
    public async Task Captures_and_files_mix_under_the_models_labels_in_any_script()
    {
        var platform = new FakePlatform { Screens = [Plain], Desktop = Filled(SKColors.Blue) };
        var ctx = Ctx(platform);
        await Capture(ctx, Box(0, 0, 300, 200));
        var design = Png("design.png", 300, 200, SKColors.Red);
        var result = await Capture(ctx, new JsonObject
        {
            ["x"] = 0, ["y"] = 0, ["width"] = 300, ["height"] = 200,
            ["compare"] = new JsonArray("1", design), ["labels"] = new JsonArray("Было", "Макет", "Стало"),
        });
        Assert.Contains("after capture 1, design.png", result.Text);
        Assert.Contains("labelled \"Было\", \"Макет\", \"Стало\"", result.Text);
        using (var three = SKBitmap.Decode(result.Files![0]))
        {
            // Two across: the design is second, top right.
            Assert.Equal((616, 496), (three.Width, three.Height));
            Assert.Equal(SKColors.Red, three.GetPixel(316 + 150, 140));
        }

        // Each script in a font that has it, shaped: no empty boxes for missing characters.
        foreach (var label in new[] { "Было", "変更前", "修改后", "قبل", "이전", "Πριν" })
        {
            using var shaped = CaptureCompare.Shape(label, 20);
            Assert.True(shaped.Width > 0, label);
            Assert.DoesNotContain((ushort)0, shaped.Glyphs);
        }
        // A long one is cut to fit its picture, with "…".
        using (var cut = CaptureCompare.Label(new string('x', 200), 20, 120))
        {
            Assert.EndsWith("…", cut.Text);
            Assert.True(cut.Width <= 120);
        }
    }

    [Fact]
    public async Task Labels_and_earlier_pictures_that_dont_fit_are_explained_before_anything_is_captured()
    {
        var platform = new FakePlatform { Screens = [Plain] };
        var ctx = Ctx(platform);
        Assert.Contains("Nothing has been captured yet", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["compare"] = "1" }))).Message);
        Assert.Empty(platform.Captures);

        await Capture(ctx, []);
        await Capture(ctx, []);
        Assert.Contains("Its captures: 1, 2.", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["compare"] = "#7" }))).Message);
        Assert.Contains("There is no picture at", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["compare"] = new JsonArray(@"C:\nowhere\old.png") }))).Message);
        Assert.Contains("3 in all", (await Assert.ThrowsAsync<ToolArgumentException>(() =>
            Capture(ctx, new JsonObject { ["compare"] = new JsonArray(1, 2), ["labels"] = new JsonArray("Old", "New") }))).Message);
        Assert.Contains("labels go with compare", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["labels"] = new JsonArray("Only") }))).Message);
        Assert.Equal(2, platform.Captures.Count);

        // A list a model wrote as a string is read as the list.
        var listed = await Capture(ctx, new JsonObject { ["compare"] = "[1, 2]" });
        Assert.Contains("after capture 1, capture 2", listed.Text);
    }

    /// <summary>A PNG of one colour in the test's own folder, its path.</summary>
    private string Png(string name, int width, int height, SKColor color)
    {
        Directory.CreateDirectory(_chat);
        var path = Path.Combine(_chat, name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, png.ToArray());
        return path;
    }

    [Fact]
    public async Task A_picture_file_can_be_the_result()
    {
        // An upscale the model made: kept at its own size, 1:1, as the next capture; the screen isn't touched.
        var platform = new FakePlatform { Screens = [Plain] };
        var ctx = Ctx(platform);
        var upscale = Png("upscale.png", 3000, 2000, SKColors.Red);
        var result = await Capture(ctx, new JsonObject { ["file"] = upscale, ["caption"] = "Upscaled 4x" });
        Assert.Empty(platform.Captures);
        var kept = Assert.Single(result.Files!);
        Assert.Equal(Path.Combine(_chat, "capture-1.png"), kept);
        Assert.Equal((3000, 2000), SizeOf(File.ReadAllBytes(kept)));
        Assert.Contains("Took the picture upscale.png as capture 1, 3000x2000", result.Text);
        // The model's copy within a screenshot's size.
        var (width, height) = SizeOf(result.Image!);
        Assert.True(width <= ctx.GetScreenSpace().ImageWidth && height <= ctx.GetScreenSpace().ImageHeight, $"{width}x{height}");
        Assert.Equal("Show the result: upscale.png", new CaptureResultTool().Describe(new JsonObject { ["file"] = upscale }));

        // The original can go: the copy is the chat's.
        File.Delete(upscale);
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task A_picture_file_compared_with_a_file_or_a_capture()
    {
        var platform = new FakePlatform { Screens = [Plain], Desktop = Filled(SKColors.Red) };
        var ctx = Ctx(platform);
        var old = Png("old.png", 300, 200, SKColors.Red);
        var edited = Png("new.png", 300, 200, SKColors.Blue);

        var files = await Capture(ctx, new JsonObject { ["file"] = edited, ["compare"] = old });
        Assert.EndsWith("capture-1-compared.png", files.Files![0]);
        Assert.Contains("after old.png", files.Text);
        using (var both = SKBitmap.Decode(files.Files[0]))
        {
            Assert.Equal(SKColors.Red, both.GetPixel(150, 140));
            Assert.Equal(SKColors.Blue, both.GetPixel(316 + 150, 140));
        }

        // A screen capture (capture 2) first, the file last.
        await Capture(ctx, Box(0, 0, 300, 200));
        var mixed = await Capture(ctx, new JsonObject { ["file"] = edited, ["compare"] = 2 });
        Assert.Contains("after capture 2", mixed.Text);

        // An upscale beside its original: each at its own pixels, the small one over the big one (1200x1096 is nearer 4:3 than 1516x840).
        var big = Png("big.png", 1200, 800, SKColors.Blue);
        var upscaled = await Capture(ctx, new JsonObject { ["file"] = big, ["compare"] = old });
        Assert.Equal((1200, 1096), SizeOf(File.ReadAllBytes(upscaled.Files![0])));
    }

    [Fact]
    public async Task A_file_that_cant_be_shown_is_explained()
    {
        var platform = new FakePlatform { Screens = [Plain], Windows = [new("Paint", new ScreenRect(0, 0, 300, 200))] };
        var ctx = Ctx(platform);
        var picture = Png("p.png", 10, 10, SKColors.Red);
        Assert.Contains("not more", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = picture, ["window"] = "Paint" }))).Message);
        Assert.Contains("not more", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = picture, ["x"] = 0, ["y"] = 0, ["width"] = 9, ["height"] = 9 }))).Message);
        Assert.Contains("There is no picture at", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = Path.Combine(_chat, "gone.png") }))).Message);
        var notes = Path.Combine(_chat, "notes.txt");
        File.WriteAllText(notes, "hello");
        Assert.Contains("isn't a picture", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = notes }))).Message);
        var fake = Path.Combine(_chat, "fake.png");
        File.WriteAllText(fake, "not a png");
        Assert.Contains("could not be read as a picture", (await Assert.ThrowsAsync<ToolArgumentException>(() => Capture(ctx, new JsonObject { ["file"] = fake }))).Message);
        Assert.Empty(platform.Captures);
        Assert.False(File.Exists(Path.Combine(_chat, "capture-1.png")));
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
