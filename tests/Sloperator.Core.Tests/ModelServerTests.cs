using System.Net;
using System.Text.Json.Nodes;
using Sloperator.Core.Agent;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Sloperator.Core.Tools;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>The agent connecting Sloperator itself to another model (model_server).</summary>
public sealed class ModelServerTests
{
    /// <summary>The top bar: what it shows, and what the tool switched it to.</summary>
    private sealed class TopBar : IModelServerSwitch
    {
        public ServerSettings Current { get; } = new() { BaseUrl = KiloGateway.Address, AutoModel = KiloGateway.Model };
        public List<(string Url, string Key, string Model)> Switched { get; } = [];

        public Task SwitchAsync(string baseUrl, string apiKey, string model)
        {
            Switched.Add((baseUrl, apiKey, model));
            Current.BaseUrl = baseUrl;
            Current.ApiKey = apiKey;
            Current.Model = model;
            Current.AutoModel = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>An LM Studio-like server on plain http: its model list, and 401 for any other key than <paramref name="key"/> when it wants one.</summary>
    private sealed class LocalServer(string[] models, string? key = null) : HttpMessageHandler
    {
        public List<string?> KeysSeen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Scheme != "http")
                throw new HttpRequestException("The SSL connection could not be established");
            var sent = request.Headers.Authorization?.Parameter;
            KeysSeen.Add(sent);
            if (key is not null && sent != key)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("""{"error":{"message":"Invalid API key"}}""") });
            if (request.RequestUri.AbsolutePath == "/v1/models")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    { Content = new StringContent(new JsonObject { ["data"] = new JsonArray(models.Select(m => (JsonNode)new JsonObject { ["id"] = m }).ToArray()) }.ToJsonString()) });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class NobodyThere : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
    }

    private static ToolContext Context(TopBar bar) =>
        new() { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath(), ModelServer = bar };

    private static Task<ToolResult> Run(HttpMessageHandler server, TopBar bar, JsonObject args) =>
        new ModelServerTool(() => new HttpClient(server)).ExecuteAsync(args, Context(bar), CancellationToken.None);

    [Fact]
    public async Task Connect_switches_the_top_bar_to_a_server_that_answers()
    {
        var bar = new TopBar();
        var result = await Run(new LocalServer(["qwen3.8-27b", "gemma-4-12b-it"]), bar, new JsonObject { ["action"] = "connect", ["address"] = "localhost:1234" });
        Assert.False(result.IsError, result.Text);
        // The address that answered, no key, and no model picked: the strongest one is Sloperator's pick.
        Assert.Equal([("http://localhost:1234/v1", "", "")], bar.Switched);
        Assert.Contains("uses qwen3.8-27b (the strongest it offers", result.Text);
        Assert.Contains("the user's next message goes to the new one", result.Text);
    }

    [Fact]
    public async Task A_model_is_picked_by_its_listed_name_or_a_unique_part_of_it()
    {
        var server = new LocalServer(["qwen3.8-27b", "gemma-4-12b-it", "gemma-4-31b-it"]);
        var bar = new TopBar();
        var picked = await Run(server, bar, new JsonObject { ["action"] = "connect", ["address"] = "http://localhost:1234/v1", ["model"] = "QWEN3.8" });
        Assert.False(picked.IsError, picked.Text);
        Assert.Equal("qwen3.8-27b", bar.Switched.Single().Model);

        // "gemma" could be either: nothing changes, and the result names them.
        var vague = await Run(server, bar, new JsonObject { ["action"] = "connect", ["address"] = "http://localhost:1234/v1", ["model"] = "gemma" });
        Assert.True(vague.IsError);
        Assert.Single(bar.Switched);
        Assert.Contains("gemma-4-12b-it, gemma-4-31b-it", vague.Text);
    }

    [Fact]
    public async Task Nothing_changes_when_the_server_does_not_answer_or_refuses_the_key()
    {
        var bar = new TopBar();
        var offline = await Run(new NobodyThere(), bar, new JsonObject { ["action"] = "connect", ["address"] = "localhost:1234" });
        Assert.True(offline.IsError);
        Assert.StartsWith("Nothing changed:", offline.Text);
        Assert.Contains("LM Studio, its server has to be switched on", offline.Text);

        var wrongKey = await Run(new LocalServer(["m"], key: "right"), bar, new JsonObject { ["action"] = "connect", ["address"] = "http://box:8080/v1", ["api_key"] = "wrong" });
        Assert.True(wrongKey.IsError);
        Assert.Contains("Ask the user for the right API key", wrongKey.Text);
        Assert.Empty(bar.Switched);
        Assert.Equal(KiloGateway.Address, bar.Current.BaseUrl);
    }

    [Fact]
    public async Task The_key_goes_to_the_server_and_stays_with_its_address_but_never_in_the_card_or_result()
    {
        var server = new LocalServer(["a-model", "b-model"], key: "sk-secret-123");
        var bar = new TopBar();
        var args = new JsonObject { ["action"] = "connect", ["address"] = "http://box:8080/v1", ["api_key"] = "sk-secret-123" };
        var result = await Run(server, bar, args);
        Assert.False(result.IsError, result.Text);
        Assert.Equal("sk-secret-123", bar.Current.ApiKey);
        Assert.DoesNotContain("sk-secret", result.Text);
        Assert.DoesNotContain("sk-secret", new ModelServerTool().Describe(args));

        // Another model on the same server keeps the key the user gave before.
        var other = await Run(server, bar, new JsonObject { ["action"] = "connect", ["address"] = "box:8080", ["model"] = "b-model" });
        Assert.False(other.IsError, other.Text);
        Assert.Equal(("http://box:8080/v1", "sk-secret-123", "b-model"), bar.Switched[^1]);

        // A key isn't carried to another server.
        await Run(new LocalServer(["c"]), bar, new JsonObject { ["action"] = "connect", ["address"] = "http://other:1234/v1" });
        Assert.Equal("", bar.Switched[^1].Key);

        // Back to a server saved in the top bar: its key comes with it.
        bar.Current.Saved.Add(new SavedServer { BaseUrl = "http://box:8080/v1", ApiKey = "sk-secret-123" });
        var back = await Run(server, bar, new JsonObject { ["action"] = "connect", ["address"] = "box:8080" });
        Assert.False(back.IsError, back.Text);
        Assert.Equal("sk-secret-123", bar.Switched[^1].Key);
    }

    [Fact]
    public async Task Show_says_the_server_and_its_models_without_the_key()
    {
        var bar = new TopBar();
        bar.Current.BaseUrl = "http://box:8080/v1";
        bar.Current.ApiKey = "sk-secret-123";
        bar.Current.Model = "a-model";
        bar.Current.AutoModel = null;
        var result = await Run(new LocalServer(["a-model", "b-model"], key: "sk-secret-123"), bar, new JsonObject { ["action"] = "show" });
        Assert.Equal("Sloperator runs on a-model at http://box:8080/v1, with an API key.\nModels it offers (2): a-model, b-model", result.Text);
    }

    [Fact]
    public async Task Without_the_app_there_is_no_tool_and_nothing_to_switch()
    {
        var ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = Path.GetTempPath() };
        var result = await new ModelServerTool().ExecuteAsync(new JsonObject { ["action"] = "show" }, ctx, CancellationToken.None);
        Assert.True(result.IsError);

        Assert.Contains("use model_server", SystemPrompt.Build("Test OS", null, "C:\\", "", modelServer: true));
        Assert.DoesNotContain("model_server", SystemPrompt.Build("Test OS", null, "C:\\", ""));
    }
}
