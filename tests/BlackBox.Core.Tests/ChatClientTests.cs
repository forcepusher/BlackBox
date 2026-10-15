using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

public class ChatClientTests
{
    [Fact]
    public async Task Stream_assembles_content_reasoning_and_split_tool_calls()
    {
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"reasoning_content":"Let me "}}]}""",
            "",
            """data: {"choices":[{"delta":{"reasoning_content":"look."}}]}""",
            """data: {"choices":[{"delta":{"content":"Taking a screenshot."}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"mouse_click","arguments":"{\"x\":"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"10,\"y\":20}"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":1,"id":"call_2","function":{"name":"wait","arguments":"{\"seconds\":1}"}}]}}]}""",
            """data: {"choices":[{"delta":{},"finish_reason":"tool_calls"}]}""",
            "data: [DONE]");

        var deltas = new List<StreamDelta>();
        var msg = await OpenAiChatClient.ReadStreamAsync(new StringReader(sse), deltas.Add, CancellationToken.None);

        Assert.Equal("Taking a screenshot.", msg.Content);
        Assert.Equal("Let me look.", msg.Reasoning);
        Assert.Equal(2, msg.ToolCalls!.Count);
        Assert.Equal("call_1", msg.ToolCalls[0].Id);
        Assert.Equal("mouse_click", msg.ToolCalls[0].Name);
        Assert.Equal("""{"x":10,"y":20}""", msg.ToolCalls[0].Arguments);
        Assert.Equal("wait", msg.ToolCalls[1].Name);
        Assert.Equal(3, deltas.Count);
    }

    [Fact]
    public async Task Leaked_think_tags_move_to_reasoning()
    {
        var sse = """data: {"choices":[{"delta":{"content":"Done, it is open.\n</think>\n\nNotepad is open."}}]}""" + "\ndata: [DONE]";
        var msg = await OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None);
        Assert.Equal("Notepad is open.", msg.Content);
        Assert.Contains("Done, it is open.", msg.Reasoning);
    }

    [Fact]
    public async Task Stream_cut_off_without_done_is_reported_as_a_drop()
    {
        var sse = """data: {"choices":[{"delta":{"content":"Half a sent"}}]}""";
        await Assert.ThrowsAsync<IOException>(() => OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None));
    }

    [Fact]
    public async Task Stream_error_chunk_throws()
    {
        var sse = """data: {"error":{"message":"context too long"}}""";
        var ex = await Assert.ThrowsAsync<LlmException>(() => OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None));
        Assert.Contains("context too long", ex.Message);
    }

    [Fact]
    public void Request_serializes_images_tool_calls_and_extras()
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("sys"),
            new()
            {
                Role = ChatRole.Assistant, Content = "",
                ToolCalls = [new ToolCall { Id = "c1", Name = "screenshot", Arguments = "" }],
            },
            ChatMessage.ToolResult("c1", "ok"),
            new() { Role = ChatRole.User, Parts = [ContentPart.FromText("look"), ContentPart.FromImage([1, 2, 3], "image/jpeg")] },
        };
        var tools = new List<ToolDefinition> { new() { Name = "screenshot", Description = "d", Parameters = new JsonObject { ["type"] = "object" } } };
        var options = new ChatRequestOptions { Model = "m", Extra = new JsonObject { ["top_k"] = 20 } };

        var req = OpenAiChatClient.BuildRequest(messages, tools, options);

        Assert.Equal("m", req["model"]!.GetValue<string>());
        Assert.Equal(20, req["top_k"]!.GetValue<int>());
        Assert.Equal("{}", req["messages"]![1]!["tool_calls"]![0]!["function"]!["arguments"]!.GetValue<string>());
        Assert.Equal("c1", req["messages"]![2]!["tool_call_id"]!.GetValue<string>());
        var image = req["messages"]![3]!["content"]![1]!;
        Assert.Equal("image_url", image["type"]!.GetValue<string>());
        Assert.Equal("data:image/jpeg;base64,AQID", image["image_url"]!["url"]!.GetValue<string>());
        Assert.Equal("screenshot", req["tools"]![0]!["function"]!["name"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080/v1")]
    [InlineData("https://api.example.com/v1/", "https://api.example.com/v1")]
    [InlineData("https://api.example.com/v1/chat/completions", "https://api.example.com/v1")]
    [InlineData("https://api.example.com/v1/models", "https://api.example.com/v1")]
    [InlineData("host:1234", "http://host:1234/v1")]
    [InlineData(" host:1234/v1/ ", "http://host:1234/v1")]
    public void Base_url_is_normalized(string input, string expected) =>
        Assert.Equal(expected, OpenAiChatClient.NormalizeBaseUrl(input));

    [Fact]
    public void Addresses_without_a_scheme_try_both_protocols()
    {
        Assert.Equal(["https://forcepusher.ddns.net:1234/v1", "http://forcepusher.ddns.net:1234/v1"], ServerAddress.Candidates("forcepusher.ddns.net:1234"));
        Assert.Equal(["http://localhost:1234/v1", "https://localhost:1234/v1"], ServerAddress.Candidates("localhost:1234/v1/chat/completions"));
        Assert.Equal(["http://192.168.1.5:8080/v1", "https://192.168.1.5:8080/v1"], ServerAddress.Candidates("192.168.1.5:8080"));
        Assert.Equal(["https://api.openai.com/v1"], ServerAddress.Candidates("https://api.openai.com/"));
        Assert.Empty(ServerAddress.Candidates("  "));
    }

    [Fact]
    public async Task The_probe_finds_the_protocol_that_answers()
    {
        var server = new BlackBox.Core.Settings.ServerSettings { BaseUrl = "host:1234", Model = "m" };
        var http = new HttpClient(new SchemeServer("http"));
        var probe = await ServerProbe.CheckAsync(http, server, CancellationToken.None);
        Assert.True(probe.Reachable);
        Assert.Equal("http://host:1234/v1", probe.BaseUrl);
        Assert.Equal("host:1234", server.BaseUrl); // the caller keeps it, if nobody has typed another address meanwhile
        Assert.Equal(["m"], probe.Models);
    }

    [Fact]
    public async Task Addresses_that_are_not_a_model_server_are_reported_not_thrown()
    {
        Assert.Empty(ServerAddress.Candidates("http://")); // half typed
        var halfTyped = await ServerProbe.CheckAsync(new HttpClient(new SchemeServer("http")), new BlackBox.Core.Settings.ServerSettings { BaseUrl = "http://" }, CancellationToken.None);
        Assert.False(halfTyped.Reachable);
        Assert.Equal("That is not a server address.", halfTyped.Message);

        // A web server (or a captive portal) answers with a page instead of a model list.
        var page = new HttpClient(new HtmlServer());
        var probe = await ServerProbe.CheckAsync(page, new BlackBox.Core.Settings.ServerSettings { BaseUrl = "http://host:80", Model = "m" }, CancellationToken.None);
        Assert.False(probe.Reachable);
        Assert.Contains("not a model list", probe.Message);
        Assert.Null((await ServerProbe.TryContextAsync(page, new BlackBox.Core.Settings.ServerSettings { BaseUrl = "http://host:80", Model = "m" }, CancellationToken.None)).Context);
    }

    private sealed class HtmlServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("<html><body>Welcome</body></html>") });
    }

    /// <summary>Answers /v1/models only over one scheme, like a plain-http LM Studio.</summary>
    private sealed class SchemeServer(string scheme) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.Scheme == scheme && request.RequestUri.AbsolutePath == "/v1/models"
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"id\":\"m\"}]}") })
                : request.RequestUri.Scheme == scheme
                    ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound))
                    : throw new HttpRequestException("The SSL connection could not be established");
    }

    [Fact]
    public void Text_tool_calls_are_extracted_when_server_does_not_parse_them()
    {
        var msg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "I'll click it.\n<tool_call>\n{\"name\": \"mouse_click\", \"arguments\": {\"x\": 5, \"y\": 6}}\n</tool_call>",
        };

        Assert.True(TextToolCallParser.TryExtract(msg, Registry));
        Assert.Equal("I'll click it.", msg.Content);
        Assert.Equal("mouse_click", msg.ToolCalls![0].Name);
        Assert.Equal("""{"x":5,"y":6}""", msg.ToolCalls[0].Arguments);
    }

    [Fact]
    public void Text_tool_calls_for_unknown_tools_are_ignored()
    {
        var msg = new ChatMessage { Role = ChatRole.Assistant, Content = "<tool_call>{\"name\":\"rm_rf\",\"arguments\":{}}</tool_call>" };
        Assert.False(TextToolCallParser.TryExtract(msg, Registry));
    }

    private static readonly ToolRegistry Registry = ToolRegistry.CreateDefault(includeScreenTools: true);

    [Fact]
    public void Qwen_xml_tool_calls_in_text_are_extracted()
    {
        var msg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "Opening it.\n<tool_call>\n<function=mouse_click>\n<parameter=x>\n120\n</parameter>\n<parameter=y>\n45.5\n</parameter>\n</function>\n</tool_call>\n" +
                      "<tool_call>\n<function=type_text>\n<parameter=text>\nhello 42\n</parameter>\n</function>\n</tool_call>",
        };

        Assert.True(TextToolCallParser.TryExtract(msg, Registry));
        Assert.Equal("Opening it.", msg.Content);
        Assert.Equal(2, msg.ToolCalls!.Count);
        Assert.Equal("""{"x":120,"y":45.5}""", msg.ToolCalls[0].Arguments);
        Assert.Equal("""{"text":"hello 42"}""", msg.ToolCalls[1].Arguments);
    }

    [Fact]
    public void Gemma_tool_calls_in_text_are_extracted()
    {
        var msg = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = "<|tool_call>call:type_text{text:<|\"|>Hello, world: \"hi\"<|\"|>}<tool_call|><|tool_call>call:mouse_click{x:120,y:45}<tool_call|>",
        };
        Assert.True(TextToolCallParser.TryExtract(msg, Registry));
        Assert.Equal(2, msg.ToolCalls!.Count);
        Assert.Equal("Hello, world: \"hi\"", System.Text.Json.Nodes.JsonNode.Parse(msg.ToolCalls[0].Arguments)!["text"]!.GetValue<string>());
        Assert.Equal("""{"x":120,"y":45}""", msg.ToolCalls[1].Arguments);
        Assert.Equal("", msg.Content);
    }

    [Fact]
    public async Task Gemma_thought_channel_moves_to_reasoning()
    {
        var sse = """data: {"choices":[{"delta":{"content":"<|channel>thought\nCheck the screen first.<channel|>Taking a look."}}]}""" + "\ndata: [DONE]";
        var msg = await OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None);
        Assert.Equal("Taking a look.", msg.Content);
        Assert.Equal("Check the screen first.", msg.Reasoning);
    }

    [Fact]
    public void Fenced_json_reply_is_treated_as_a_tool_call()
    {
        var msg = new ChatMessage { Role = ChatRole.Assistant, Content = "```json\n{\"name\": \"screenshot\", \"arguments\": {}}\n```" };
        Assert.True(TextToolCallParser.TryExtract(msg, Registry));
        Assert.Equal("screenshot", msg.ToolCalls![0].Name);
        Assert.Equal("", msg.Content);
    }

    [Theory]
    [InlineData("{\"x\": 10, \"y\": 20,}")]                 // trailing comma
    [InlineData("{'x': 10, 'y': 20}")]                    // single quotes
    [InlineData("{x: 10, y: 20}")]                        // unquoted keys
    [InlineData("{\"x\": 10, \"y\": 20")]                  // truncated
    [InlineData("\"{\\\"x\\\": 10, \\\"y\\\": 20}\"")]     // double-encoded
    [InlineData("```json\n{\"x\": 10, \"y\": 20}\n```")]   // fenced
    [InlineData("{\"x\": 10, \"y\": 20} I will click now")] // trailing prose
    public void Sloppy_argument_json_is_repaired(string raw)
    {
        var args = ToolCallRepair.ParseArguments(raw);
        Assert.Equal(10, args["x"]!.GetValue<int>());
        Assert.Equal(20, args["y"]!.GetValue<int>());
    }

    [Fact]
    public void Coordinates_and_aliases_are_normalized()
    {
        var click = ToolCallRepair.NormalizeArguments(ToolCallRepair.ParseArguments("""{"coordinate": [5, 6]}"""), Registry.Find("mouse_click")!);
        Assert.Equal(5, click["x"]!.GetValue<int>());
        Assert.Equal(6, click["y"]!.GetValue<int>());

        var drag = ToolCallRepair.NormalizeArguments(ToolCallRepair.ParseArguments("""{"start": "(1, 2)", "end": [3, 4]}"""), Registry.Find("mouse_drag")!);
        Assert.Equal(1, drag["x"]!.GetValue<double>());
        Assert.Equal(4, drag["to_y"]!.GetValue<int>());

        var shell = ToolCallRepair.NormalizeArguments(ToolCallRepair.ParseArguments("""{"cmd": "dir"}"""), Registry.Find("run_shell")!);
        Assert.Equal("dir", shell["command"]!.GetValue<string>());

        var type = ToolCallRepair.NormalizeArguments(ToolCallRepair.ParseArguments("""{"Text": "hi"}"""), Registry.Find("type_text")!);
        Assert.Equal("hi", type["text"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("mouse_click", "mouse_click")]
    [InlineData("Mouse-Click", "mouse_click")]
    [InlineData("functions.screenshot", "screenshot")]
    [InlineData("double_click", "mouse_click")]
    [InlineData("bash", "run_shell")]
    [InlineData("hotkey", "press_keys")]
    public void Tool_names_are_resolved(string called, string expected) =>
        Assert.Equal(expected, ToolCallRepair.ResolveTool(called, Registry)!.Name);

    [Fact]
    public void Aliases_like_double_click_and_ctrl_click_keep_their_meaning()
    {
        var click = Registry.Find("mouse_click")!;
        var args = new JsonObject { ["x"] = 1, ["y"] = 2 };
        ToolCallRepair.ApplyNameHints("double_click", args, click);
        Assert.Equal(2, args["clicks"]!.GetValue<int>());

        Assert.Equal("mouse_click", ToolCallRepair.ResolveTool("ctrl_click", Registry)!.Name);
        var ctrl = new JsonObject { ["x"] = 1, ["y"] = 2 };
        ToolCallRepair.ApplyNameHints("ctrl_click", ctrl, click);
        Assert.Equal("""["ctrl"]""", ctrl["modifiers"]!.ToJsonString());

        // Only parameters the tool has: a tool named like a double click gets no clicks argument.
        var other = new JsonObject();
        ToolCallRepair.ApplyNameHints("double_size", other, Registry.Find("read_file")!);
        Assert.Empty(other);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Let me take a screenshot first.", true)]
    [InlineData("I'll open Notepad now:", true)]
    [InlineData("Done. Notepad is open with your text.", false)]
    [InlineData("Which file do you mean?", false)]
    [InlineData("You've declined both of my PowerShell commands. Could you tell me how you'd like the listing? Let me know and I'll continue.", false)]
    [InlineData("Let me know if you need anything else.", false)]
    public void Unfinished_replies_are_detected(string content, bool expected) =>
        Assert.Equal(expected, AgentSession.LooksUnfinished(new ChatMessage { Role = ChatRole.Assistant, Content = content }));

    [Theory]
    [InlineData("The task is complete. I opened Notepad.", true)]
    [InlineData("I have successfully completed the task.", true)]
    [InlineData("Let me check whether the task is complete.", false)]
    [InlineData("Clicking Save now.", false)]
    public void Completion_claims_are_detected(string content, bool expected) =>
        Assert.Equal(expected, AgentSession.LooksFinished(content));
}

public class ContextDetectionTests
{
    /// <summary>Answers by URL path; anything unknown is a 404, like a server without that endpoint.</summary>
    private sealed class RoutedServer(Dictionary<string, string> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(routes.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private static Task<ModelFacts> Probe(Dictionary<string, string> routes) =>
        ServerProbe.TryContextAsync(new HttpClient(new RoutedServer(routes)),
            new BlackBox.Core.Settings.ServerSettings { BaseUrl = "http://host:1234/v1", Model = "m" }, CancellationToken.None);

    [Fact]
    public async Task Ollama_reports_the_context_it_runs_with_not_the_models_maximum()
    {
        var routes = new Dictionary<string, string>
        {
            ["/api/version"] = """{"version":"0.12.0"}""",
            ["/api/ps"] = """{"models":[]}""",
            ["/api/show"] = """{"parameters":"num_ctx 16384\nstop \"<|im_end|>\"","model_info":{"qwen3.context_length":262144}}""",
        };
        Assert.Equal(16384, (await Probe(routes)).Context);

        routes["/api/ps"] = """{"models":[{"name":"m","model":"m","context_length":8192}]}""";
        Assert.Equal(8192, (await Probe(routes)).Context);
    }

    [Fact]
    public async Task llama_cpp_and_vLLM_report_their_context()
    {
        Assert.Equal(4096, (await Probe(new() { ["/props"] = """{"default_generation_settings":{"n_ctx":4096}}""" })).Context);
        Assert.Equal(32768, (await Probe(new() { ["/v1/models"] = """{"data":[{"id":"m","max_model_len":32768}]}""" })).Context);
        Assert.Null((await Probe([])).Context); // nothing answers: the caller assumes 100k
    }

    [Fact]
    public void The_real_context_size_is_read_from_too_long_errors()
    {
        Assert.Equal(8192, ContextOverflowException.ParseLimit("This model's maximum context length is 8192 tokens. However, your messages resulted in 9100 tokens."));
        Assert.Equal(4096, ContextOverflowException.ParseLimit("""{"error":{"code":400,"message":"the request exceeds the available context size, try increasing it","type":"exceed_context_size_error","n_prompt_tokens":5000,"n_ctx":4096}}"""));
        Assert.Null(ContextOverflowException.ParseLimit("The number of tokens to keep from the initial prompt is greater than the context length."));
    }
}
