using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Comfy;
using Sloperator.Core.Llm;
using Sloperator.Core.Media;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

// A picture the user gave to follow is shown next to what each ComfyUI job makes: asked for a bunny
// from the user's reference sheet, Nemotron judged four results against its memory of the sheet,
// many steps back, and called them mature and on-style while the user saw a chubby teenager.
public class ReferencePictureTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-reference-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Png(string name, int width, int height, SKColor color)
    {
        var path = Path.Combine(_dir, name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void The_pictures_of_the_users_latest_message_with_any_are_found_again_later_in_the_chat()
    {
        var sheet = Png("Anton Zilberman Refsheet (2).png", 300, 220, SKColors.Gray);
        var older = Png("old.png", 10, 10, SKColors.Red);
        var notes = Path.Combine(_dir, "notes.txt");
        File.WriteAllText(notes, "x");
        var history = new List<ChatMessage>
        {
            new UserInput("An old one", [older]).ToMessage(seesImages: true),
            new UserInput("Generate me a bunny picture using this as a reference sheet", [sheet, notes]).ToMessage(seesImages: true),
            new() { Role = ChatRole.Assistant, Content = "Here it is." },
            new UserInput("A bit too young looking, make him mature").ToMessage(seesImages: true),
        };

        // A path with " (" in it, and no file that isn't a picture.
        Assert.Equal([sheet], UserInput.PicturesIn(history));

        // As a saved chat keeps it, without the pictures themselves.
        Assert.Equal([sheet], UserInput.PicturesIn(Chats.ChatStore.ForSaving(history)));

        File.Delete(sheet);
        Assert.Equal([older], UserInput.PicturesIn(history));
        Assert.Empty(UserInput.PicturesIn([new UserInput("hello").ToMessage(seesImages: true)]));
    }

    // A walk cycle sheet "on a transparent background" came out with every pixel transparent, blank on
    // white, and qwen3.8 described four knights in it.
    [Fact]
    public void How_much_of_a_picture_is_transparent_is_said_with_it()
    {
        var blank = Png("knight_walk.png", 64, 48, SKColors.Transparent);
        Assert.Contains("a 64x48 picture, shown at 64x64, fully transparent: nothing in it shows (shown on white)", Picture.Read(blank).Text);
        Assert.Contains("fully transparent: nothing in it shows", MediaPreview.Look(blank, null, makeTile: true).Line);

        var sprite = Path.Combine(_dir, "knight.png");
        using (var bitmap = new SKBitmap(new SKImageInfo(40, 40, SKColorType.Rgba8888, SKAlphaType.Premul)))
        {
            bitmap.Erase(SKColors.Transparent);
            using (var canvas = new SKCanvas(bitmap))
                canvas.DrawRect(0, 0, 20, 40, new SKPaint { Color = SKColors.SteelBlue });
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(sprite, data.ToArray());
        }
        Assert.Contains("50% of it transparent", Picture.Read(sprite).Text);

        // An opaque one says nothing about it.
        Assert.DoesNotContain("transparent", Picture.Read(Png("photo.png", 30, 20, SKColors.Gray)).Text);
    }

    // Asked to crop the clothed figure from the sheet, qwen3.8 read its corner on its 0-1000 grid and
    // cropped at those numbers as pixels: three crops of the wrong figure.
    [Fact]
    public void A_model_that_points_on_a_grid_is_told_how_its_places_in_a_picture_become_pixels()
    {
        var sheet = Png("Anton Zilberman Refsheet.png", 2945, 2160, SKColors.Gray);

        var gridModel = new UserInput("A bunny like this", [sheet]).ToMessage(seesImages: true, grid: 1000);
        var text = gridModel.Parts![0].Text!;
        Assert.Contains("a 2945x2160 picture, shown below at 2048x1504; places you see in it are on your 0-1000 grid, " +
                        "so in its own pixels x is 2.945 times your x and y 2.16 times your y)", text);
        Assert.Equal([sheet], UserInput.PicturesIn([gridModel]));
        Assert.Contains("2.945 times your x", Picture.Read(sheet, 1000).Text);

        // A model that points in pixels of what it sees gets no such note.
        Assert.DoesNotContain("grid", new UserInput("A bunny like this", [sheet]).ToMessage(seesImages: true).Parts![0].Text!);
        Assert.DoesNotContain("grid", Picture.Read(sheet).Text);
    }

    [Fact]
    public async Task A_run_knows_the_picture_given_earlier_in_the_chat()
    {
        var sheet = Png("sheet.png", 40, 30, SKColors.Gray);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };
        var server = new ScriptedServer(Sse.Text("Made it."), Sse.Text("Made him older."));
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(server), "http://test", null), new ChatRequestOptions(),
            ToolRegistry.CreateDefault(includeScreenTools: false), ctx, "sys");

        await session.RunAsync(new UserInput("A bunny like this", [sheet]), new RecordingObserver(), CancellationToken.None);
        await session.RunAsync("Make him older", new RecordingObserver(), CancellationToken.None);

        Assert.Equal([sheet], ctx.UserPictures);
    }

    [Fact]
    public void The_picture_to_follow_and_the_look_at_the_result_are_put_side_by_side()
    {
        var sheet = Png("sheet.png", 2945, 2160, SKColors.Blue);
        using var look = new SKBitmap(1024, 1424);
        look.Erase(SKColors.Red);
        using var lookJpeg = look.Encode(SKEncodedImageFormat.Jpeg, 90);

        var both = MediaPreview.SideBySide(sheet, lookJpeg.ToArray(), "The user's picture")!;

        using var image = SKBitmap.Decode(both);
        Assert.True(image.Width <= 2048 && image.Height <= 2048, $"{image.Width}x{image.Height}");
        Assert.Equal(0, image.Width % 32);
        Assert.Equal(0, image.Height % 32);
        Assert.True((long)image.Width * image.Height <= 2048L * 1536 * 11 / 10, $"{image.Width}x{image.Height}");
        // The sheet on the left, the result on the right, at the same height under the labels.
        var middle = image.Height / 2;
        Assert.True(image.GetPixel(image.Width / 5, middle).Blue > 200);
        Assert.True(image.GetPixel(image.Width * 9 / 10, middle).Red > 200);
        Assert.Null(MediaPreview.SideBySide(Path.Combine(_dir, "gone.png"), lookJpeg.ToArray(), "The user's picture"));
    }
}

/// <summary>The side by side in the comfyui tool's runs, against a stand-in ComfyUI.</summary>
public class ReferencePictureToolTests : IDisposable
{
    private readonly FakeComfyServer _server = new();
    private readonly string _home = Directory.CreateTempSubdirectory("bb-reference-home-").FullName;
    private readonly ToolContext _ctx;

    public ReferencePictureToolTests()
    {
        var paths = new AppPaths(_home);
        Directory.CreateDirectory(paths.ComfyDir);
        File.WriteAllText(Path.Combine(paths.ComfyDir, "state.json"), new JsonObject { ["url"] = _server.Url.ToString() }.ToJsonString());
        _ctx = new ToolContext
        {
            Platform = new FakePlatform(),
            Settings = new AgentSettings(),
            WorkingDirectory = _home,
            Comfy = ComfyHub.For(paths.ComfyDir),
            ScratchDirectory = paths.ScratchDir,
        };
    }

    public void Dispose()
    {
        _server.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private string Png(string name, int width, int height)
    {
        var path = Path.Combine(_home, name);
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Blue);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private Task<ToolResult> Run(string workflow) =>
        new ComfyTool().ExecuteAsync(new JsonObject { ["action"] = "run", ["workflow"] = workflow }, _ctx, CancellationToken.None);

    [Fact]
    public async Task A_run_shows_the_users_picture_next_to_what_it_made()
    {
        var plain = await Run(FakeComfyServer.ApiWorkflow);
        using var alone = SKBitmap.Decode(plain.Image!);
        Assert.Equal("What the job made:", plain.ImageCaption);

        _ctx.UserPictures = [Png("refsheet.png", 600, 440)];
        var result = await Run(FakeComfyServer.ApiWorkflow);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("On the left is the picture the user gave you (refsheet.png); on the right, what the job made.", result.Text);
        Assert.Equal("The user's picture, and what the job made:", result.ImageCaption);
        using var both = SKBitmap.Decode(result.Image!);
        Assert.True(both.Width > alone.Width, $"{both.Width} wide, the look alone {alone.Width}");
    }

    [Fact]
    public async Task Without_a_picture_from_the_user_the_one_the_job_started_from_is_shown()
    {
        var photo = Png("photo.png", 64, 48);
        var workflow = """
            {"1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "v1-5-pruned-emaonly-fp16.safetensors"}},
             "2": {"class_type": "CLIPTextEncode", "inputs": {"text": "a watercolor", "clip": ["1", 1]}},
             "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "", "clip": ["1", 1]}},
             "4": {"class_type": "LoadImage", "inputs": {"image": "PHOTO"}},
             "6": {"class_type": "VAEEncode", "inputs": {"pixels": ["4", 0], "vae": ["1", 2]}},
             "7": {"class_type": "KSampler", "inputs": {"model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0], "latent_image": ["6", 0],
                   "seed": 1, "steps": 20, "cfg": 7, "sampler_name": "euler", "scheduler": "normal", "denoise": 0.6}},
             "8": {"class_type": "VAEDecode", "inputs": {"samples": ["7", 0], "vae": ["1", 2]}},
             "9": {"class_type": "SaveImage", "inputs": {"images": ["8", 0], "filename_prefix": "img2img"}}}
            """.Replace("PHOTO", photo.Replace("\\", "\\\\"));

        var result = await Run(workflow);

        Assert.False(result.IsError, result.Text);
        Assert.Contains("On the left is the picture the job started from (photo.png)", result.Text);
        Assert.Equal("The picture the job started from, and what it made:", result.ImageCaption);
    }
}
