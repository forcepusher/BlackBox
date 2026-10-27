using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using SkiaSharp;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>
/// Opt-in: a run on munito with more screenshots than its gateway takes in one request (8) must
/// leave out all but the latest, remember the cap and finish, still seeing the latest screen. Set
/// BB_MUNITO_KEY to a munito API key to run it (BB_MUNITO_MODEL picks the model). Three small
/// requests.
/// </summary>
public class MunitoImageCapTests
{
    private const string Address = "https://api.munito.ai/inference/v1";

    [Fact]
    public async Task More_screenshots_than_munito_takes_leave_only_the_latest()
    {
        var key = Environment.GetEnvironmentVariable("BB_MUNITO_KEY");
        Assert.SkipWhen(string.IsNullOrEmpty(key), "Set BB_MUNITO_KEY to a munito API key to run against munito.");
        var server = new ServerSettings
        {
            BaseUrl = Address,
            ApiKey = key!,
            Model = Environment.GetEnvironmentVariable("BB_MUNITO_MODEL") is { Length: > 0 } model ? model : "deepseek-ai/deepseek-v4.1-flash-1017",
        };
        var connection = BackendConnection.Create(LlmHttp.Create(TimeSpan.FromMinutes(5)), server);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var session = new AgentSession(connection.Client, connection.Options, ToolRegistry.CreateDefault(false), ctx,
            "You are a test assistant.", contextTokens: ServerSettings.FallbackContextSize);
        session.History.Add(new ChatMessage { Role = ChatRole.User, Content = "Take screenshots until the screen turns a colour other than blue.", FromUser = true });
        // Nine screens, as in the run that failed, the last of them red: the one a run must keep.
        AddScreens(session, 0, 9, SKColors.Red);

        var observer = await AskColourAsync(session);
        Assert.Equal(8, session.MaxImages); // what munito took on 28 October 2026
        Assert.Contains(observer.Notices, n => n.Contains("takes 8 pictures in one request"));
        Assert.Contains("red", session.History[^1].Content, StringComparison.OrdinalIgnoreCase);

        // Eight more, past the cap again: left out before the request, which isn't refused.
        AddScreens(session, 9, 8, SKColors.Green);
        observer = await AskColourAsync(session);
        Assert.DoesNotContain(observer.Notices, n => n.Contains("pictures in one request"));
        Assert.Contains("green", session.History[^1].Content, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Screenshot steps, blue screens and the last of them <paramref name="last"/>.</summary>
    private static void AddScreens(AgentSession session, int from, int count, SKColor last)
    {
        for (var i = from; i < from + count; i++)
        {
            session.History.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "", ToolCalls = [new ToolCall { Id = $"c{i}", Name = "screenshot", Arguments = "{}" }] });
            session.History.Add(ChatMessage.ToolResult($"c{i}", "Took a screenshot."));
            session.History.Add(new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(AgentSession.ScreenshotText), ContentPart.FromImage(Screen(i == from + count - 1 ? last : SKColors.Blue), "image/jpeg")] });
        }
    }

    private static async Task<RecordingObserver> AskColourAsync(AgentSession session)
    {
        var observer = new RecordingObserver();
        await session.RunAsync("Without using tools, say in one word which colour the latest screenshot is.", observer, TestContext.Current.CancellationToken);
        TestContext.Current.SendDiagnosticMessage("Notices: " + string.Join(" | ", observer.Notices) + " | max images " + session.MaxImages);
        Assert.True(observer.StopReason == AgentStopReason.Completed, $"{observer.StopReason}: {observer.Detail}");
        return observer;
    }

    /// <summary>A screen of one colour at the size the failed run sent, 2560 × 1088.</summary>
    private static byte[] Screen(SKColor color)
    {
        using var bitmap = new SKBitmap(2560, 1088);
        bitmap.Erase(color);
        using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}
