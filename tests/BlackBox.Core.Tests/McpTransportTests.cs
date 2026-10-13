using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Mcp;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>MCP servers the way users configure them: a program started over stdio, and a URL.</summary>
public class McpTransportTests
{
    /// <summary>The test server built next to the tests (a project reference copies it there).</summary>
    private static string TestServerDll => Path.Combine(AppContext.BaseDirectory, "BlackBox.McpTestServer.dll");

    private static ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };

    [Fact]
    public async Task A_stdio_server_is_started_with_env_and_cwd_and_stopped_with_the_host()
    {
        Assert.True(File.Exists(TestServerDll), "test server not built: " + TestServerDll);
        var cwd = Directory.CreateTempSubdirectory("bb-mcp-cwd-").FullName;
        var config = McpConfig.Parse(new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["local"] = new JsonObject
                {
                    ["command"] = "dotnet",
                    ["args"] = new JsonArray(TestServerDll),
                    ["env"] = new JsonObject { ["BB_TEST_ENV"] = "hello" },
                    ["cwd"] = cwd,
                },
            },
        }.ToJsonString()).Servers;
        var host = new McpHost();

        await host.ConnectAsync(config, CancellationToken.None);

        var status = Assert.Single(host.Statuses);
        Assert.True(status.State == McpServerState.Connected, status.Summary);
        var whoami = await host.Tools.Single(t => t.Name == "local__whoami").ExecuteAsync([], Ctx(), CancellationToken.None);
        Assert.Contains("BB_TEST_ENV=hello", whoami.Text);
        Assert.Contains("cwd=" + cwd, whoami.Text);
        var pid = int.Parse(whoami.Text.Split('\n')[0]["pid=".Length..]);
        Assert.NotEqual(Environment.ProcessId, pid);

        var sum = await host.Tools.Single(t => t.Name == "local__add").ExecuteAsync(new JsonObject { ["a"] = 2, ["b"] = 40 }, Ctx(), CancellationToken.None);
        Assert.Equal("42", sum.Text);

        // Disposing the host ends the child process; nothing is left running.
        await host.DisposeAsync();
        var gone = false;
        for (var i = 0; i < 50 && !gone; i++)
        {
            try { gone = Process.GetProcessById(pid).HasExited; }
            catch (ArgumentException) { gone = true; }
            if (!gone) await Task.Delay(100, TestContext.Current.CancellationToken);
        }
        Assert.True(gone, "the server process is still running");
    }

    [Fact]
    public async Task A_command_that_does_not_exist_fails_with_a_message()
    {
        await using var host = new McpHost();
        await host.ConnectAsync([new McpServerConfig { Name = "nope", Command = "bb-no-such-program-xyz", Args = ["--x"] }], CancellationToken.None);

        var status = Assert.Single(host.Statuses);
        Assert.Equal(McpServerState.Failed, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        TestContext.Current.SendDiagnosticMessage(status.Summary);
        Assert.Empty(host.Tools);
    }

    /// <summary>
    /// Opt-in (set BB_MCP_NPX=1; needs Node and the network): a real community server started the
    /// way every README says, with "npx". On Windows npx is a .cmd, which plain process creation
    /// cannot start; this proves the whole path works on this machine.
    /// </summary>
    [Fact]
    public async Task A_real_npx_server_starts_and_answers()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("BB_MCP_NPX") is null, "Set BB_MCP_NPX=1 to run (needs Node and the network).");
        await using var host = new McpHost();
        var config = McpConfig.Parse("""{"mcpServers": {"everything": {"command": "npx", "args": ["-y", "@modelcontextprotocol/server-everything"]}}}""").Servers;

        await host.ConnectAsync(config, CancellationToken.None);

        var status = Assert.Single(host.Statuses);
        Assert.True(status.State == McpServerState.Connected, status.Summary);
        TestContext.Current.SendDiagnosticMessage(status.Summary);
        Assert.NotEmpty(host.Tools);
        // Its tool set changes between releases; add and echo have been there from the start.
        var tool = host.Tools.FirstOrDefault(t => t.OriginalName == "add") ?? host.Tools.FirstOrDefault(t => t.OriginalName == "echo");
        Assert.True(tool is not null, "no add or echo tool; the server offers: " + string.Join(", ", status.ToolNames));
        var args = tool.OriginalName == "add" ? new JsonObject { ["a"] = 40, ["b"] = 2 } : new JsonObject { ["message"] = "forty-two" };
        var result = await tool.ExecuteAsync(args, Ctx(), CancellationToken.None);
        Assert.False(result.IsError, result.Text);
        Assert.Contains(tool.OriginalName == "add" ? "42" : "forty-two", result.Text);
    }

    [Fact]
    public async Task An_http_server_gets_the_configured_headers()
    {
        await using var fake = new FakeHttpMcpServer();
        await using var host = new McpHost();
        var config = McpConfig.Parse(new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["web"] = new JsonObject
                {
                    ["url"] = fake.Url + "mcp",
                    ["headers"] = new JsonObject { ["Authorization"] = "Bearer t0k", ["X-Test"] = "1" },
                },
            },
        }.ToJsonString()).Servers;

        await host.ConnectAsync(config, CancellationToken.None);

        var status = Assert.Single(host.Statuses);
        Assert.True(status.State == McpServerState.Connected, status.Summary);
        Assert.Equal(["ping"], status.ToolNames);
        var result = await host.Tools.Single().ExecuteAsync([], Ctx(), CancellationToken.None);
        Assert.Equal("pong", result.Text);
        Assert.All(fake.Requests, r => Assert.Equal("Bearer t0k", r.Authorization));
        Assert.All(fake.Requests, r => Assert.Equal("1", r.XTest));
        Assert.Contains("tools/call", fake.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task An_http_server_that_is_not_there_fails_with_a_message()
    {
        await using var host = new McpHost();
        await host.ConnectAsync([new McpServerConfig { Name = "down", Url = "http://127.0.0.1:9/mcp", Transport = McpTransport.StreamableHttp }], CancellationToken.None);

        var status = Assert.Single(host.Statuses);
        Assert.Equal(McpServerState.Failed, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
        TestContext.Current.SendDiagnosticMessage(status.Summary);
    }

    /// <summary>
    /// The smallest Streamable HTTP MCP server: JSON-RPC over POST with plain JSON answers (the
    /// protocol allows that instead of an event stream). Records the headers of every request.
    /// </summary>
    internal sealed class FakeHttpMcpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public FakeHttpMcpServer()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _loop = Task.Run(ServeAsync);
        }

        public string Url { get; }
        public List<(string Method, string? Authorization, string? XTest)> Requests { get; } = [];

        private static int FreePort()
        {
            using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        private async Task ServeAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) when (_cts.IsCancellationRequested) { return; }
                catch (HttpListenerException) { return; }
                try { await HandleAsync(ctx); }
                catch (Exception ex) { ctx.Response.StatusCode = 500; await WriteAsync(ctx, ex.Message); }
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            if (req.HttpMethod == "GET") { ctx.Response.StatusCode = 405; ctx.Response.Close(); return; } // no server-initiated stream
            if (req.HttpMethod == "DELETE") { ctx.Response.StatusCode = 200; ctx.Response.Close(); return; }
            using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            var method = body["method"]!.GetValue<string>();
            lock (Requests) Requests.Add((method, req.Headers["Authorization"], req.Headers["X-Test"]));

            JsonNode? result = method switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = body["params"]!["protocolVersion"]!.DeepClone(),
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "fake", ["version"] = "1" },
                },
                "tools/list" => new JsonObject
                {
                    ["tools"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "ping",
                        ["description"] = "Answers pong.",
                        ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                    }),
                },
                "tools/call" => new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "pong" }) },
                "ping" => new JsonObject(),
                _ => null,
            };
            if (body["id"] is null)
            {
                ctx.Response.StatusCode = 202; // a notification
                ctx.Response.Close();
                return;
            }
            // Like any server from before the newer handshake (server/discover): unknown methods get the JSON-RPC
            // "method not found" error, which makes the client fall back to initialize.
            var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = body["id"]!.DeepClone() };
            if (result is not null) reply["result"] = result;
            else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found: " + method };
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["Mcp-Session-Id"] = "fake-session";
            await WriteAsync(ctx, reply.ToJsonString());
        }

        private static async Task WriteAsync(HttpListenerContext ctx, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try { await _loop; } catch (Exception) { }
            _listener.Close();
        }
    }
}
