using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

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
    public async Task OpenRouters_reasoning_details_are_put_together_and_sent_back()
    {
        // As OpenRouter streamed Haiku 5.5's thinking on 2026-10-29: the text a few words at a
        // time, the signature last, all under one index.
        var sse = string.Join("\n",
            """data: {"choices":[{"delta":{"role":"assistant","content":"","reasoning":"17*23:","reasoning_details":[{"type":"reasoning.text","text":"17*23:","format":"anthropic-claude-v1","index":0}]}}]}""",
            """data: {"choices":[{"delta":{"content":"","reasoning":" 340+51.","reasoning_details":[{"type":"reasoning.text","text":" 340+51.","format":"anthropic-claude-v1","index":0}]}}]}""",
            """data: {"choices":[{"delta":{"content":"","reasoning":"","reasoning_details":[{"type":"reasoning.text","signature":"c2lnbmVk","format":"anthropic-claude-v1","index":0}]}}]}""",
            """data: {"choices":[{"delta":{"content":"391"},"finish_reason":"stop"}]}""",
            "data: [DONE]");
        var msg = await OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None);
        Assert.Equal("17*23: 340+51.", msg.Reasoning);
        var detail = Assert.Single(msg.ReasoningDetails!)!;
        Assert.Equal("17*23: 340+51.", (string)detail["text"]!);
        Assert.Equal("c2lnbmVk", (string)detail["signature"]!);
        Assert.Equal("anthropic-claude-v1", (string)detail["format"]!);

        var sent = OpenAiChatClient.BuildRequest([ChatMessage.User("17*23?"), msg, ChatMessage.User("Thanks")], [], new ChatRequestOptions { Model = "anthropic/claude-haiku-5.5" }, sendReasoning: true);
        Assert.Equal("c2lnbmVk", (string)sent["messages"]![1]!["reasoning_details"]![0]!["signature"]!);
        // A server that turned the reasoning down gets none of it.
        Assert.Null(OpenAiChatClient.BuildRequest([msg], [], new ChatRequestOptions(), sendReasoning: false)["messages"]![0]!["reasoning_details"]);

        // Claude's thinking cut off before its signature came would be refused: left out.
        var cut = await OpenAiChatClient.ReadStreamAsync(new StringReader(string.Join("\n",
            """data: {"choices":[{"delta":{"reasoning":"Let me","reasoning_details":[{"type":"reasoning.text","text":"Let me","format":"anthropic-claude-v1","index":0}]},"finish_reason":"length"}]}""",
            "data: [DONE]")), null, CancellationToken.None);
        Assert.Equal("Let me", cut.Reasoning);
        Assert.Null(cut.ReasoningDetails);
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

        // OpenRouter and Kilo pass an upstream 429 on in the stream.
        sse = """data: {"error":{"message":"Daily limit reached for thinkingmachines/inkling-small:free","code":429}}""";
        await Assert.ThrowsAsync<RateLimitedException>(() => OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None));

        // LM Studio's llama.cpp out of KV room while other requests ran: worth sending again after a while.
        sse = """data: {"error":"failed to process mtmd chunk"}""";
        var failed = await Assert.ThrowsAsync<ConnectionLostException>(() => OpenAiChatClient.ReadStreamAsync(new StringReader(sse), null, CancellationToken.None));
        Assert.True(failed.ServerFailed);
        Assert.False(failed.ReceivedTokens);
    }

    [Fact]
    public async Task Requests_too_large_and_requests_held_back_are_told_apart()
    {
        var server = new ScriptedServer();
        server.Failures.Enqueue((System.Net.HttpStatusCode.RequestEntityTooLarge, "Request Entity Too Large\n\nFUNCTION_PAYLOAD_TOO_LARGE\n"));
        server.Failures.Enqueue(((System.Net.HttpStatusCode)429, """{"error":{"message":"Rate limit exceeded for free models. Please try again later."}}"""));
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null);
        Task<ChatMessage> Send() => client.CompleteAsync([ChatMessage.User("hi")], [], new ChatRequestOptions(), null, CancellationToken.None);

        var tooLarge = await Assert.ThrowsAsync<RequestTooLargeException>(Send);
        Assert.True(tooLarge.Bytes > 0);
        Assert.Contains("FUNCTION_PAYLOAD_TOO_LARGE", tooLarge.Message);
        Assert.Null(tooLarge.ContextLimit);

        var limited = await Assert.ThrowsAsync<RateLimitedException>(Send);
        Assert.EndsWith("answered 429: Rate limit exceeded for free models. Please try again later.", limited.Message);
        Assert.Null(limited.RetryAfter); // Kilo doesn't say how long
    }

    [Theory]
    [InlineData("image input is not supported - hint: if this is unexpected, you may need to provide the mmproj", true)] // llama-server
    [InlineData("No endpoints found that support image input", true)]                                                   // OpenRouter
    [InlineData("Invalid content type. image_url is only supported by certain models.", true)]                          // OpenAI
    [InlineData("Qwen3-32B is not a multimodal model", true)]                                                           // vLLM
    [InlineData("Model does not support images. Please use a model that does.", true)]                                  // LM Studio
    [InlineData("Failed to deserialize the JSON body: unknown variant `image_url`, expected `text`", true)]               // DeepSeek
    [InlineData("'image' is not supported: not available for this model", true)]                                       // munito (Gemma 4)
    [InlineData("Invalid tool schema", false)]
    [InlineData("The image is 21 MB; the most is 20 MB.", false)]
    [InlineData("registry.ollama.ai/library/llama3.2-vision:latest does not support tools", false)]                    // a model's name
    [InlineData("No endpoints found for meta-llama/llama-3.2-11b-vision-instruct.", false)]
    [InlineData("Images are scaled to 1 MP. " + "Some long explanation of the limits that has nothing to do with it at all; " + "tools are not supported", false)]
    public void Errors_saying_the_model_takes_no_pictures_are_told_apart(string error, bool refused) =>
        Assert.Equal(refused, OpenAiChatClient.LooksLikeImagesRefused(error));

    [Theory]
    [InlineData("too many image inputs", true, null)]                                         // munito, 28 October 2026
    [InlineData("Too many images in the request: at most 8 images are allowed.", true, 8)]
    [InlineData("You can send a maximum of 5 images per request", true, 5)]
    [InlineData("At most 1 image(s) may be provided in one request.", true, 1)]                // vLLM, Azure
    [InlineData("too many images and documents: 104 + 1 > 100", true, 100)]                   // Anthropic
    [InlineData("messages.3.content.1.image.source.base64.data: At least one of the image dimensions exceed max allowed size for many-image requests: 2000 pixels", false, null)]
    [InlineData("image input is not supported", false, null)]
    [InlineData("too many tokens in the prompt", false, null)]
    [InlineData("The image is 21 MB; the most is 20 MB.", false, null)]
    public void Errors_saying_a_request_had_too_many_pictures_are_told_apart(string error, bool tooMany, int? limit)
    {
        Assert.Equal(tooMany, OpenAiChatClient.LooksLikeTooManyImages(error));
        Assert.Equal(limit, TooManyImagesException.ParseLimit(error));
    }

    [Fact]
    public async Task A_server_refusing_pictures_says_so_rather_than_being_asked_again()
    {
        // llama-server without its projector answers 500, which was sent again twelve times as a dropped connection.
        var server = new ScriptedServer();
        server.Failures.Enqueue((System.Net.HttpStatusCode.InternalServerError, """{"error":{"code":500,"message":"image input is not supported - hint: if this is unexpected, you may need to provide the mmproj","type":"server_error"}}"""));
        server.Failures.Enqueue((System.Net.HttpStatusCode.BadRequest, """{"error":{"message":"image input is not supported"}}"""));
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", null);
        var picture = new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText("Screenshot after the last action:"), ContentPart.FromImage([1, 2, 3], "image/jpeg")] };

        var refused = await Assert.ThrowsAsync<ImagesRefusedException>(() => client.CompleteAsync([ChatMessage.User("hi"), picture], [], new ChatRequestOptions(), null, CancellationToken.None));
        Assert.Contains("you may need to provide the mmproj", refused.Message);
        // Without a picture in the request, the same words are some other trouble.
        var other = await Assert.ThrowsAsync<LlmException>(() => client.CompleteAsync([ChatMessage.User("hi")], [], new ChatRequestOptions(), null, CancellationToken.None));
        Assert.IsNotType<ImagesRefusedException>(other);
    }

    [Fact]
    public void A_429_says_how_long_to_wait_however_the_server_puts_it()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_792_160_000);
        var headers = new HttpResponseMessage().Headers;
        headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), OpenAiChatClient.RetryAfter(headers, "", now));

        // OpenRouter's reset time, in milliseconds since 1970, as Kilo passes it on in the error's metadata.
        const string kilo = """{"error":{"message":"Rate limit exceeded: limit_rpd/thinkingmachines/inkling-small-20260730","code":429,"metadata":{"headers":{"X-RateLimit-Limit":"1000","X-RateLimit-Remaining":"0","X-RateLimit-Reset":"1792163600000"}}}}""";
        Assert.Equal(TimeSpan.FromHours(1), OpenAiChatClient.RetryAfter(null, kilo, now));

        // Gemini says it in words, and again as retryDelay.
        const string gemini = """{"error":{"code":429,"message":"You exceeded your current quota. Please retry in 37.25s.","details":[{"retryDelay":"37s"}]}}""";
        Assert.Equal(TimeSpan.FromSeconds(37.25), OpenAiChatClient.RetryAfter(null, gemini, now));

        Assert.Null(OpenAiChatClient.RetryAfter(null, """{"error":{"message":"Rate limit exceeded for free models. Please try again later."}}""", now));
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

    [Fact]
    public void Effort_goes_as_reasoning_effort_or_as_OpenRouters_reasoning_object()
    {
        var options = new ChatRequestOptions { Model = "gpt-5.6", Effort = "high" };
        var plain = OpenAiChatClient.BuildRequest([ChatMessage.User("hi")], [], options);
        Assert.Equal("high", (string)plain["reasoning_effort"]!);
        Assert.Null(plain["reasoning"]);

        var routed = OpenAiChatClient.BuildRequest([ChatMessage.User("hi")], [], options, effortAsObject: true);
        Assert.Equal("high", (string)routed["reasoning"]!["effort"]!);
        Assert.Null(routed["reasoning_effort"]);

        // None picked or none taken: nothing, as before.
        Assert.Null(OpenAiChatClient.BuildRequest([ChatMessage.User("hi")], [], new ChatRequestOptions { Model = "qwen" })["reasoning_effort"]);
        // A server that turned the extras down gets none of them.
        Assert.Null(OpenAiChatClient.BuildRequest([ChatMessage.User("hi")], [], options, sendExtras: false)["reasoning_effort"]);

        Assert.True(OpenAiChatClient.OpenRouterStyle("https://openrouter.ai/api/v1"));
        Assert.True(OpenAiChatClient.OpenRouterStyle(KiloGateway.Address));
        Assert.False(OpenAiChatClient.OpenRouterStyle("https://api.openai.com/v1"));
        Assert.False(OpenAiChatClient.OpenRouterStyle("http://forcepusher.ddns.net:1234/v1"));
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
    public async Task A_services_website_or_key_in_the_address_box_is_taken_for_what_it_is()
    {
        // The website people know, any page of it: its API is tried first.
        Assert.Equal("https://openrouter.ai/api/v1", ServerAddress.Candidates("openrouter.ai")[0]);
        Assert.Equal("https://openrouter.ai/api/v1", ServerAddress.Candidates("https://openrouter.ai/settings/keys")[0]);
        Assert.Equal("https://api.groq.com/openai/v1", ServerAddress.Candidates("www.groq.com")[0]);
        Assert.Equal(["https://openrouter.ai/api/v1"], ServerAddress.Candidates("https://openrouter.ai/api/v1"));
        Assert.True(KiloGateway.Is("kilo.ai"));

        Assert.True(ServerAddress.LooksLikeKey("sk-or-v1-0123456789abcdef0123456789abcdef"));
        Assert.True(ServerAddress.LooksLikeKey(" gsk_0123456789ABCDEFabcdef0123 "));
        Assert.False(ServerAddress.LooksLikeKey("localhost:1234"));
        Assert.False(ServerAddress.LooksLikeKey("my-gpu-box"));
        Assert.False(ServerAddress.LooksLikeKey("forcepusher.ddns.net:1234/v1"));
        var key = await ServerProbe.CheckAsync(new HttpClient(new HtmlServer()), new Sloperator.Core.Settings.ServerSettings { BaseUrl = "sk-or-v1-0123456789abcdef0123456789abcdef" }, CancellationToken.None);
        Assert.False(key.Reachable);
        Assert.StartsWith("That looks like an API key, not a server address", key.Message);

        // Another website: what it is, not its 404 page's HTML.
        var site = await ServerProbe.CheckAsync(new HttpClient(new PageNotFound()), new Sloperator.Core.Settings.ServerSettings { BaseUrl = "https://example.com" }, CancellationToken.None);
        Assert.Equal("Can't reach the server: there is a website at this address (404 for /models), not a model server's API. " +
                     "The service's documentation names its OpenAI-compatible API address, often ending in /v1.", site.Message);
    }

    private sealed class PageNotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { Content = new StringContent("<!DOCTYPE html><html><head><title>404</title></head><body>Not found</body></html>") });
    }

    [Fact]
    public async Task The_probe_finds_the_protocol_that_answers()
    {
        var server = new Sloperator.Core.Settings.ServerSettings { BaseUrl = "host:1234", Model = "m" };
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
        var halfTyped = await ServerProbe.CheckAsync(new HttpClient(new SchemeServer("http")), new Sloperator.Core.Settings.ServerSettings { BaseUrl = "http://" }, CancellationToken.None);
        Assert.False(halfTyped.Reachable);
        Assert.Equal("That is not a server address.", halfTyped.Message);

        // A web server (or a captive portal) answers with a page instead of a model list.
        var page = new HttpClient(new HtmlServer());
        var probe = await ServerProbe.CheckAsync(page, new Sloperator.Core.Settings.ServerSettings { BaseUrl = "http://host:80", Model = "m" }, CancellationToken.None);
        Assert.False(probe.Reachable);
        Assert.Contains("not a model list", probe.Message);
        Assert.Null((await ServerProbe.TryContextAsync(page, new Sloperator.Core.Settings.ServerSettings { BaseUrl = "http://host:80", Model = "m" }, CancellationToken.None)).Context);
    }

    [Fact]
    public async Task A_refused_api_key_is_named_as_such_not_as_an_unreachable_server()
    {
        // A wrong OpenRouter key lists the models (that needs none) and fails at the first message.
        var server = new ScriptedServer();
        server.Failures.Enqueue((System.Net.HttpStatusCode.Unauthorized, """{"error":{"message":"User not found.","code":401}}"""));
        server.Failures.Enqueue(((System.Net.HttpStatusCode)402, """{"error":{"message":"Insufficient credits","code":402}}"""));
        var client = new OpenAiChatClient(new HttpClient(server), "http://test", "sk-or-v1-wrong");
        Task<ChatMessage> Send() => client.CompleteAsync([ChatMessage.User("hi")], [], new ChatRequestOptions(), null, CancellationToken.None);
        var wrongKey = await Assert.ThrowsAsync<KeyRefusedException>(Send);
        Assert.Equal("The server turned down the API key (it says \"User not found.\"). Check the key next to the server address at the top.", wrongKey.Message);
        Assert.Contains("run out of credits (it says \"Insufficient credits\")", (await Assert.ThrowsAsync<KeyRefusedException>(Send)).Message);

        // OpenAI wants a key for its model list too: the server answered, so it isn't "Can't reach the server".
        var noKey = await ServerProbe.CheckAsync(new HttpClient(new KeyServer()), new Sloperator.Core.Settings.ServerSettings { BaseUrl = "api.example.com/v1" }, CancellationToken.None);
        Assert.False(noKey.Reachable);
        Assert.True(noKey.KeyRefused);
        Assert.Equal("The server wants an API key (it says \"Missing bearer authentication in header\"). Enter yours next to the server address at the top.", noKey.Message);
    }

    /// <summary>Answers 401 over https, as OpenAI does without a key; plain http doesn't connect.</summary>
    private sealed class KeyServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            request.RequestUri!.Scheme == "https"
                ? Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\n  \"error\": {\n    \"message\": \"Missing bearer authentication in header\",\n    \"type\": \"invalid_request_error\"\n  }\n}"),
                })
                : throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
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
    public void A_repair_leaves_the_text_inside_values_alone()
    {
        // A file's None became null and its Markdown fences went, whenever its call needed a repair
        // (here: one closing brace too many).
        const string content = "def f(flag=True):\n    return None\n\n```python\nprint(f())\n```";
        var raw = new JsonObject { ["path"] = "notes.md", ["content"] = content, ["append"] = false }.ToJsonString().Replace("false", "False") + "}";
        var args = ToolCallRepair.ParseArguments(raw);
        Assert.Equal(content, args["content"]!.GetValue<string>());
        Assert.False(args["append"]!.GetValue<bool>());
        Assert.Equal(content, ToolCallRepair.ParseArguments("```json\n" + raw + "\n```")["content"]!.GetValue<string>());
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
            new Sloperator.Core.Settings.ServerSettings { BaseUrl = "http://host:1234/v1", Model = "m" }, CancellationToken.None);

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
        Assert.Null((await Probe(routes)).Vision); // an older Ollama doesn't say what it takes

        // Whether it takes images, so a model that doesn't gets no screenshots.
        routes["/api/show"] = """{"parameters":"num_ctx 16384","capabilities":["completion","tools"]}""";
        Assert.Equal((8192, false), ((await Probe(routes)).Context, (await Probe(routes)).Vision));
        routes["/api/show"] = """{"capabilities":["completion","vision","tools"]}""";
        Assert.True((await Probe(routes)).Vision);
    }

    [Fact]
    public async Task llama_cpp_and_vLLM_report_their_context()
    {
        Assert.Equal(4096, (await Probe(new() { ["/props"] = """{"default_generation_settings":{"n_ctx":4096}}""" })).Context);
        // llama-server started without a vision projector says so.
        Assert.False((await Probe(new() { ["/props"] = """{"default_generation_settings":{"n_ctx":4096},"modalities":{"vision":false,"audio":false}}""" })).Vision);
        Assert.True((await Probe(new() { ["/props"] = """{"default_generation_settings":{"n_ctx":4096},"modalities":{"vision":true,"audio":false}}""" })).Vision);
        Assert.Equal(32768, (await Probe(new() { ["/v1/models"] = """{"data":[{"id":"m","max_model_len":32768}]}""" })).Context);
        Assert.Null((await Probe([])).Context); // nothing answers: the caller assumes 1M
    }

    [Fact]
    public void The_real_context_size_is_read_from_too_long_errors()
    {
        Assert.Equal(8192, ContextOverflowException.ParseLimit("This model's maximum context length is 8192 tokens. However, your messages resulted in 9100 tokens."));
        Assert.Equal(1_000_000, ContextOverflowException.ParseLimit("This endpoint's maximum context length is 1000000 tokens. However, you requested about 1163647 tokens (1163631 of text input, 16 in the output).")); // Kilo
        Assert.Equal(4096, ContextOverflowException.ParseLimit("""{"error":{"code":400,"message":"the request exceeds the available context size, try increasing it","type":"exceed_context_size_error","n_prompt_tokens":5000,"n_ctx":4096}}"""));
        Assert.Null(ContextOverflowException.ParseLimit("The number of tokens to keep from the initial prompt is greater than the context length."));
    }

    /// <summary>
    /// LM Studio out of the box: a model loaded on first use gets a 4k context. Its memory
    /// guardrail refuses 32k for this one, and takes 16k.
    /// </summary>
    private sealed class FreshLmStudio : HttpMessageHandler
    {
        /// <summary>The context a model gets on first use: LM Studio's 4k, or what the user set for it.</summary>
        public int Default { get; init; } = 4096;
        public int? Context { get; private set; }
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add($"{request.Method} {path} {body}".Trim());
            string? answer = path switch
            {
                "/api/v0/models/m" => Context is { } c
                    ? $$"""{"id":"m","type":"vlm","state":"loaded","loaded_context_length":{{c}}}"""
                    : """{"id":"m","type":"vlm","state":"not-loaded","max_context_length":262144}""",
                "/v1/chat/completions" => Load(Default),
                "/api/v1/models" => Context is { } c
                    ? $$$"""{"models":[{"key":"m","max_context_length":262144,"loaded_instances":[{"id":"m","config":{"context_length":{{{c}}}}}]}]}"""
                    : """{"models":[{"key":"m","max_context_length":262144,"loaded_instances":[]}]}""",
                "/api/v1/models/unload" => Unload(),
                "/api/v1/models/load" when body.Contains("32768") => null,
                "/api/v1/models/load" => Load(16384),
                _ => null,
            };
            return answer is null
                ? new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"model_load_failed"}""") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer) };
        }

        private string Load(int size)
        {
            if (Context is null || size != Default)
                Context = size;
            return size == Default
                ? "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n"
                : $$$"""{"instance_id":"m","status":"loaded","load_config":{"context_length":{{{size}}}}}""";
        }

        private string Unload()
        {
            Context = null;
            return """{"instance_id":"m"}""";
        }
    }

    [Fact]
    public async Task A_model_LM_Studio_loads_with_its_4k_default_is_loaded_again_with_room_for_Sloperator()
    {
        var server = new FreshLmStudio();
        var notes = new List<string>();
        var settings = new Sloperator.Core.Settings.ServerSettings { BaseUrl = "http://host:1234/v1", Model = "m" };
        var facts = await ServerProbe.EnsureLoadedAsync(new HttpClient(server), settings, null, CancellationToken.None, notes.Add);
        Assert.Equal(16384, facts.Context);
        Assert.Contains(server.Calls, c => c.StartsWith("POST /api/v1/models/unload") && c.Contains("\"instance_id\":\"m\""));
        Assert.Equal("LM Studio loaded m with a 4k context, too small for Sloperator; it runs with 16k now. To choose the size yourself, load it in LM Studio with the context length you want.", Assert.Single(notes));

        // Loaded by the user already, even small: left as it is.
        server.Calls.Clear();
        notes.Clear();
        Assert.Equal(16384, (await ServerProbe.EnsureLoadedAsync(new HttpClient(server), settings, null, CancellationToken.None, notes.Add)).Context);
        Assert.DoesNotContain(server.Calls, c => c.Contains("/load") || c.Contains("/unload"));
        Assert.Empty(notes);

        // A size the user set for the model (LM Studio's 32000) is theirs: loaded again at 64k beside
        // another model, one such crashed at its first answer.
        var theirs = new FreshLmStudio { Default = 32000 };
        Assert.Equal(32000, (await ServerProbe.EnsureLoadedAsync(new HttpClient(theirs), settings, null, CancellationToken.None, notes.Add)).Context);
        Assert.DoesNotContain(theirs.Calls, c => c.Contains("/load") || c.Contains("/unload"));
        Assert.Empty(notes);
    }
}
