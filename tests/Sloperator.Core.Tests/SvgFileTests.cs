using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Sloperator.Core.Documents;
using Sloperator.Core.Media;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

// SVG files as pictures: drawn by Svg.Skia for the chat, File inspector and read_file.
public class SvgFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-svg-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string name, string svg)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, svg);
        return path;
    }

    private const string Mockup = """
        <svg xmlns="http://www.w3.org/2000/svg" width="200" height="100" viewBox="0 0 200 100">
          <style>.button { fill: #FCC21B; }</style>
          <rect width="200" height="100" fill="#202020"/>
          <rect class="button" x="20" y="40" width="100" height="32" rx="6"/>
          <text x="70" y="61" text-anchor="middle" font-size="14" fill="#2F2F2F">Continue</text>
        </svg>
        """;

    [Fact]
    public void An_svg_is_drawn_at_its_size_and_scaled_sharp()
    {
        var svg = SvgFile.Open(Write("mockup.svg", Mockup));
        Assert.NotNull(svg);
        Assert.Equal((200f, 100f), (svg.Width, svg.Height));
        using (var drawn = svg.Render(1))
        {
            Assert.Equal((200, 100), (drawn.Width, drawn.Height));
            Assert.Equal(new SKColor(0x20, 0x20, 0x20), drawn.GetPixel(5, 5));
            Assert.Equal(new SKColor(0xFC, 0xC2, 0x1B), drawn.GetPixel(25, 45)); // the CSS class's fill
        }
        using var big = svg.RenderAtLeast(1000);
        Assert.Equal((1000, 500), (big.Width, big.Height));
        Assert.Equal(new SKColor(0xFC, 0xC2, 0x1B), big.GetPixel(125, 225));
    }

    [Fact]
    public void Its_size_comes_from_the_view_box_without_width_and_height()
    {
        var svg = SvgFile.Open(Write("icon.svg", """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 16"><rect width="24" height="16" fill="red"/></svg>"""));
        Assert.NotNull(svg);
        Assert.Equal((24f, 16f), (svg.Width, svg.Height));
        using var drawn = PictureFile.Decode(Path.Combine(_dir, "icon.svg"));
        Assert.Equal((24, 16), (drawn!.Width, drawn.Height));
    }

    [Fact]
    public void What_is_no_svg_draws_nothing()
    {
        Assert.Null(SvgFile.Open(Write("broken.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\"")));
        Assert.Null(SvgFile.Open(Write("empty.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"/>")));
        Assert.Null(SvgFile.Open(McpTestServer.TestTools.Png));
        Assert.Null(PictureFile.Decode(Path.Combine(_dir, "broken.svg")));
    }

    [Fact]
    public void A_picture_next_to_it_is_drawn_and_one_on_the_web_is_not_fetched()
    {
        // Opening a file must not make a request: an SVG from anywhere can name a tracker.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using (var blue = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Rgba8888, SKAlphaType.Premul)))
            {
                blue.Erase(SKColors.Blue);
                using var png = blue.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(_dir, "local.png"), png.ToArray());
            }
            var svg = SvgFile.Open(Write("linked.svg", $"""
                <svg xmlns="http://www.w3.org/2000/svg" width="100" height="40">
                  <image x="0" y="0" width="40" height="40" href="local.png"/>
                  <image x="60" y="0" width="40" height="40" href="http://127.0.0.1:{port}/tracker.png"/>
                </svg>
                """));
            Assert.NotNull(svg);
            using var drawn = svg.Render(1);
            Assert.Equal(SKColors.Blue, drawn.GetPixel(20, 20));
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void An_svg_is_a_picture_in_the_chat_and_file_inspector()
    {
        Assert.Equal("image", MediaPreview.KindOf("mockup.svg"));
        Assert.Equal(InspectorKind.Picture, FileInspector.KindOf(Write("mockup.svg", Mockup)));
        Assert.StartsWith("SVG drawing 200x100, ", MediaPreview.Look(Path.Combine(_dir, "mockup.svg"), null, makeTile: false).Line);
    }

    [Fact]
    public async Task Read_file_gives_its_lines_and_what_it_draws()
    {
        var path = Write("mockup.svg", Mockup);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };
        var result = await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = path }, ctx, CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Contains("4\t  <rect class=\"button\"", result.Text);
        // Drawn 1024 wide at least, so the model reads a small mockup's text; its size told in its own units.
        Assert.Contains("It draws a 200x100 picture, shown at 1024x512.", result.Text);
        Assert.NotNull(result.Image);
        Assert.Equal([path], result.Files);

        // Without sight, only its lines.
        var blind = await new ReadFileTool().ExecuteAsync(new JsonObject { ["path"] = path }, new ToolContext
            { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir, SeesImages = false }, CancellationToken.None);
        Assert.Null(blind.Image);
        Assert.Contains("Continue", blind.Text);
    }

    [Fact]
    public async Task A_written_or_edited_svg_is_shown_in_the_chat()
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _dir };
        var path = Path.Combine(_dir, "mockup.svg");
        var written = await new WriteFileTool().ExecuteAsync(new JsonObject { ["path"] = path, ["content"] = Mockup }, ctx, CancellationToken.None);
        Assert.Equal([path], written.Files);
        var edited = await new EditFileTool().ExecuteAsync(new JsonObject { ["path"] = path, ["old_text"] = "Continue", ["new_text"] = "Next" }, ctx, CancellationToken.None);
        Assert.False(edited.IsError, edited.Text);
        Assert.Equal([path], edited.Files);
    }

    [Fact]
    public void An_mcp_servers_svg_is_drawn_for_the_model()
    {
        var shown = Picture.Load(System.Text.Encoding.UTF8.GetBytes(Mockup));
        Assert.NotNull(shown);
        Assert.Equal((1024, 512), (shown.ShownWidth, shown.ShownHeight));
    }

    [Fact]
    public void An_area_of_a_drawing_is_cut_sharp()
    {
        var path = Write("mockup.svg", Mockup);
        var picture = DocumentArea.Decode(path);
        Assert.NotNull(picture);
        Assert.Equal((2048, 1024), (picture.Width, picture.Height));
    }
}
