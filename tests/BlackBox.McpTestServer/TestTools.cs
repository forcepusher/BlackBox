using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace BlackBox.McpTestServer;

/// <summary>The tools of the test server, shared by the stdio executable and the in-process tests.</summary>
public static class TestTools
{
    public const string Instructions = "add returns the exact sum; trust it over mental arithmetic.";

    /// <summary>A 1x1 red PNG.</summary>
    public static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8DwHwAFBQIAX8jx0gAAAABJRU5ErkJggg==");

    public static McpServerOptions ServerOptions(string name = "bb-test") => new()
    {
        ServerInfo = new Implementation { Name = name, Version = "1.0" },
        ServerInstructions = Instructions,
        ToolCollection = Collection(),
    };

    public static McpServerPrimitiveCollection<McpServerTool> Collection()
    {
        var tools = new McpServerPrimitiveCollection<McpServerTool>
        {
            McpServerTool.Create(([Description("The text to repeat")] string text) => "echo: " + text,
                new McpServerToolCreateOptions { Name = "echo", Description = "Repeats the text back.", ReadOnly = true }),
            McpServerTool.Create(([Description("First number")] int a, [Description("Second number")] int b) => a + b,
                new McpServerToolCreateOptions { Name = "add", Description = "Adds two integers and returns the sum." }),
            McpServerTool.Create(() => new CallToolResult
            {
                Content =
                [
                    new TextContentBlock { Text = "Here is a picture." },
                    ImageContentBlock.FromBytes(Png, "image/png"),
                ],
            }, new McpServerToolCreateOptions { Name = "picture", Description = "Returns a small image.", ReadOnly = true }),
            McpServerTool.Create(() => new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = "It broke." }],
            }, new McpServerToolCreateOptions { Name = "fail", Description = "Always fails." }),
            McpServerTool.Create(() =>
                $"pid={Environment.ProcessId}\ncwd={Environment.CurrentDirectory}\nBB_TEST_ENV={Environment.GetEnvironmentVariable("BB_TEST_ENV")}",
                new McpServerToolCreateOptions { Name = "whoami", Description = "Reports the server process.", ReadOnly = true }),
            McpServerTool.Create(async ([Description("Seconds to wait")] double seconds, CancellationToken ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
                return "slept";
            }, new McpServerToolCreateOptions { Name = "sleep", Description = "Waits." }),
        };
        return tools;
    }
}
