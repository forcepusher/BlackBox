using System.Net;
using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Which model Sloperator uses when none was picked by hand, or the picked one isn't on the server.</summary>
public class ModelChoiceTests
{
    /// <summary>
    /// The user's LM Studio (forcepusher.ddns.net) as its /api/v1/models listed its models on
    /// 2026-10-06, trimmed to the fields Sloperator reads. qwen3.8-27b@q6_k_xl was loaded.
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
        return LmStudio(models);
    }

    /// <summary>An LM Studio offering these models, in this order.</summary>
    private static Dictionary<string, string> LmStudio(IReadOnlyList<JsonObject> models) => new()
    {
        ["/v1/models"] = Ids(models.Select(m => m["key"]!.GetValue<string>())),
        ["/api/v1/models"] = new JsonObject { ["models"] = new JsonArray(models.Select(m => m.DeepClone()).ToArray()) }.ToJsonString(),
    };

    /// <summary>One of the user's models as LM Studio describes it.</summary>
    private static JsonObject UsersModel(string key) =>
        UsersLmStudio.Select(m => JsonNode.Parse(m)!.AsObject()).Single(m => m["key"]!.GetValue<string>() == key);

    /// <summary>An OpenAI-style /v1/models list.</summary>
    private static string Ids(IEnumerable<string> ids) =>
        new JsonObject { ["data"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray()) }.ToJsonString();

    private static Task<ProbeResult> Probe(Dictionary<string, string> routes, string picked = "") =>
        ServerProbe.CheckAsync(new HttpClient(new Routes(routes)), new ServerSettings { BaseUrl = "http://host:1234/v1", Model = picked }, CancellationToken.None);

    [Fact]
    public async Task With_no_model_picked_the_strongest_one_on_the_users_LM_Studio_is_used()
    {
        var probe = await Probe(LmStudio());
        // Not Gemma 4 31B, though larger: it solves 43.4% of Terminal-Bench 2.1 to Qwen3.8 27B's 79.8%.
        Assert.Equal("qwen3.8-27b@q6_k_xl", probe.Model);
        Assert.True(probe.AutoPicked);
        Assert.Equal("27B, 6-bit, 79.8% on Terminal-Bench 2.1", probe.ModelDetails);
        Assert.Equal(11, probe.Models.Count);
        Assert.DoesNotContain("text-embedding-nomic-embed-text-v1.5", probe.Models);
    }

    [Fact]
    public async Task The_users_models_rank_by_their_agent_benchmark_score_then_by_size_and_bits()
    {
        var server = new ServerSettings { BaseUrl = "http://host:1234/v1" };
        var ids = UsersLmStudio.Select(m => JsonNode.Parse(m)!["key"]!.GetValue<string>()).ToList();
        var left = (await ServerProbe.CatalogAsync(new HttpClient(new Routes(LmStudio())), server, ids, CancellationToken.None)).ToList();
        var order = new List<string>();
        while (ModelChoice.Strongest(left) is { } next)
        {
            order.Add(next.Id);
            left.Remove(next);
        }
        Assert.Equal(
        [
            "qwen3.8-27b@q6_k_xl", "qwen3.8-27b@q4_k_s",                  // 79.8%
            "qwen3.6-27b-mtp@q6_k_xl", "qwen3.6-27b-mtp@q4_k_xl",         // 60.7%, as Qwen3.6 27B
            "gemma-4-31b-it", "gemma-4-31b-it-qat",                       // 43.4%
            "gemma-4-12b-it-qat",                                         // 27.3%
            // Not scored: a fine-tune of Qwen3.5 27B under its own name.
            "xortron.criminalcomputing.2026.27b.next@q6_k", "xortron.criminalcomputing.2026.27b.next@q5_k_m",
            "xortron.criminalcomputing.2026.27b.instruct.next-i1",
            "qwen3.8-27b@q4_0",                                           // an incomplete file
        ], order);
    }

    [Fact]
    public async Task A_picked_model_is_used_while_the_server_offers_it_and_stood_in_for_while_it_does_not()
    {
        var offered = await Probe(LmStudio(), picked: "gemma-4-31b-it");
        Assert.Equal("gemma-4-31b-it", offered.Model);
        Assert.False(offered.AutoPicked);
        Assert.Null(offered.ModelDetails);

        var missing = await Probe(LmStudio(keep: id => id != "qwen3.8-27b@q4_k_s"), picked: "qwen3.8-27b@q4_k_s");
        Assert.Equal("qwen3.8-27b@q6_k_xl", missing.Model);
        Assert.True(missing.AutoPicked);
        Assert.DoesNotContain("qwen3.8-27b@q4_k_s", missing.Models);
    }

    [Fact]
    public async Task Among_equally_strong_models_the_loaded_one_wins_then_the_first_listed()
    {
        // A second 6-bit build of the loaded model, listed first: the loaded one needs no loading.
        var loaded = UsersModel("qwen3.8-27b@q6_k_xl");
        var other = loaded.DeepClone().AsObject();
        other["key"] = "qwen3.8-27b@q6_k";
        other["loaded_instances"] = new JsonArray();
        Assert.Equal("qwen3.8-27b@q6_k_xl", (await Probe(LmStudio([other, loaded]))).Model);

        loaded["loaded_instances"] = new JsonArray();
        Assert.Equal("qwen3.8-27b@q6_k", (await Probe(LmStudio([other, loaded]))).Model);
    }

    [Theory]
    [InlineData("qwen3.8-27b@q6_k_xl", 79.8, "Qwen3.8 27B")]
    [InlineData("Qwen/Qwen3.6-27B-MTP-GGUF", 60.7, "Qwen3.6 27B")]
    [InlineData("qwen3.6-35b-a3b", 44.9, "Qwen3.6 35B A3B")]
    [InlineData("gemma-4-26b-a4b-it", 39.0, "Gemma 4 26B A4B")]
    [InlineData("google/gemma-4-31b-it-qat", 43.4, "Gemma 4 31B")]
    [InlineData("openai/gpt-oss-120b", 26.2, "gpt-oss-120b")]
    [InlineData("mistral-small-4", 21.0, "Mistral Small 4")]
    [InlineData("kimi-k2.6", 65.9, "Kimi K2.6")]
    public void Listed_models_are_found_by_their_names(string id, double score, string listed)
    {
        var rating = AgentBenchmark.Rate(ModelChoice.FromName(id));
        Assert.NotNull(rating);
        Assert.True(rating.Exact);
        Assert.Equal(score, rating.Score);
        Assert.Equal(listed, rating.Basis.Model);
    }

    [Theory]
    [InlineData("qwen3.9-27b", "Qwen3.8 27B")]      // a later version, as strong as the one it follows at least
    [InlineData("qwen3.5-27b", "Qwen3.5 9B")]       // a larger size, as strong as the smaller one at least
    [InlineData("kimi-k2.7", "Kimi K2.6")]
    [InlineData("gemma3:27b", null)]                // Gemma 3 27B solves 4.5%: not listed
    [InlineData("Qwen3-VL-32B-Instruct", null)]     // earlier than every listed Qwen
    [InlineData("gpt-oss-20b", null)]               // smaller than the listed one
    [InlineData("gemma-4-e4b-it", null)]            // no size to go by
    [InlineData("mistral-small-3.2-24b-instruct-2506", null)]
    [InlineData("deepseek-r1-distill-qwen-32b", null)]
    [InlineData("medgemma-27b-it", null)]
    [InlineData("xortron.criminalcomputing.2026.27b.next@q6_k", null)]
    public void Other_models_are_scored_as_the_listed_one_they_follow_or_not_at_all(string id, string? listed)
    {
        var rating = AgentBenchmark.Rate(ModelChoice.FromName(id));
        Assert.Equal(listed, rating?.Basis.Model);
        Assert.True(rating is null or { Exact: false });
    }

    [Fact]
    public void A_scored_model_comes_before_any_unscored_one_and_a_later_version_before_the_one_it_follows()
    {
        // Llama 3.3 70B solves 4.9% of the tasks; models under a fifth aren't listed.
        Assert.Equal("qwen3.5-9b", ModelChoice.Strongest([ModelChoice.FromName("llama-3.3-70b-instruct"), ModelChoice.FromName("qwen3.5-9b")])!.Id);
        Assert.Equal("qwen3.9-27b", ModelChoice.Strongest([ModelChoice.FromName("qwen3.8-27b"), ModelChoice.FromName("qwen3.9-27b")])!.Id);
        Assert.Equal("qwen3.5-27b", ModelChoice.Strongest([ModelChoice.FromName("qwen3.5-9b"), ModelChoice.FromName("qwen3.5-27b")])!.Id);
        Assert.Equal("27B, 5-bit, scored as Qwen3.5 9B: 29.2% on Terminal-Bench 2.1", ModelChoice.Describe(ModelChoice.FromName("qwen3.5-27b@q5_k_m")));
    }

    [Fact]
    public void The_table_tells_which_models_take_no_images_where_the_server_does_not()
    {
        // From the names alone every model is taken to see; MiniMax-M2.7 scores more but can't.
        Assert.Equal("gemma-4-12b-it", ModelChoice.Strongest([ModelChoice.FromName("minimax-m2.7"), ModelChoice.FromName("gemma-4-12b-it")])!.Id);
        Assert.Equal("minimax-m3", ModelChoice.Strongest([ModelChoice.FromName("minimax-m2.7"), ModelChoice.FromName("minimax-m3")])!.Id);
        // What the server says goes first.
        Assert.Equal("minimax-m2.7", ModelChoice.Strongest([ModelChoice.FromName("minimax-m2.7") with { Vision = true }, ModelChoice.FromName("gemma-4-12b-it")])!.Id);
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
        Assert.Equal("qwen3.8-27b@q6_k_xl", probe.Model); // 27B from the name, 6 bits from "Q6_K_XL"
        Assert.Equal("27B, 6-bit, 79.8% on Terminal-Bench 2.1", probe.ModelDetails);
        Assert.Equal(11, probe.Models.Count);
    }

    [Fact]
    public async Task Other_servers_are_judged_by_the_names()
    {
        var probe = await Probe(new() { ["/v1/models"] = Ids(["text-embedding-3-small", "llama-3.1-8b-instruct", "Llama-3.3-70B-Instruct-AWQ", "Llama-3.3-70B-Instruct-FP8", "gpt-4o"]) });
        Assert.Equal("Llama-3.3-70B-Instruct-FP8", probe.Model);
        Assert.Equal("70B, 8-bit", probe.ModelDetails);

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
        // Neither is scored: Qwen3 is older than every listed Qwen.
        Assert.Equal("qwen3-14b", ModelChoice.Strongest([ModelChoice.FromName("qwen3-30b-a3b"), ModelChoice.FromName("qwen3-14b")])!.Id);
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
    public async Task Servers_say_which_models_take_an_effort()
    {
        // LM Studio 0.4 lists a model's reasoning options; one that only turns thinking on and off
        // takes no effort, nor does one with no reasoning entry (qwen3.8 on the test server).
        const string lmStudio = """
            {"models":[
            {"type":"llm","key":"openai/gpt-oss-20b","params_string":"20B","capabilities":{"vision":false,"trained_for_tool_use":true,"reasoning":{"allowed_options":["low","medium","high"],"default":"medium"}},"loaded_instances":[]},
            {"type":"llm","key":"google/gemma-4-26b-a4b","params_string":"26B-A4B","capabilities":{"vision":true,"reasoning":{"allowed_options":["off","on"],"default":"on"}},"loaded_instances":[]},
            {"type":"llm","key":"qwen3.8-27b@q6_k_xl","params_string":"27B","capabilities":{"vision":true,"trained_for_tool_use":true},"loaded_instances":[]}
            ]}
            """;
        var catalog = await ServerProbe.CatalogAsync(new HttpClient(new Routes(new() { ["/api/v1/models"] = lmStudio })),
            new ServerSettings { BaseUrl = "http://forcepusher.ddns.net:1234/v1" }, ["openai/gpt-oss-20b", "google/gemma-4-26b-a4b", "qwen3.8-27b@q6_k_xl"], CancellationToken.None);
        Assert.Equal(["low", "medium", "high"], catalog[0].Efforts);
        Assert.Empty(catalog[1].Efforts!);
        Assert.Empty(catalog[2].Efforts!);

        // OpenRouter-style lists name "reasoning" among a model's parameters.
        var thinks = ServerProbe.FromListEntry(new ModelInfo("openai/gpt-5.6"), JsonNode.Parse("""{"id":"openai/gpt-5.6","supported_parameters":["tools","reasoning","include_reasoning"]}""")!.AsObject());
        Assert.Equal(["low", "medium", "high"], thinks.Efforts);
        var plain = ServerProbe.FromListEntry(new ModelInfo("nvidia/nemotron-3.5-super-vl:free"), JsonNode.Parse("""{"id":"nvidia/nemotron-3.5-super-vl:free","supported_parameters":["tools","max_tokens"]}""")!.AsObject());
        Assert.Empty(plain.Efforts!);
        // Claude through such a gateway: the levels of its version (dotted, as OpenRouter writes it),
        // low to high for one before efforts (a thinking budget there). Its signed thinking goes back.
        IReadOnlyList<string>? Routed(string id) => ServerProbe.FromListEntry(new ModelInfo(id), JsonNode.Parse($$"""{"id":"{{id}}","supported_parameters":["tools","reasoning"]}""")!.AsObject()).Efforts;
        Assert.Equal(Efforts.Levels, Routed("anthropic/claude-haiku-5.5"));
        Assert.Equal(["low", "medium", "high", "max"], Routed("anthropic/claude-sonnet-4.6"));
        Assert.Equal(["low", "medium", "high"], Routed("anthropic/claude-sonnet-4.5"));
        Assert.Empty(ServerProbe.FromListEntry(new ModelInfo("anthropic/claude-3-haiku"), JsonNode.Parse("""{"id":"anthropic/claude-3-haiku","supported_parameters":["tools"]}""")!.AsObject()).Efforts!);
        // Not Gemini yet: its tool calls want their own signatures back, untried.
        Assert.Empty(Routed("google/gemini-3.1-pro")!);
        // A bare list (vLLM, OpenAI) goes by the name.
        Assert.Equal(["low", "medium", "high"], ServerProbe.FromListEntry(new ModelInfo("gpt-5.6"), JsonNode.Parse("""{"id":"gpt-5.6"}""")!.AsObject()).Efforts);
        Assert.Empty(ServerProbe.FromListEntry(new ModelInfo("Qwen/Qwen3.8-27B"), JsonNode.Parse("""{"id":"Qwen/Qwen3.8-27B","max_model_len":131072}""")!.AsObject()).Efforts!);
    }

    /// <summary>Kilo's model list on 2026-10-16, a few of its 398 models, trimmed to the fields Sloperator reads.</summary>
    private const string KiloModels = """
        {"data":[
        {"id":"kilo-auto/free","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["max_tokens","tools"],"pricing":{"prompt":"0","completion":"0"},"context_length":256000},
        {"id":"anthropic/claude-opus-5.5","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["tools","tool_choice"],"pricing":{"prompt":"0.000004","completion":"0.00002"},"context_length":1000000},
        {"id":"nvidia/nemotron-3.5-super-vl:free","architecture":{"input_modalities":["text","image","video"],"output_modalities":["text"]},"supported_parameters":["max_tokens","tool_choice","tools"],"pricing":{"prompt":"0","completion":"0","discount":0},"context_length":1000000},
        {"id":"nvidia/nemotron-3.5-content-safety:free","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]},"supported_parameters":["max_tokens"],"pricing":{"prompt":"0","completion":"0"},"context_length":128000},
        {"id":"nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free","architecture":{"input_modalities":["text","audio","image","video"],"output_modalities":["text"]},"supported_parameters":["tools"],"pricing":{"prompt":"0","completion":"0"},"context_length":256000},
        {"id":"google/lyria-3-pro-preview","architecture":{"input_modalities":["text","image"],"output_modalities":["text","audio"]},"supported_parameters":["max_tokens"],"pricing":{"prompt":"0","completion":"0"},"context_length":1048576},
        {"id":"google/lyria-3-clip-preview","architecture":{"input_modalities":["text","image"],"output_modalities":["audio"]},"supported_parameters":["max_tokens"],"pricing":{"prompt":"0","completion":"0"},"context_length":1048576},
        {"id":"nvidia/nemotron-3-ultra-550b-a55b:free","architecture":{"input_modalities":["text"],"output_modalities":["text"]},"supported_parameters":["tools"],"pricing":{"prompt":"0","completion":"0"},"context_length":1000000}
        ]}
        """;

    private static Task<ProbeResult> ProbeKilo(string picked, string key = "") =>
        ServerProbe.CheckAsync(new HttpClient(new Routes(new() { ["/api/v1/models"] = KiloModels })),
            new ServerSettings { BaseUrl = KiloGateway.Address, Model = picked, ApiKey = key }, CancellationToken.None);

    [Fact]
    public async Task Without_a_key_Kilo_offers_only_its_free_models_and_says_what_each_takes()
    {
        var probe = await ProbeKilo(KiloGateway.Model);
        Assert.Equal(KiloGateway.Model, probe.Model);
        Assert.False(probe.AutoPicked);
        Assert.DoesNotContain("anthropic/claude-opus-5.5", probe.Models); // Kilo answers it only with a key
        Assert.DoesNotContain("google/lyria-3-clip-preview", probe.Models); // writes no text
        Assert.Contains("nvidia/nemotron-3-ultra-550b-a55b:free", probe.Models);
        Assert.True(probe.Vision);
        Assert.Equal(1_000_000, probe.LoadedContext);
        Assert.True(probe.ContextHosted); // the most Kilo takes for it, not a size the user loads it with
        var server = new ServerSettings();
        server.SetDetectedContext(probe.LoadedContext);
        Assert.Equal(1_000_000, server.ContextSize); // used whole on Auto

        // Sloperator starts there, and with nothing picked uses the free model it was tested with: not
        // Nano Omni, though its name is the only one that tells a size (it clicked 1 of 4 targets).
        Assert.Equal(KiloGateway.Address, new ServerSettings().BaseUrl);
        var auto = await ProbeKilo("");
        Assert.Equal(KiloGateway.Model, auto.Model);
        Assert.True(auto.AutoPicked);
        Assert.Equal("nvidia/nemotron-3-nano-omni-30b-a3b-reasoning:free",
            ModelChoice.Strongest((await ServerProbe.CatalogAsync(new HttpClient(new Routes([])), new ServerSettings(),
                JsonNode.Parse(KiloModels)!["data"]!.AsArray().OfType<JsonObject>().Where(m => m["pricing"]!["prompt"]!.GetValue<string>() == "0").ToList(),
                CancellationToken.None)))!.Id);
        Assert.False((await ProbeKilo("nvidia/nemotron-3-ultra-550b-a55b:free")).Vision);

        // With a key, everything it sells.
        Assert.Contains("anthropic/claude-opus-5.5", (await ProbeKilo("", "sk-test")).Models);
    }

    [Fact]
    public void The_model_Sloperator_picked_is_used_but_not_saved()
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
    /// one Sloperator picks. It only lists the models and reads their details; nothing is loaded.
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
            TestContext.Current.SendDiagnosticMessage($"{m.Id}: chat={m.Chat} vision={m.Vision} {ModelChoice.Describe(m)} loaded={m.Loaded} incomplete={ModelChoice.LooksIncomplete(m)}");
        TestContext.Current.SendDiagnosticMessage($"picked {probe.Model} ({probe.ModelDetails})");

        var chosen = catalog.Single(m => m.Id == probe.Model);
        var chosenScore = AgentBenchmark.Rate(chosen)?.Score ?? -1;
        foreach (var other in catalog.Where(m => m.Chat && !ModelChoice.LooksIncomplete(m) && m.Vision != false))
        {
            var score = AgentBenchmark.Rate(other)?.Score ?? -1;
            Assert.True(score <= chosenScore, $"{other.Id} scores higher than {chosen.Id}");
            if (score == chosenScore)
                Assert.True((ModelChoice.DenseEquivalent(other) ?? -1) <= (ModelChoice.DenseEquivalent(chosen) ?? -1), $"{other.Id} is larger than {chosen.Id}");
        }
    }
}
