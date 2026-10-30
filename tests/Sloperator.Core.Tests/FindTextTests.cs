using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Sloperator.Core.Platform;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

public class FindTextTests
{
    private static TextLine Line(params (string Text, double X)[] words) =>
        new(string.Join(" ", words.Select(w => w.Text)), words.Select(w => new TextWord(w.Text, w.X, 100, w.Text.Length * 8, 16)).ToList());

    [Fact]
    public void Text_is_found_as_words_or_a_run_of_them_whatever_its_case_and_spaces()
    {
        var lines = new[] { Line(("Export", 10), ("settings", 70)), Line(("AB-123", 10), ("EW-611", 130)), Line(("Save", 10), ("As", 50)) };

        var one = Assert.Single(TextSearch.Find(lines, "ew-611", fuzzy: false));
        Assert.Equal(("EW-611", 130.0, 48.0), (one.Text, one.X, one.Width));
        var run = Assert.Single(TextSearch.Find(lines, "Export Settings", fuzzy: false));
        Assert.Equal((10.0, 124.0), (run.X, run.Width)); // from Export's left edge to settings' right one
        Assert.Equal("Save As", Assert.Single(TextSearch.Find(lines, "saveas", fuzzy: false)).Text);
        Assert.Empty(TextSearch.Find(lines, "EW-617", fuzzy: false));
        // Read twice (at two sizes), a place is still one.
        Assert.Single(TextSearch.Find([.. lines, lines[1]], "EW-611", fuzzy: false));
    }

    [Fact]
    public void A_reading_with_letters_recognition_confuses_is_offered_when_the_text_isnt_there()
    {
        var lines = new[] { Line(("EW-6l1", 10)), Line(("Tools", 10)) };
        Assert.Empty(TextSearch.Find(lines, "EW-611", fuzzy: false));
        Assert.Equal("EW-6l1", Assert.Single(TextSearch.Find(lines, "EW-611", fuzzy: true)).Text);
        // Windows read "VY" in a table as "w": two letters off, the closest reading first.
        var table = new[] { Line(("w-643", 10), ("VY-918", 110), ("WY-648", 210)) };
        Assert.Equal(["w-643", "WY-648"], TextSearch.Find(table, "VY-643", fuzzy: true).Select(m => m.Text));

        var space = new ScreenSpace(new ScreenRect(0, 0, 2560, 1080), (16384, 1080));
        var report = FindTextTool.Report("EW-611", lines, space);
        Assert.Contains("isn't in the screen's text as written", report);
        Assert.Contains("\"EW-6l1\"", report);
        Assert.Contains("isn't in the screen's text (2 lines read)", FindTextTool.Report("Cancel", lines, space));
    }

    [Fact]
    public void Places_are_given_in_screenshot_coordinates()
    {
        // A 4K screen goes at about half its size (1920x1088): the box is mapped as a click's x/y would be.
        var lines = new[] { new TextLine("Delete", [new TextWord("Delete", 2000, 1000, 100, 20)]) };
        var space = new ScreenSpace(new ScreenRect(0, 0, 3840, 2160), (16384, 1080));
        var report = FindTextTool.Report("delete", lines, space);
        var (x0, y0) = space.ToModel(2000, 1000);
        var (x1, y1) = space.ToModel(2100, 1020);
        Assert.Contains("on the screen once", report);
        Assert.Contains($"({(x0 + x1) / 2}, {(y0 + y1) / 2}): \"Delete\", box ({x0}, {y0}) to ({x1}, {y1})", report);
        Assert.Equal((1000, 1050), (x0, x1));
    }

    [Fact]
    public async Task Without_text_recognition_the_tool_says_so()
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var result = await new FindTextTool().ExecuteAsync(new JsonObject { ["text"] = "OK" }, ctx, CancellationToken.None);
        Assert.True(result.IsError);
    }

    private sealed class NoLanguageReader : ITextReader
    {
        public IReadOnlyList<TextLine>? Read(RawImage image) => null;
        public IReadOnlyList<string> Languages() => [];
        public Task<TextInstall> InstallAsync(CancellationToken ct) => Task.FromResult(TextInstall.Failed);
    }

    [Fact]
    public async Task In_the_app_the_offer_is_a_card_and_the_model_is_told_not_to_ask()
    {
        var offers = 0;
        var ctx = new ToolContext { Platform = new FakePlatform { Text = new NoLanguageReader() }, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(),
            OfferTextRecognition = () => offers++ };
        var result = await new FindTextTool().ExecuteAsync(new JsonObject { ["text"] = "OK" }, ctx, CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Equal(1, offers);
        Assert.Contains("don't ask them about it yourself, and don't install it", result.Text);
        Assert.DoesNotContain("Add-WindowsCapability", result.Text);
    }

    [Fact]
    public async Task Without_a_language_the_agent_is_told_to_offer_installing_one()
    {
        var ctx = new ToolContext { Platform = new FakePlatform { Text = new NoLanguageReader() }, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var result = await new FindTextTool().ExecuteAsync(new JsonObject { ["text"] = "OK" }, ctx, CancellationToken.None);
        Assert.True(result.IsError);
        // Install, skip for now, or turn it off for good in Settings.
        Assert.Contains("Only if they choose to install", result.Text);
        Assert.Contains("skip it for now (you will offer it again each time this comes up)", result.Text);
        Assert.DoesNotContain("once", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Settings → General → Use text recognition", result.Text);
        Assert.Contains("-Verb RunAs", result.Text); // installing a capability needs an administrator
        Assert.Contains("first tell them that Windows will now show an administrator prompt", result.Text); // the user is ready for it
        Assert.Contains("-ExecutionPolicy Bypass", result.Text); // a restricted execution policy doesn't stop it
        Assert.Contains("Add-WindowsCapability -Online -Name", result.Text);
    }

#if WINDOWS
    [Fact]
    public async Task Windows_reads_interface_text_off_the_screen()
    {
        // Small interface text in Segoe UI, as on the click benchmark's screens.
        var platform = new FakePlatform { ScreenBounds = new ScreenRect(0, 0, 900, 200), Text = new Sloperator.Platform.Windows.WindowsTextReader(), Desktop = Draw(900, 200) };
        var probe = platform.Text.Read(platform.Desktop!);
        Assert.SkipWhen(probe is null, "No text recognition language is installed.");
        Assert.NotEmpty(platform.Text.Languages()); // what Settings shows
        var ctx = new ToolContext { Platform = platform, Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };

        var result = await new FindTextTool().ExecuteAsync(new JsonObject { ["text"] = "VY-643" }, ctx, CancellationToken.None);

        Assert.False(result.IsError, result.Text);
        Assert.True(result.Text.Contains("on the screen once"), result.Text + "\nRead: " + string.Join(" | ", probe!.Select(l => l.Text)));
        // Drawn from x 410, its middle is near 440; the line's other codes are read too.
        var x = int.Parse(System.Text.RegularExpressions.Regex.Match(result.Text, @"\((\d+), \d+\)").Groups[1].Value);
        Assert.InRange(x, 420, 460);
    }

    private static RawImage Draw(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKPaint { Typeface = SKTypeface.FromFamilyName("Segoe UI"), TextSize = 12, IsAntialias = true, Color = new SKColor(20, 22, 26) })
        {
            canvas.Clear(SKColors.White);
            string[] codes = ["AB-123", "CD-456", "EF-789", "GH-012", "VY-643", "KL-345"];
            for (var i = 0; i < codes.Length; i++)
                canvas.DrawText(codes[i], 10 + i * 100, 100, font);
        }
        var pixels = new byte[width * height * 4];
        Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return new RawImage(width, height, pixels);
    }
#endif
}
