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
        var sse = """data: {"choices":[{"delta":{"content":"Done, it is open.\n</think>\n\nNotepad is open."}}]}""";
        var msg = await OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None);
        Assert.Equal("Notepad is open.", msg.Content);
        Assert.Contains("Done, it is open.", msg.Reasoning);
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
        var options = new ChatRequestOptions { Model = "local", Temperature = 0.6, Extra = new JsonObject { ["top_k"] = 20 } };

        var req = OpenAiChatClient.BuildRequest(messages, tools, options);

        Assert.Equal("local", req["model"]!.GetValue<string>());
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
    public void Base_url_is_normalized(string input, string expected) =>
        Assert.Equal(expected, OpenAiChatClient.NormalizeBaseUrl(input));

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
    public void Double_click_alias_keeps_its_meaning()
    {
        var args = new JsonObject { ["x"] = 1, ["y"] = 2 };
        ToolCallRepair.ApplyNameHints("double_click", args);
        Assert.Equal(2, args["clicks"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Let me take a screenshot first.", true)]
    [InlineData("I'll open Notepad now:", true)]
    [InlineData("Done. Notepad is open with your text.", false)]
    [InlineData("Which file do you mean?", false)]
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
