using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: a run on Kilo's free Nemotron, at the 1M Sloperator assumes, grown past what Kilo takes,
/// must recover and finish. Set BB_KILO=1 to run them; no key is needed. Each sends two or three
/// requests (refused, a handoff note for text, retry) of Kilo's 200 an hour, one of them over 20 MB.
/// </summary>
public class KiloOverflowTests
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("BB_KILO") == "1";

    private static AgentSession NewSession()
    {
        Assert.SkipUnless(Enabled, "Set BB_KILO=1 to run against Kilo's free Nemotron.");
        var server = new ServerSettings { BaseUrl = KiloGateway.Address, Model = KiloGateway.Model };
        var connection = BackendConnection.Create(LlmHttp.Create(TimeSpan.FromMinutes(20)), server);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(false), ctx,
            "You are a test assistant.", contextTokens: ServerSettings.FallbackContextSize);
        session.History.Add(new ChatMessage { Role = ChatRole.User, Content = "Remember: the password is BLUEFISH.", FromUser = true });
        return session;
    }

    private static void AddStep(AgentSession session, int i, string result, ContentPart? screenshot = null)
    {
        session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "read_file", Arguments = $$"""{"path":"log{{i}}.txt"}""" }] });
        session.History.Add(ChatMessage.ToolResult($"c{i}", result));
        if (screenshot is not null)
            session.History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(AgentSession.ScreenshotText), screenshot] });
    }

    private static async Task<RecordingObserver> AskPasswordAsync(AgentSession session)
    {
        var observer = new RecordingObserver();
        await session.RunAsync("Without using tools, reply with just the password I gave you at the start.", observer, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage("Notices: " + string.Join(" | ", observer.Notices) + " | context now " + session.ContextTokens);
        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        Assert.Contains("BLUEFISH", session.History[^1].Content, StringComparison.OrdinalIgnoreCase);
        return observer;
    }

    /// <summary>
    /// 4.6 MB of text: Kilo's gateway counts a fourth of the characters, 1.15M tokens, and refuses
    /// it naming 1M, while Nemotron's own count (the usage Sloperator goes by) is about 700k, under
    /// the 800k at which the run would have reset by itself.
    /// </summary>
    [Fact]
    public async Task Text_past_Kilos_count_at_the_1M_assumed_is_recovered()
    {
        var session = NewSession();
        var chars = 0;
        for (var i = 0; chars < 4_600_000; i++)
        {
            var text = $"Log page {i}: the quick brown fox number {i} jumped over lazy dog number {i * 7}. " +
                       string.Concat(Enumerable.Repeat($"Entry {i}: nothing unusual happened. ", 60));
            AddStep(session, i, text);
            chars += text.Length;
        }
        session.History.FindLast(m => m.Role == ChatRole.Assistant)!.PromptTokens = 700_000;

        var observer = await AskPasswordAsync(session);

        Assert.Contains(observer.Notices, n => n.Contains("refused") && n.Contains("as too long"));
        Assert.InRange(session.ContextTokens, 16_384, 800_000);
    }

    /// <summary>
    /// Screenshots of 21 MB, few tokens: Kilo takes requests of 20 MB at most and answers 413 with
    /// FUNCTION_PAYLOAD_TOO_LARGE, long before 1M tokens. The screenshots but the latest are left
    /// out and the request sent again.
    /// </summary>
    [Fact]
    public async Task Screenshots_past_Kilos_20_MB_at_the_1M_assumed_are_recovered()
    {
        var session = NewSession();
        long bytes = 0;
        for (var i = 0; bytes * 4 / 3 < 21_000_000; i++)
        {
            var jpeg = Noise(1600, 900, i);
            AddStep(session, i, $"Log page {i}: nothing unusual happened.", ContentPart.FromImage(jpeg, "image/jpeg"));
            bytes += jpeg.Length;
        }
        session.History.FindLast(m => m.Role == ChatRole.Assistant)!.PromptTokens = 60_000;

        var observer = await AskPasswordAsync(session);

        Assert.Contains(observer.Notices, n => n.Contains("more than the server takes"));
        // The screenshots before the latest went; the context wasn't reset, nor made smaller.
        Assert.DoesNotContain(observer.Notices, n => n.Contains("Context reset"));
        Assert.Equal(ServerSettings.FallbackContextSize, session.ContextTokens);
    }

    /// <summary>A picture that doesn't compress: random pixels, about 1.3 MB as a JPEG at 1600 × 900.</summary>
    private static byte[] Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        using var bitmap = new SKBitmap(width, height);
        var pixels = new byte[width * height * 4];
        random.NextBytes(pixels);
        for (var p = 3; p < pixels.Length; p += 4)
            pixels[p] = 255;
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }
}
