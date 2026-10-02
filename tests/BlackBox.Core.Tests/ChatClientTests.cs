using System.Text.Json.Nodes;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
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

        Assert.True(TextToolCallParser.TryExtract(msg, new HashSet<string> { "mouse_click" }));
        Assert.Equal("I'll click it.", msg.Content);
        Assert.Equal("mouse_click", msg.ToolCalls![0].Name);
        Assert.Equal("""{"x":5,"y":6}""", msg.ToolCalls[0].Arguments);
    }

    [Fact]
    public void Text_tool_calls_for_unknown_tools_are_ignored()
    {
        var msg = new ChatMessage { Role = ChatRole.Assistant, Content = "<tool_call>{\"name\":\"rm_rf\",\"arguments\":{}}</tool_call>" };
        Assert.False(TextToolCallParser.TryExtract(msg, new HashSet<string> { "mouse_click" }));
    }
}
