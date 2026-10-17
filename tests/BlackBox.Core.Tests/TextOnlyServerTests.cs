using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Platform;
using BlackBox.Core.Settings;
using BlackBox.Core.Ssh;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>
/// Opt-in (BB_TEXT_ONLY_URL, BB_TEXT_ONLY_MODEL): a real server whose model takes no pictures, such
/// as Kilo's gateway (https://ai-gateway.kilo.ai/api/v1, no key) with a text-only free model, which
/// answers 404 "No endpoints found that support image input". Only the Unity editor test scene is
/// sent, never this PC's screen.
/// </summary>
public class TextOnlyServerTests
{
    private static readonly string? Url = Environment.GetEnvironmentVariable("BB_TEXT_ONLY_URL");
    private static readonly string? Model = Environment.GetEnvironmentVariable("BB_TEXT_ONLY_MODEL");

    [Fact]
    public async Task A_real_server_that_refuses_pictures_gets_them_in_words_and_the_run_goes_on()
    {
        Assert.SkipWhen(Url is null || Model is null, "Set BB_TEXT_ONLY_URL and BB_TEXT_ONLY_MODEL to run.");
        using var http = LlmHttp.Create(TimeSpan.FromMinutes(5));
        var paths = new AppPaths(Directory.CreateTempSubdirectory("bb-text-only-").FullName);
        using var scene = SkiaSharp.SKBitmap.Decode(Path.Combine(AppContext.BaseDirectory, "ClickScenes", "unity-editor.png"));
        using var bgra = scene.Copy(SkiaSharp.SKColorType.Bgra8888);
        using var jpeg = scene.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80);
        var platform = new FakePlatform { ScreenBounds = new(0, 0, bgra.Width, bgra.Height), Desktop = new RawImage(bgra.Width, bgra.Height, bgra.Bytes) };
        await using var mcp = new McpHost();
        using var ssh = new SshHosts(paths.KnownHostsFile);

        // A chat that holds a picture from a model that could see, carried on with this one, which
        // the server says can't: every message failed.
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Parts = [ContentPart.FromText("Which program is this?"), ContentPart.FromImage(jpeg.ToArray(), "image/jpeg")], FromUser = true },
            new() { Role = ChatRole.Assistant, Content = "The Unity editor." },
        };
        var settings = new ServerSettings { BaseUrl = Url!, Model = Model!, SupportsVision = false };
        var session = AgentSetup.Create(BackendConnection.Create(http, settings), platform, new AgentSettings(), paths, mcp, ssh, history);
        var observer = new RecordingObserver();
        await session.RunAsync("Without any tools, answer in one word: which program did I ask about?", observer, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage("Switched model: " + string.Join(" | ", observer.Notices));
        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        Assert.Contains(observer.Notices, n => n.Contains("doesn't take pictures") && n.Contains("image input"));
        Assert.DoesNotContain(history, m => m.Parts?.Any(p => p.IsImage) == true);

        // A server that didn't say the model can't see: it gets the screen tools, and its first
        // screenshot is refused. The run goes on, and the next ones are set up without.
        settings.SupportsVision = true;
        history = [];
        session = AgentSetup.Create(BackendConnection.Create(http, settings), platform, new AgentSettings(), paths, mcp, ssh, history);
        observer = new RecordingObserver();
        await session.RunAsync("Take a screenshot, then say in one sentence what you could tell from it.", observer, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage("Didn't say: tools " + string.Join(", ", observer.ToolsRun) + " · " + string.Join(" | ", observer.Notices));
        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        if (observer.ToolsRun.Contains("screenshot"))
        {
            Assert.Contains(observer.Notices, n => n.Contains("doesn't take pictures"));
            Assert.False(session.SeesImages);
        }
    }
}
