using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Mcp;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using BlackBox.McpTestServer;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>MCP support: the config file, how tools are shown to the model, and a real MCP server running in-process.</summary>
public class McpTests
{
    // ---- Data/mcp.json ---------------------------------------------------------------

    [Fact]
    public void The_standard_config_shape_is_understood()
    {
        var config = McpConfig.Parse("""
            {
              // comments and trailing commas are fine: people paste from READMEs
              "mcpServers": {
                "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Docs"], "env": { "DEBUG": "1" }, "cwd": "C:\\" },
                "remote": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer t" } },
                "old": { "type": "sse", "url": "http://localhost:3000/sse" },
                "off": { "command": "uvx", "args": ["something"], "disabled": true },
              }
            }
            """);

        Assert.Equal(["files", "remote", "old", "off"], config.Servers.Select(s => s.Name));
        var files = config.Servers[0];
        Assert.Equal(McpTransport.Stdio, files.Transport);
        Assert.Equal("npx", files.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", "C:\\Docs"], files.Args);
        Assert.Equal("1", files.Env["DEBUG"]);
        Assert.Equal("C:\\", files.Cwd);
        Assert.Equal(McpTransport.Http, config.Servers[1].Transport);
        Assert.Equal("Bearer t", config.Servers[1].Headers["Authorization"]);
        Assert.Equal(McpTransport.Sse, config.Servers[2].Transport);
        Assert.True(config.Servers[3].Disabled);
    }

    [Fact]
    public void Other_spellings_of_the_config_are_accepted()
    {
        // VS Code's key, "type": "http", a single string instead of an args list, "enabled": false.
        var vscode = McpConfig.Parse("""{"servers": {"a": {"type": "http", "url": "http://x/mcp"}, "b": {"command": "node", "args": "server.js --port 1", "enabled": false}}}""");
        Assert.Equal(McpTransport.StreamableHttp, vscode.Servers[0].Transport);
        Assert.Equal(["server.js", "--port", "1"], vscode.Servers[1].Args);
        Assert.True(vscode.Servers[1].Disabled);

        // Just the inner map.
        Assert.Equal("c", McpConfig.Parse("""{"c": {"command": "x"}}""").Servers.Single().Name);

        // Nothing configured, in every form.
        Assert.Empty(McpConfig.Parse("").Servers);
        Assert.Empty(McpConfig.Parse("{}").Servers);
        Assert.Empty(McpConfig.Parse(McpConfig.Template).Servers);
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[1,2]", "must be a JSON object")]
    [InlineData("""{"foo": 1}""", "No \"mcpServers\" entry")]
    [InlineData("""{"mcpServers": {"a": {}}}""", "needs either \"command\"")]
    [InlineData("""{"mcpServers": {"a": {"command": "x", "url": "http://y"}}}""", "both \"command\" and \"url\"")]
    [InlineData("""{"mcpServers": {"a": {"command": "x", "type": "grpc"}}}""", "unknown type \"grpc\"")]
    [InlineData("""{"mcpServers": {"a": {"url": "example.com/mcp"}}}""", "full http:// or https:// address")]
    [InlineData("""{"mcpServers": {"a": {"type": "sse", "command": "x"}}}""", "has no \"url\"")]
    [InlineData("""{"mcpServers": {"a": {"command": "x", "args": [{"b": 1}]}}}""", "every entry of \"args\" must be a string")]
    [InlineData("""{"mcpServers": {"a": {"command": "x", "env": "A=1"}}}""", "\"env\" must be an object")]
    [InlineData("""{"mcpServers": {"a": "npx"}}""", "must be an object with \"command\" or \"url\"")]
    [InlineData("""{"mcpServers": {"": {"command": "x"}}}""", "empty name")]
    public void Mistakes_in_the_config_are_explained(string json, string expected)
    {
        var ex = Assert.Throws<McpConfigException>(() => McpConfig.Parse(json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void The_config_file_round_trips_as_typed()
    {
        var dir = Directory.CreateTempSubdirectory("bb-mcp-").FullName;
        var path = Path.Combine(dir, "Data", "mcp.json");
        Assert.Equal(McpConfig.Template, McpConfig.Load(path)); // no file yet
        const string text = "{\n  // mine\n  \"mcpServers\": {}\n}";
        McpConfig.Save(path, text);
        Assert.Equal(text, McpConfig.Load(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void Saving_the_settings_keeps_what_the_agent_changed_while_they_were_open()
    {
        const string opened = """{ "mcpServers": { "files": { "command": "npx", "args": ["fs"] }, "old": { "command": "x" } } }""";
        // Meanwhile the agent added blender and removed old.
        var current = McpConfig.WithoutServer(McpConfig.WithServer(opened, "blender", new JsonObject { ["command"] = "uvx", ["args"] = new JsonArray("mcp-for-blender") }), "old")!;

        // The user changed another server: both changes stay.
        const string mine = """{ "mcpServers": { "files": { "command": "npx", "args": ["fs", "C:\\Docs"] }, "old": { "command": "x" } } }""";
        var merged = McpConfig.Parse(McpConfig.Merge(opened, current, mine)).Servers;
        Assert.Equal(["files", "blender"], merged.Select(s => s.Name));
        Assert.Equal(["fs", "C:\\Docs"], merged[0].Args);
        Assert.Equal(["mcp-for-blender"], merged[1].Args);

        // Both changed the same server: the user's edit wins.
        const string mineToo = """{ "mcpServers": { "files": { "command": "npx", "args": ["fs"] }, "old": { "command": "y" } } }""";
        var both = McpConfig.Parse(McpConfig.Merge(opened, current, mineToo)).Servers;
        Assert.Equal(["files", "old", "blender"], both.Select(s => s.Name));
        Assert.Equal("y", both[1].Command);

        // Nothing changed underneath: the user's text as typed, comments and all.
        const string typed = "{ // mine\n  \"mcpServers\": {}\n}";
        Assert.Equal(typed, McpConfig.Merge(opened, opened, typed));
    }

    // ---- how a tool looks to the model ----------------------------------------------

    [Fact]
    public void Tool_names_carry_the_server_and_only_safe_characters()
    {
        Assert.Equal("files__read_file", McpTool.ExposedName("files", "read_file"));
        Assert.Equal("My_Server__web_search", McpTool.ExposedName("My Server", "web.search"));
        Assert.Equal("brave__", McpTool.Prefix("brave!"));
        Assert.Equal("x__x", McpTool.ExposedName("", "???"));
        var longName = McpTool.ExposedName(new string('s', 40), new string('t', 40));
        Assert.True(longName.Length <= McpTool.MaxNameLength);
        Assert.StartsWith(new string('s', 40) + "__", longName);
        // Two long names that are the same once cut keep the number that tells them apart: cut off
        // with the rest, it was lost, and naming them went round for ever.
        var second = McpTool.ExposedName(new string('s', 40), new string('t', 40), "_2");
        Assert.NotEqual(longName, second);
        Assert.EndsWith("_2", second);
        Assert.True(second.Length <= McpTool.MaxNameLength);
    }

    [Fact]
    public void Schemas_get_the_shape_the_request_and_the_argument_repair_expect()
    {
        var withDollar = JsonSerializer.Deserialize<JsonElement>("""{"$schema": "http://json-schema.org/draft-07/schema#", "type": "object", "properties": {"q": {"type": "string"}}, "required": ["q"]}""");
        var schema = McpTool.NormalizeSchema(withDollar);
        Assert.Null(schema["$schema"]);
        Assert.Equal("string", schema["properties"]!["q"]!["type"]!.GetValue<string>());
        Assert.Equal("q", schema["required"]![0]!.GetValue<string>());

        var bare = McpTool.NormalizeSchema(JsonSerializer.Deserialize<JsonElement>("""{"type": "object"}"""));
        Assert.Equal("object", bare["type"]!.GetValue<string>());
        Assert.NotNull(bare["properties"]);
        Assert.Empty(bare["properties"]!.AsObject());
    }

    [Fact]
    public void Results_become_text_and_the_first_image_rides_along()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "line one" },
                ImageContentBlock.FromBytes(TestTools.Png, "image/png"),
                ImageContentBlock.FromBytes(TestTools.Png, "image/png"),
                new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "file:///a.txt", Text = "embedded text" } },
                new ResourceLinkBlock { Uri = "file:///b.txt", Name = "b" },
            ],
        };
        var mapped = McpTool.ToToolResult(result);
        Assert.Equal("line one\nembedded text\nResource: file:///b.txt (b)\n(1 more image left out)", mapped.Text);
        Assert.Equal(TestTools.Png, mapped.Image);
        Assert.Equal("image/png", mapped.ImageMime);
        Assert.False(mapped.IsError);

        var failed = McpTool.ToToolResult(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "nope" }] });
        Assert.True(failed.IsError);
        Assert.Equal("nope", failed.Text);

        var structured = McpTool.ToToolResult(new CallToolResult { Content = [], StructuredContent = JsonSerializer.Deserialize<JsonElement>("""{"sum":42}""") });
        Assert.Equal("""{"sum":42}""", structured.Text);

        Assert.Equal("(no output)", McpTool.ToToolResult(new CallToolResult { Content = [] }).Text);

        var huge = McpTool.ToToolResult(new CallToolResult { Content = [new TextContentBlock { Text = new string('x', 100_000) }] });
        Assert.True(huge.Text.Length < 61_000);
        Assert.EndsWith("[output truncated]", huge.Text);
    }

    [Fact]
    public void Read_only_tools_are_the_only_safe_ones()
    {
        static McpTool Make(ToolAnnotations? annotations) => new("srv", "srv__t",
            new ModelContextProtocol.Protocol.Tool { Name = "t", Description = "d", Annotations = annotations },
            (_, _) => Task.FromResult(new CallToolResult()));

        Assert.Equal(ToolRisk.ReadOnly, Make(new ToolAnnotations { ReadOnlyHint = true }).Risk);
        Assert.Equal(ToolRisk.Risky, Make(new ToolAnnotations { ReadOnlyHint = false }).Risk);
        Assert.Equal(ToolRisk.Risky, Make(null).Risk);
    }

    [Fact]
    public async Task A_server_failure_during_a_call_is_an_error_result_not_a_crash()
    {
        var tool = new McpTool("srv", "srv__t", new ModelContextProtocol.Protocol.Tool { Name = "t" },
            (_, _) => throw new IOException("pipe closed"));
        var result = await tool.ExecuteAsync([], Ctx(), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("pipe closed", result.Text);
        Assert.Contains("srv", result.Text);
    }

    // ---- a real MCP server, in-process ------------------------------------------------

    /// <summary>A test server over in-memory pipes; <see cref="McpHost"/> connects to it like to any other.</summary>
    private sealed class InProcessServer : IAsyncDisposable
    {
        private readonly List<Task> _runs = [];
        private readonly List<McpServer> _servers = [];

        public IClientTransport Start(string name)
        {
            var toServer = new Pipe();
            var toClient = new Pipe();
            var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream(), name), TestTools.ServerOptions(name));
            _servers.Add(server);
            _runs.Add(server.RunAsync());
            return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var s in _servers) await s.DisposeAsync();
        }
    }

    private static McpServerConfig Server(string name) => new() { Name = name, Command = "in-process" };

    private static async Task<(McpHost Host, InProcessServer Servers)> ConnectAsync(params string[] names)
    {
        var servers = new InProcessServer();
        var host = new McpHost { TransportFactory = c => servers.Start(c.Name) };
        await host.ConnectAsync(names.Select(Server).ToList(), CancellationToken.None);
        return (host, servers);
    }

    private static ToolContext Ctx() => new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };

    private static (AgentSession Session, RecordingObserver Observer) Session(McpHost host, ScriptedServer model, AgentSettings? settings = null)
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = settings ?? new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var registry = ToolRegistry.CreateDefault(includeScreenTools: false, extra: host.Tools);
        var prompt = SystemPrompt.Build("Test OS", null, "C:\\", "", host.Connected);
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(model), "http://test", null), new ChatRequestOptions(), registry, ctx, prompt);
        return (session, new RecordingObserver());
    }

    [Fact]
    public async Task Connecting_lists_the_tools_under_the_servers_name()
    {
        var (host, servers) = await ConnectAsync("test");
        await using var _ = host;
        await using var __ = servers;

        var status = Assert.Single(host.Statuses);
        Assert.Equal(McpServerState.Connected, status.State);
        Assert.Equal(["add", "echo", "fail", "picture", "sleep", "whoami"], status.ToolNames); // by name, whatever order the server uses
        Assert.Equal(status.ToolNames.Select(n => "test__" + n), host.Tools.Select(t => t.Name));
        Assert.Equal(TestTools.Instructions, status.Instructions);
        Assert.Contains("test__add", host.Tools.Select(t => t.Name));

        var add = host.Tools.Single(t => t.Name == "test__add");
        Assert.Equal("Adds two integers and returns the sum.", add.Description);
        Assert.Equal("integer", add.Parameters["properties"]!["a"]!["type"]!.GetValue<string>());
        Assert.Equal(ToolRisk.Risky, add.Risk);
        Assert.Equal(ToolRisk.ReadOnly, host.Tools.Single(t => t.Name == "test__echo").Risk);

        var prompt = SystemPrompt.Build("Test OS", null, "C:\\", "", host.Connected);
        Assert.Contains("test__*: 6 tools. " + TestTools.Instructions, prompt);
    }

    [Fact]
    public async Task Connecting_again_while_a_server_starts_ends_that_start_at_once()
    {
        // The settings changed again while a server started (npx downloading it): the first start
        // went on for its 90 seconds, and a server that came up after its connection was stopped
        // ran on with nobody to end it.
        await using var servers = new InProcessServer();
        var unanswered = new List<Pipe>();
        var host = new McpHost
        {
            TransportFactory = c =>
            {
                if (c.Name != "slow")
                    return servers.Start(c.Name);
                var (toServer, toClient) = (new Pipe(), new Pipe());
                unanswered.Add(toClient); // no server reads or answers
                return new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream());
            },
        };
        await using var _ = host;
        var first = host.ConnectAsync([Server("slow")], CancellationToken.None);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(first.IsCompleted);

        await host.ConnectAsync([Server("test")], CancellationToken.None);
        await first.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var status = Assert.Single(host.Statuses);
        Assert.Equal(("test", McpServerState.Connected), (status.Name, status.State));
    }

    [Fact]
    public async Task The_model_calls_mcp_tools_like_any_other()
    {
        var (host, servers) = await ConnectAsync("test");
        await using var _ = host;
        await using var __ = servers;
        var model = new ScriptedServer(
            Sse.ToolCall("c1", "test__echo", """{"text": "hi"}"""),
            Sse.ToolCall("c2", "add", """{"a": "17", "b": 25}"""), // bare name, and a number sent as text
            Sse.ToolCall("c3", "test__fail", "{}"),
            Sse.ToolCall("c4", "test__picture", "{}"),
            Sse.Text("Done."));
        var (session, observer) = Session(host, model);

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(AgentStopReason.Completed, observer.StopReason);
        Assert.Equal(["test__echo", "add", "test__fail", "test__picture"], observer.ToolsRun);
        Assert.Equal("echo: hi", observer.Results[0].Text);
        Assert.Equal("42", observer.Results[1].Text);
        Assert.True(observer.Results[2].IsError);
        Assert.Equal("It broke.", observer.Results[2].Text);
        Assert.True(observer.Results[3].Image is not null, "no image; the tool said: " + observer.Results[3].Text);
        Assert.Equal(TestTools.Png, observer.Results[3].Image);

        // The request offered the tool with its schema, and the picture reached the model as an image.
        var tools = model.Requests[0]["tools"]!.AsArray().Select(t => t!["function"]!).ToList();
        var echo = tools.Single(t => t["name"]!.GetValue<string>() == "test__echo");
        Assert.Equal("string", echo["parameters"]!["properties"]!["text"]!["type"]!.GetValue<string>());
        var last = model.Requests[^1]["messages"]!.AsArray()[^1]!;
        Assert.Equal("user", last["role"]!.GetValue<string>());
        Assert.Contains("image_url", last["content"]!.ToJsonString());
    }

    [Fact]
    public async Task The_agent_sets_up_a_server_itself_and_uses_its_tools_in_the_same_run()
    {
        var servers = new InProcessServer();
        await using var _ = servers;
        await using var host = new McpHost
        {
            TransportFactory = c => c.Name == "broken" ? throw new InvalidOperationException("no such program") : servers.Start(c.Name),
        };
        var file = Path.Combine(Directory.CreateTempSubdirectory("bb-mcp-").FullName, "mcp.json");
        McpConfig.Save(file, """{ "mcpServers": { "other": { "command": "x", "disabled": true } } }""");
        var model = new ScriptedServer(
            Sse.ToolCall("c1", "mcp_servers", """{"action": "add", "name": "broken", "command": "nothing.exe"}"""),
            Sse.ToolCall("c2", "mcp_servers", """{"action": "add", "name": "test", "command": "C:\\tools\\uv\\uvx.exe", "args": ["test-mcp"], "env": {"TOKEN": "t"}}"""),
            Sse.ToolCall("c3", "test__add", """{"a": 2, "b": 3}"""),
            Sse.ToolCall("c4", "mcp_servers", """{"action": "list"}"""),
            Sse.Text("Done."));
        var setup = new McpSetup(host, file);
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), Mcp = setup };
        var registry = ToolRegistry.CreateDefault(includeScreenTools: false, extra: [new McpServersTool()]);
        setup.Registry = registry;
        var session = new AgentSession(new OpenAiChatClient(new HttpClient(model), "http://test", null), new ChatRequestOptions(), registry, ctx, "sys");
        var observer = new RecordingObserver();

        await session.RunAsync("Set up the test server and add 2 and 3", observer, CancellationToken.None);

        // A server that doesn't start isn't kept, and says why.
        Assert.True(observer.Results[0].IsError);
        Assert.Contains("no such program", observer.Results[0].Text);
        // One that starts is kept in the file the user edits in Settings, and its tools are there from the next request on.
        Assert.Contains("test__add", observer.Results[1].Text);
        Assert.DoesNotContain(model.Requests[1]["tools"]!.AsArray(), t => t!["function"]!["name"]!.GetValue<string>() == "test__add");
        Assert.Contains(model.Requests[2]["tools"]!.AsArray(), t => t!["function"]!["name"]!.GetValue<string>() == "test__add");
        Assert.Equal("5", observer.Results[2].Text);
        Assert.Contains("test: connected, 6 tools", observer.Results[3].Text);
        var saved = McpConfig.Parse(File.ReadAllText(file)).Servers;
        Assert.Equal(["other", "test"], saved.Select(s => s.Name));
        Assert.Equal(@"C:\tools\uv\uvx.exe", saved[1].Command);
        Assert.Equal(["test-mcp"], saved[1].Args);
        Assert.Equal("t", saved[1].Env["TOKEN"]);
        Assert.True(saved[0].Disabled);

        // Removing it stops it and forgets it.
        var removed = await new McpServersTool().ExecuteAsync(new JsonObject { ["action"] = "remove", ["name"] = "test" }, ctx, CancellationToken.None);
        Assert.False(removed.IsError, removed.Text);
        Assert.Equal(["other"], McpConfig.Parse(File.ReadAllText(file)).Servers.Select(s => s.Name));
        Assert.Null(registry.Find("test__add"));
        Assert.Empty(host.Tools);
    }

    [Fact]
    public async Task Approval_follows_the_servers_read_only_annotation()
    {
        var (host, servers) = await ConnectAsync("test");
        await using var _ = host;
        await using var __ = servers;
        var model = new ScriptedServer(
            Sse.ToolCall("c1", "test__echo", """{"text": "x"}"""),
            Sse.ToolCall("c2", "test__add", """{"a": 1, "b": 2}"""),
            Sse.Text("Done."));
        var (session, observer) = Session(host, model, new AgentSettings { Approval = ApprovalMode.AskRisky });

        await session.RunAsync("go", observer, CancellationToken.None);

        Assert.Equal(1, observer.ApprovalRequests); // add only; echo is read-only
    }

    [Fact]
    public async Task Same_tool_on_two_servers_gets_two_names()
    {
        var (host, servers) = await ConnectAsync("alpha", "beta", "alpha!"); // the third cleans up to the first's prefix
        await using var _ = host;
        await using var __ = servers;

        var names = host.Tools.Select(t => t.Name).ToList();
        Assert.Contains("alpha__add", names);
        Assert.Contains("beta__add", names);
        Assert.Contains("alpha__add_2", names);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var registry = ToolRegistry.CreateDefault(false, extra: host.Tools);
        Assert.Null(ToolCallRepair.ResolveTool("add", registry)); // ambiguous, so not guessed
        Assert.Equal("beta__add", ToolCallRepair.ResolveTool("beta__add", registry)!.Name);
    }

    [Fact]
    public async Task A_server_whose_name_fills_the_tool_names_still_gets_one_name_per_tool()
    {
        // Cut to 64 characters, every tool of a server with a 62-character name came out the same,
        // and numbering them went round for ever with the MCP lock held: runs, Settings and closing
        // the app all waited on it.
        var connect = ConnectAsync(new string('s', 62));
        Assert.Same(connect, await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)));
        var (host, servers) = await connect;
        await using var _ = host;
        await using var __ = servers;

        var names = host.Tools.Select(t => t.Name).ToList();
        Assert.True(names.Count > 1);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(names, n => Assert.True(n.Length <= McpTool.MaxNameLength, n));
    }

    [Fact]
    public async Task A_server_that_cannot_start_is_reported_and_the_others_still_work()
    {
        var servers = new InProcessServer();
        await using var _ = servers;
        var host = new McpHost
        {
            TransportFactory = c => c.Name == "broken" ? throw new InvalidOperationException("no such program") : servers.Start(c.Name),
        };
        await using var __ = host;

        await host.ConnectAsync([Server("broken"), Server("good"), new McpServerConfig { Name = "off", Command = "x", Disabled = true }], CancellationToken.None);

        var broken = host.Statuses.Single(s => s.Name == "broken");
        Assert.Equal(McpServerState.Failed, broken.State);
        Assert.Equal("no such program.", broken.Error);
        Assert.Equal(McpServerState.Connected, host.Statuses.Single(s => s.Name == "good").State);
        Assert.Equal(McpServerState.Disabled, host.Statuses.Single(s => s.Name == "off").State);
        Assert.All(host.Tools, t => Assert.Equal("good", t.ServerName));
        Assert.Equal(["good"], host.Connected.Select(s => s.Name));
    }

    [Fact]
    public async Task A_runs_tool_list_is_fixed_even_if_the_servers_change_underneath()
    {
        var (host, servers) = await ConnectAsync("test");
        await using var _ = servers;
        var registry = ToolRegistry.CreateDefault(false, extra: host.Tools);
        var before = registry.Definitions().Select(d => d.Name).ToList();

        // Settings saved with no servers while a run is on: the host stops them.
        await host.ConnectAsync([], CancellationToken.None);
        Assert.Empty(host.Tools);

        Assert.Equal(before, registry.Definitions().Select(d => d.Name));
        var result = await registry.Find("test__echo")!.ExecuteAsync(new JsonObject { ["text"] = "x" }, Ctx(), CancellationToken.None);
        Assert.True(result.IsError);
        Assert.Contains("the server was stopped", result.Text);
        await host.DisposeAsync();
    }
}
