using System.Net;
using System.Text.Json.Nodes;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;
using Xunit;

namespace BlackBox.Core.Tests;

/// <summary>Which model BlackBox uses when none was picked by hand, or the picked one isn't on the server.</summary>
public class ModelChoiceTests
{
    /// <summary>
    /// The user's LM Studio (forcepusher.ddns.net) as its /api/v1/models listed its models on
    /// 2026-10-06, trimmed to the fields BlackBox reads. qwen3.8-27b@q6_k_xl was loaded.
    /// </summary>
    private static readonly string[] UsersLmStudio =
    [
        """{"type":"llm","key":"qwen3.8-27b@q4_0","quantization":{"name":"Q4_0","bits_per_weight":4},"size_bytes":2297201904,"params_string":"27B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"qwen3.8-27b@q4_k_s","quantization":{"name":"Q4_K_S","bits_per_weight":4},"size_bytes":16285824272,"params_string":"27B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"qwen3.8-27b@q6_k_xl","quantization":{"name":"Q6_K_XL","bits_per_weight":6},"size_bytes":26226672912,"params_string":"27B","loaded_instances":[{"id":"qwen3.8-27b@q6_k_xl"}],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"qwen3.6-27b-mtp@q4_k_xl","quantization":{"name":"Q4_K_XL","bits_per_weight":4},"size_bytes":18840243904,"params_string":"27B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"qwen3.6-27b-mtp@q6_k_xl","quantization":{"name":"Q6_K_XL","bits_per_weight":6},"size_bytes":26946576064,"params_string":"27B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"gemma-4-31b-it-qat","quantization":{"name":"Q4_K_XL","bits_per_weight":4},"size_bytes":18488399262,"params_string":"31B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"gemma-4-31b-it","quantization":{"name":"Q5_K_XL","bits_per_weight":5},"size_bytes":23091154400,"params_string":"31B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"gemma-4-12b-it-qat","quantization":{"name":"Q4_K_XL","bits_per_weight":4},"size_bytes":6925877568,"params_string":"12B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"xortron.criminalcomputing.2026.27b.instruct.next-i1","quantization":{"name":"IQ3_XS","bits_per_weight":3},"size_bytes":12596387040,"params_string":"27B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"xortron.criminalcomputing.2026.27b.next@q6_k","quantization":{"name":"Q6_K","bits_per_weight":6},"size_bytes":22711786720,"params_string":"27B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"llm","key":"xortron.criminalcomputing.2026.27b.next@q5_k_m","quantization":{"name":"Q5_K_M","bits_per_weight":5},"size_bytes":19860355456,"params_string":"27B","loaded_instances":[],"capabilities":{"vision":true,"trained_for_tool_use":true}}""",
        """{"type":"embedding","key":"text-embedding-nomic-embed-text-v1.5","quantization":{"name":"Q4_K_M","bits_per_weight":4},"size_bytes":84106624,"params_string":null,"loaded_instances":[]}""",
    ];

    /// <summary>The same server's /api/v0/models, all that older LM Studio versions have: no sizes, and "vlm" for models that take images.</summary>
    private const string UsersLmStudioV0 = """
        {"data":[
        {"id":"qwen3.8-27b@q4_0","type":"vlm","quantization":"Q4_0","state":"not-loaded"},
        {"id":"qwen3.8-27b@q4_k_s","type":"vlm","quantization":"Q4_K_S","state":"not-loaded"},
        {"id":"qwen3.8-27b@q6_k_xl","type":"vlm","quantization":"Q6_K_XL","state":"loaded"},
        {"id":"qwen3.6-27b-mtp@q4_k_xl","type":"vlm","quantization":"Q4_K_XL","state":"not-loaded"},
        {"id":"qwen3.6-27b-mtp@q6_k_xl","type":"vlm","quantization":"Q6_K_XL","state":"not-loaded"},
        {"id":"gemma-4-31b-it-qat","type":"vlm","quantization":"Q4_K_XL","state":"not-loaded"},
        {"id":"gemma-4-31b-it","type":"vlm","quantization":"Q5_K_XL","state":"not-loaded"},
        {"id":"gemma-4-12b-it-qat","type":"vlm","quantization":"Q4_K_XL","state":"not-loaded"},
        {"id":"xortron.criminalcomputing.2026.27b.instruct.next-i1","type":"vlm","quantization":"IQ3_XS","state":"not-loaded"},
        {"id":"xortron.criminalcomputing.2026.27b.next@q6_k","type":"vlm","quantization":"Q6_K","state":"not-loaded"},
        {"id":"xortron.criminalcomputing.2026.27b.next@q5_k_m","type":"vlm","quantization":"Q5_K_M","state":"not-loaded"},
        {"id":"text-embedding-nomic-embed-text-v1.5","type":"embeddings","quantization":"Q4_K_M","state":"not-loaded"}
        ]}
        """;

    /// <summary>Answers by URL path; anything else is a 404, like a server without that endpoint.</summary>
    private sealed class Routes(Dictionary<string, string> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(routes.TryGetValue(request.RequestUri!.AbsolutePath, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>The user's LM Studio offering only the models <paramref name="keep"/> lets through, listed in reverse if asked.</summary>
    private static Dictionary<string, string> LmStudio(Func<string, bool>? keep = null, bool reversed = false)
    {
        var models = UsersLmStudio.Select(m => JsonNode.Parse(m)!.AsObject())
            .Where(m => keep?.Invoke(m["key"]!.GetValue<string>()) ?? true).ToList();
        if (reversed)
            models.Reverse();
        return new()
        {
            ["/v1/models"] = Ids(models.Select(m => m["key"]!.GetValue<string>())),
            ["/api/v1/models"] = new JsonObject { ["models"] = new JsonArray(models.Select(m => (JsonNode?)m).ToArray()) }.ToJsonString(),
        };
    }

    /// <summary>An OpenAI-style /v1/models list.</summary>
    private static string Ids(IEnumerable<string> ids) =>
        new JsonObject { ["data"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()) }.ToJsonString();

    private static Task<ProbeResult> Probe(Dictionary<string, string> routes, string picked = "") =>
        ServerProbe.CheckAsync(new HttpClient(new Routes(routes)), new ServerSettings { BaseUrl = "http://host:1234/v1", Model = picked }, CancellationToken.None);

    [Fact]
    public async Task With_no_model_picked_the_strongest_one_on_the_users_LM_Studio_is_used()
    {
        var probe = await Probe(LmStudio());
        Assert.Equal("gemma-4-31b-it", probe.Model); // 31B beats 27B, and its 5 bits beat the QAT build's 4
        Assert.True(probe.AutoPicked);
        Assert.Equal("31B, 5-bit", probe.ModelSize);
        Assert.Equal(11, probe.Models.Count);
        Assert.DoesNotContain("text-embedding-nomic-embed-text-v1.5", probe.Models);
    }

    [Fact]
    public async Task A_picked_model_is_used_while_the_server_offers_it_and_stood_in_for_while_it_does_not()
    {
        var offered = await Probe(LmStudio(), picked: "qwen3.8-27b@q4_k_s");
        Assert.Equal("qwen3.8-27b@q4_k_s", offered.Model);
        Assert.False(offered.AutoPicked);
        Assert.Null(offered.ModelSize);

        var missing = await Probe(LmStudio(keep: id => id != "qwen3.8-27b@q4_k_s"), picked: "qwen3.8-27b@q4_k_s");
        Assert.Equal("gemma-4-31b-it", missing.Model);
        Assert.True(missing.AutoPicked);
        Assert.DoesNotContain("qwen3.8-27b@q4_k_s", missing.Models);
    }

    [Fact]
    public async Task Among_equally_strong_models_the_loaded_one_wins_then_the_first_listed()
    {
        // Without the 31B models three 27B 6-bit builds tie; the loaded one needs no loading,
        // even when listed after the others.
        var probe = await Probe(LmStudio(keep: id => !id.StartsWith("gemma-4-31b"), reversed: true));
        Assert.Equal("qwen3.8-27b@q6_k_xl", probe.Model);

        var none = await Probe(LmStudio(keep: id => id is "qwen3.6-27b-mtp@q6_k_xl" or "xortron.criminalcomputing.2026.27b.next@q6_k"));
        Assert.Equal("qwen3.6-27b-mtp@q6_k_xl", none.Model);
    }

    [Fact]
    public async Task A_file_too_small_for_its_size_is_not_picked()
    {
        // LM Studio lists qwen3.8-27b@q4_0 as 27B at 4 bits in 2.3 GB, and it fails to load.
        var probe = await Probe(LmStudio(keep: id => id is "qwen3.8-27b@q4_0" or "qwen3.8-27b@q4_k_s"));
        Assert.Equal("qwen3.8-27b@q4_k_s", probe.Model);
        var smaller = await Probe(LmStudio(keep: id => id is "qwen3.8-27b@q4_0" or "gemma-4-12b-it-qat"));
        Assert.Equal("gemma-4-12b-it-qat", smaller.Model);
    }

    [Fact]
    public async Task An_older_LM_Studio_is_judged_by_api_v0_and_the_names()
    {
        var routes = new Dictionary<string, string>
        {
            ["/v1/models"] = Ids(JsonNode.Parse(UsersLmStudioV0)!["data"]!.AsArray().Select(m => m!["id"]!.GetValue<string>())),
            ["/api/v0/models"] = UsersLmStudioV0,
        };
        var probe = await Probe(routes);
        Assert.Equal("gemma-4-31b-it", probe.Model); // 31B from the name, 5 bits from "Q5_K_XL"
        Assert.Equal("31B, 5-bit", probe.ModelSize);
        Assert.Equal(11, probe.Models.Count);
    }

    [Fact]
    public async Task Other_servers_are_judged_by_the_names()
    {
        var probe = await Probe(new() { ["/v1/models"] = Ids(["text-embedding-3-small", "llama-3.1-8b-instruct", "Llama-3.3-70B-Instruct-AWQ", "Llama-3.3-70B-Instruct-FP8", "gpt-4o"]) });
        Assert.Equal("Llama-3.3-70B-Instruct-FP8", probe.Model);
        Assert.Equal("70B, 8-bit", probe.ModelSize);

        // Nothing tells the sizes: the first model that can chat, in the server's order.
        var hosted = await Probe(new() { ["/v1/models"] = Ids(["whisper-1", "gpt-4o-mini", "gpt-4o"]) });
        Assert.Equal("gpt-4o-mini", hosted.Model);
        Assert.Equal(["gpt-4o-mini", "gpt-4o"], hosted.Models);

        // A server that lists nothing is taken at its word that the picked model is there.
        var silent = await Probe(new() { ["/v1/models"] = Ids([]) }, picked: "my-model");
        Assert.Equal("my-model", silent.Model);
        Assert.False(silent.AutoPicked);
        Assert.Null((await Probe(new() { ["/v1/models"] = Ids(["text-embedding-3-small"]) })).Model);
    }

    [Fact]
    public void A_model_that_cannot_see_comes_after_one_that_can()
    {
        var blind = new ModelInfo("big") { Params = 70, Vision = false };
        var seeing = new ModelInfo("small") { Params = 12, Vision = true };
        var unknown = new ModelInfo("other") { Params = 8 };
        Assert.Equal("small", ModelChoice.Strongest([blind, seeing, unknown])!.Id);
        Assert.Equal("other", ModelChoice.Strongest([blind, unknown])!.Id); // servers that don't say are assumed to see
    }

    [Fact]
    public void A_mixture_of_experts_counts_as_the_dense_model_it_is_about_as_strong_as()
    {
        Assert.Equal("qwen3.5-27b", ModelChoice.Strongest([ModelChoice.FromName("qwen3.5-35b-a3b"), ModelChoice.FromName("qwen3.5-27b")])!.Id);
        Assert.Equal("Qwen3-235B-A22B", ModelChoice.Strongest([ModelChoice.FromName("llama-3.3-70b"), ModelChoice.FromName("Qwen3-235B-A22B")])!.Id);
        Assert.Equal("30B-A3B, 4-bit", ModelChoice.Describe(ModelChoice.FromName("qwen3-30b-a3b@q4_k_m")));
    }

    [Theory]
    [InlineData("qwen3.8-27b@q6_k_xl", 27d, null, 6d)]
    [InlineData("Qwen3.8-27B-UD-IQ2_S", 27d, null, 2d)]
    [InlineData("gemma-4-31b-it-qat", 31d, null, null)]
    [InlineData("xortron.criminalcomputing.2026.27b.instruct.next-i1", 27d, null, null)]
    [InlineData("Qwen3-30B-A3B-Instruct-2507", 30d, 3d, null)]
    [InlineData("mixtral-8x7b-instruct-v0.1.Q4_K_M", 56d, null, 4d)]
    [InlineData("qwen2.5-1.5b-instruct-bf16", 1.5d, null, 16d)]
    [InlineData("llama3.1:8b", 8d, null, null)]
    [InlineData("smollm2-135m-instruct-mlx-8bit", 0.135d, null, 8d)]
    [InlineData("Qwen2.5-7B-Instruct-1M", 7d, null, null)]
    [InlineData("Llama-3.3-70B-Instruct-AWQ", 70d, null, 4d)]
    [InlineData("gpt-4o", null, null, null)]
    public void Names_tell_the_size_and_quantization(string id, double? size, double? active, double? bits)
    {
        var info = ModelChoice.FromName(id);
        Assert.Equal(size, info.Params is { } p ? Math.Round(p, 3) : null);
        Assert.Equal(active, info.ActiveParams);
        Assert.Equal(bits, info.Bits);
        Assert.True(info.Chat);
    }

    [Theory]
    [InlineData("text-embedding-nomic-embed-text-v1.5")]
    [InlineData("whisper-1")]
    [InlineData("gpt-4o-mini-tts")]
    [InlineData("bge-reranker-v2-m3")]
    public void Models_that_cannot_chat_are_told_by_their_names(string id) => Assert.False(ModelChoice.FromName(id).Chat);

    [Fact]
    public void The_model_BlackBox_picked_is_used_but_not_saved()
    {
        var paths = new AppPaths(Directory.CreateTempSubdirectory("bb-settings-").FullName);
        paths.EnsureCreated();
        var store = new SettingsStore(paths);
        var settings = new AppSettings();
        settings.Server.Model = "picked";
        settings.Server.AutoModel = "stand-in";
        Assert.Equal("stand-in", BackendConnection.Create(new HttpClient(), settings.Server).Options.Model);

        store.Save(settings);
        Assert.DoesNotContain("stand-in", File.ReadAllText(paths.SettingsFile));
        var loaded = store.Load().Server;
        Assert.Equal("picked", loaded.Model);
        Assert.Equal("picked", loaded.ModelInUse);
    }

    /// <summary>
    /// Opt-in against a real server (BB_SERVER_URL): no chat model it offers is stronger than the
    /// one BlackBox picks. It only lists the models and reads their details; nothing is loaded.
    /// </summary>
    [Fact]
    public async Task On_a_real_server_no_model_is_stronger_than_the_one_picked()
    {
        var url = Environment.GetEnvironmentVariable("BB_SERVER_URL");
        Assert.SkipWhen(url is null, "Set BB_SERVER_URL to run.");
        using var http = LlmHttp.Create(TimeSpan.FromSeconds(30));
        var probe = await ServerProbe.CheckAsync(http, new ServerSettings { BaseUrl = url! }, CancellationToken.None);
        Assert.True(probe.Reachable, probe.Message);
        Assert.True(probe.AutoPicked);

        var found = new ServerSettings { BaseUrl = probe.BaseUrl! };
        var ids = await new OpenAiChatClient(http, found.BaseUrl, null).ListModelsAsync(CancellationToken.None);
        var catalog = await ServerProbe.CatalogAsync(http, found, ids, CancellationToken.None);
        foreach (var m in catalog)
            TestContext.Current.SendDiagnosticMessage($"{m.Id}: chat={m.Chat} vision={m.Vision} size={ModelChoice.Describe(m)} loaded={m.Loaded} incomplete={ModelChoice.LooksIncomplete(m)}");
        TestContext.Current.SendDiagnosticMessage($"picked {probe.Model} ({probe.ModelSize})");

        var chosen = catalog.Single(m => m.Id == probe.Model);
        foreach (var other in catalog.Where(m => m.Chat && !ModelChoice.LooksIncomplete(m) && m.Vision != false))
        {
            Assert.True((ModelChoice.DenseEquivalent(other) ?? -1) <= (ModelChoice.DenseEquivalent(chosen) ?? -1), $"{other.Id} is larger than {chosen.Id}");
            if (ModelChoice.DenseEquivalent(other) == ModelChoice.DenseEquivalent(chosen))
                Assert.True((other.Bits ?? -1) <= (chosen.Bits ?? -1), $"{other.Id} has more bits than {chosen.Id}");
        }
    }
}
