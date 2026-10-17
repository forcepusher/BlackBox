using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

// How the maker of a checkpoint or LoRA uses it, from Civitai: Nemotron prompted the user's Nova Furry
// XL in plain sentences at 512x760, where its page asks for quality tags and its pictures were made at
// 768x1344 with a hires pass.
[Collection(CivitaiStandIn.Name)]
public class ModelCardTests : IDisposable
{
    private readonly FileServer _civitai = new();
    private readonly string _before = ComfyModelSearch.CivitaiBase;
    private readonly string _dir = Directory.CreateTempSubdirectory("bb-cards-").FullName;

    public ModelCardTests() => ComfyModelSearch.CivitaiBase = _civitai.Base;

    public void Dispose()
    {
        ComfyModelSearch.CivitaiBase = _before;
        _civitai.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // As Civitai's page for Nova Furry XL has it, cut down.
    internal const string NovaDescription =
        "<p>Anima version is available!</p><p>Nova Furry XL A 2d/2.5d furry checkpoint model that can have great details on any type of furs, scales and feathers, " +
        "which aims to be XL-fied Peaki Furry and is loved by many people who wanted a model like this for a very long time and asked for it again and again.</p>" +
        "<h3><strong>Rules</strong></h3><p>You cannot use the generated images for commercial use if it's not edited</p>" +
        "<h3><strong>Recommended settings</strong>:</h3><p>Sampler: Euler a for best performance</p><p>Steps: 20-50 steps</p>" +
        "<p><strong>(Illustrious)</strong></p><p>CFG Scale: 3-5</p><p>Prompt: masterpiece, best quality, amazing quality, very aesthetic, furry, anthro, {Prompt}</p>" +
        "<p>Negative Prompt: human, multiple tails, lowres, bad anatomy, bad hands, (worst quality, bad quality:1.2), watermark</p>";

    // One of its maker's pictures, as Civitai's images API gives it with withMeta=true.
    internal const string NovaImage = """
        {"items": [{"id": 124939704, "width": 1152, "height": 2016, "nsfwLevel": "None",
          "meta": {"seed": 3468117727, "steps": 20, "width": 768, "height": 1344, "prompt": "masterpiece, best quality, 1girl, (furry, anthro:1.2), white rabbit girl, BREAK, depth of field",
                   "sampler": "Euler a", "cfgScale": 4.5, "clipSkip": 2, "Hires upscale": "1.5", "Denoising strength": "0.4",
                   "negativePrompt": "3d, rendered, human, multiple tails, lowres"},
          "username": "Crody", "baseModel": "Illustrious"}]}
        """;

    /// <summary>A model file of <paramref name="size"/> bytes made from <paramref name="seed"/>, and its SHA-256.</summary>
    private (string Path, string Hash) ModelFile(string name, int size = 0x120000, byte seed = 7)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return (path, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private void Civitai(string hash, long size, string baseModel = "Illustrious")
    {
        _civitai.Json($"/api/v1/model-versions/by-hash/{hash}", $$$"""
            {"id": 2792241, "modelId": 503815, "name": "IL v17.0", "baseModel": "{{{baseModel}}}", "trainedWords": [],
             "files": [{"name": "novaFurryXL_ilV170.safetensors", "sizeKB": {{{size / 1024.0}}}, "hashes": {"SHA256": "{{{hash}}}"}}]}
            """);
        _civitai.Json("/api/v1/models/503815", new JsonObject
        {
            ["id"] = 503815, ["name"] = "Nova Furry XL", ["type"] = "Checkpoint", ["description"] = NovaDescription,
            ["creator"] = new JsonObject { ["username"] = "Crody" },
        }.ToJsonString());
        _civitai.Json("/api/v1/images", NovaImage);
    }

    [Fact]
    public async Task A_checkpoint_found_by_its_hash_comes_with_its_pages_settings_and_its_makers_recipe()
    {
        var (path, hash) = ModelFile("novaFurryXL_ilV170.safetensors");
        Civitai(hash, new FileInfo(path).Length);
        var cards = new ComfyModelCards(_dir);

        var card = await cards.LookUpAsync(path, TestContext.Current.CancellationToken);

        Assert.NotNull(card);
        Assert.Equal("Nova Furry XL (IL v17.0)", card.Title);
        Assert.Equal("Illustrious", card.Base);
        // From the first line about settings, without the paragraphs about the model itself.
        Assert.StartsWith("Recommended settings :\nSampler: Euler a for best performance\nSteps: 20-50 steps\n(Illustrious)\nCFG Scale: 3-5\nPrompt: masterpiece", card.Advice);
        Assert.DoesNotContain("Peaki", card.Advice);
        var example = Assert.Single(card.Examples);
        Assert.Equal((768, 1344, 4.5, 20, 2), (example.Width, example.Height, example.Cfg, example.Steps, example.ClipSkip));
        Assert.Equal("then hires 1.5x at denoise 0.4", example.Hires);

        var text = ComfyModelCards.Format("novaFurryXL_ilV170.safetensors", card);
        Assert.Contains("Illustrious, an SDXL model, made for about 1 megapixel", text);
        Assert.Contains("Euler a (sampler_name euler_ancestral, scheduler normal), CFG 4.5, 20 steps, clip skip 2, 768x1344, then hires 1.5x at denoise 0.4", text);
        Assert.Contains("CLIPSetLastLayer", text);
        Assert.Contains("BREAK is AUTOMATIC1111's", text);
        // The maker's own pictures only.
        Assert.Contains(_civitai.Requests, r => r.StartsWith("/api/v1/images?modelVersionId=2792241&username=Crody&withMeta=true", StringComparison.Ordinal));

        // Asked once: the next look is the saved card, also for a new start of BlackBox.
        var asked = _civitai.Requests.Count;
        Assert.NotNull(await new ComfyModelCards(_dir).LookUpAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(asked, _civitai.Requests.Count);
    }

    // This PC's Pony Diffusion V6 XL got the card of "Incomplete anime pony", a fine-tune of it of the
    // same size: the 64 KB a megabyte in that Civitai's quick hash (AutoV1) reads were the same in both.
    [Fact]
    public async Task A_file_is_looked_up_by_its_whole_hash_not_the_quick_one_its_fine_tunes_share()
    {
        var (path, hash) = ModelFile("ponyDiffusionV6XL_v6StartWithThisOne.safetensors");
        var bytes = File.ReadAllBytes(path);
        var quick = Convert.ToHexString(SHA256.HashData(bytes.AsSpan(0x100000, 0x10000)))[..8];
        _civitai.Json($"/api/v1/model-versions/by-hash/{quick}", """{"id": 1, "modelId": 336883, "name": "v1.0", "baseModel": "Pony", "files": []}""");
        _civitai.Json("/api/v1/models/336883", """{"id": 336883, "name": "Incomplete anime pony"}""");
        _civitai.Json($"/api/v1/model-versions/by-hash/{hash}", """{"id": 290640, "modelId": 257749, "name": "V6 (start with this one)", "baseModel": "Pony", "files": []}""");
        _civitai.Json("/api/v1/models/257749", """{"id": 257749, "name": "Pony Diffusion V6 XL"}""");

        var card = await new ComfyModelCards(_dir).LookUpAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("Pony Diffusion V6 XL (V6 (start with this one))", card?.Title);
        Assert.DoesNotContain(_civitai.Requests, r => r.Contains(quick, StringComparison.Ordinal));

        // Civitai doesn't know it: not asked again for a while.
        var (unknown, _) = ModelFile("mine.safetensors", seed: 9);
        var cards = new ComfyModelCards(_dir);
        Assert.Null(await cards.LookUpAsync(unknown, TestContext.Current.CancellationToken));
        var asked = _civitai.Requests.Count;
        Assert.Null(await cards.LookUpAsync(unknown, TestContext.Current.CancellationToken));
        Assert.Equal(asked, _civitai.Requests.Count);
    }

    [Fact]
    public async Task A_files_hash_is_worked_out_once_and_again_when_the_file_changes()
    {
        var (path, hash) = ModelFile("x.safetensors");
        Assert.Equal(hash, await new ComfyModelCards(_dir).HashAsync(path));
        Assert.Contains(hash, File.ReadAllText(Path.Combine(_dir, "model-hashes.json")));

        // Kept with the file's size and time: a new start of BlackBox reads it back.
        File.WriteAllText(Path.Combine(_dir, "model-hashes.json"), File.ReadAllText(Path.Combine(_dir, "model-hashes.json")).Replace(hash, "SAVED"));
        Assert.Equal("SAVED", await new ComfyModelCards(_dir).HashAsync(path));

        // A file changed since is hashed again.
        var (_, changed) = ModelFile("x.safetensors", seed: 8);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(changed, await new ComfyModelCards(_dir).HashAsync(path));
        Assert.Null(await new ComfyModelCards(_dir).HashAsync(Path.Combine(_dir, "gone.safetensors")));
    }

    [Fact]
    public void Embeddings_its_maker_used_are_said_to_be_ones()
    {
        // DreamShaper 8's maker's picture: "BadDream" in its negative prompt is an embedding, which ComfyUI reads as a word.
        var image = JsonNode.Parse("""
            {"meta": {"Size": "512x832", "steps": 30, "prompt": "(masterpiece), portrait of a knight", "sampler": "DPM++ SDE Karras", "cfgScale": 9, "clipSkip": 2,
                      "negativePrompt": "BadDream, (UnrealisticDream:1.3)", "civitaiResources": [{"type": "checkpoint", "modelVersionId": 128713}, {"type": "embed", "modelVersionId": 106916}]}}
            """)!.AsObject();
        var example = ComfyModelCards.Example(image)!;
        Assert.True(example.Embeddings);
        Assert.Equal((512, 832), (example.Width, example.Height));
        var text = ComfyModelCards.Format("dreamshaper_8.safetensors", new ModelCard("DreamShaper (8)", "https://civitai.com/models/4384", "SD 1.5", [], null, [example]));
        Assert.Contains("SD 1.5, made for about 512x512", text);
        Assert.Contains("DPM++ SDE Karras (sampler_name dpmpp_sde, scheduler karras)", text);
        Assert.Contains("embedding:Name", text);
    }

    [Theory]
    [InlineData("Euler a", "euler_ancestral", "normal")]
    [InlineData("DPM++ 2M Karras", "dpmpp_2m", "karras")]
    [InlineData("DPM++ SDE Karras", "dpmpp_sde", "karras")]
    [InlineData("DPM++ 2M SDE Exponential", "dpmpp_2m_sde", "exponential")]
    [InlineData("UniPC", "uni_pc", "normal")]
    public void Samplers_are_named_as_comfyui_names_them(string a1111, string sampler, string scheduler) =>
        Assert.Equal((sampler, scheduler), ComfyModelCards.ComfySampler(a1111));

    [Theory]
    [InlineData("SD 1.5", 512)]
    [InlineData("SD 2.1 768", 768)]
    [InlineData("SDXL 1.0", 1024)]
    [InlineData("Pony", 1024)]
    [InlineData("Illustrious", 1024)]
    [InlineData("Flux.1 D", 1024)]
    [InlineData("LTXV", null)]
    [InlineData("Wan Video 14B t2v", null)]
    public void Each_base_model_has_the_size_it_is_made_for(string baseModel, int? side) =>
        Assert.Equal(side, ComfyModelCards.NativeSide(baseModel));

    [Fact]
    public void A_files_own_tensors_and_metadata_say_its_base_model()
    {
        Assert.Equal("SDXL", ComfyModelCards.BaseFromTensors(["conditioner.embedders.1.model.ln_final.weight", "model.diffusion_model.out.0.bias"]));
        Assert.Equal("SD 1.5", ComfyModelCards.BaseFromTensors(["cond_stage_model.transformer.text_model.final_layer_norm.bias"]));
        Assert.Equal("Flux", ComfyModelCards.BaseFromTensors(["model.diffusion_model.double_blocks.0.img_attn.norm.key_norm.scale"]));
        Assert.Equal("SDXL", ComfyModelCards.BaseFromTensors(["lora_te2_text_model_encoder_layers_0_mlp_fc1.alpha"]));
        Assert.Equal("SDXL", ComfyModelCards.BaseFromName("sdxl_base_v1-0"));
        Assert.Equal("SD 1.5", ComfyModelCards.BaseFromName("sd_v1"));

        var path = Path.Combine(_dir, "lora.safetensors");
        var header = Encoding.UTF8.GetBytes("""{"__metadata__": {"ss_base_model_version": "sdxl_base_v1-0"}, "lora_unet_x.alpha": {"dtype": "F16", "shape": [], "data_offsets": [0, 2]}}""");
        File.WriteAllBytes(path, [.. BitConverter.GetBytes((ulong)header.Length), .. header, 0, 0]);
        Assert.Equal("SDXL", ComfyModelCards.BaseFromFile(path));
        Assert.Null(ComfyModelCards.BaseFromFile(ModelFile("noise.safetensors").Path));
    }
}

/// <summary>The cards in the comfyui tool's answers: once each, with the files it lists and the runs it makes.</summary>
[Collection(CivitaiStandIn.Name)]
public class ModelCardToolTests : IDisposable
{
    private readonly FileServer _civitai = new();
    private readonly FakeComfyServer _comfy = new();
    private readonly string _home = Directory.CreateTempSubdirectory("bb-cards-home-").FullName;
    private readonly string _before = ComfyModelSearch.CivitaiBase;
    private readonly ToolContext _ctx;

    public ModelCardToolTests()
    {
        ComfyModelSearch.CivitaiBase = _civitai.Base;
        var paths = new AppPaths(_home);
        Directory.CreateDirectory(paths.ComfyDir);
        File.WriteAllText(Path.Combine(paths.ComfyDir, "state.json"), new JsonObject { ["url"] = _comfy.Url.ToString() }.ToJsonString());
        _ctx = new ToolContext
        {
            Platform = new FakePlatform(),
            Settings = new AgentSettings(),
            WorkingDirectory = _home,
            Comfy = ComfyHub.For(paths.ComfyDir),
            ScratchDirectory = paths.ScratchDir,
        };
        // The fake server's sdxl.safetensors, with Civitai's answers for it.
        var checkpoints = Path.Combine(_comfy.CodeDir, "models", "checkpoints");
        Directory.CreateDirectory(checkpoints);
        var bytes = new byte[0x120000];
        new Random(3).NextBytes(bytes);
        File.WriteAllBytes(Path.Combine(checkpoints, "sdxl.safetensors"), bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        _civitai.Json($"/api/v1/model-versions/by-hash/{hash}", $$$"""
            {"id": 2792241, "modelId": 503815, "name": "IL v17.0", "baseModel": "Illustrious", "trainedWords": [],
             "files": [{"name": "sdxl.safetensors", "sizeKB": {{{bytes.Length / 1024.0}}}, "hashes": {"SHA256": "{{{hash}}}"}}]}
            """);
        _civitai.Json("/api/v1/models/503815", new JsonObject
        {
            ["id"] = 503815, ["name"] = "Nova Furry XL", ["description"] = ModelCardTests.NovaDescription, ["creator"] = new JsonObject { ["username"] = "Crody" },
        }.ToJsonString());
        _civitai.Json("/api/v1/images", ModelCardTests.NovaImage);
    }

    public void Dispose()
    {
        ComfyModelSearch.CivitaiBase = _before;
        _civitai.Dispose();
        _comfy.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private Task<ToolResult> Call(ComfyTool tool, string json) => tool.ExecuteAsync(JsonNode.Parse(json)!.AsObject(), _ctx, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Looking_a_checkpoint_up_by_name_brings_its_card_once()
    {
        var tool = new ComfyTool();

        var listed = await Call(tool, """{"action": "models", "search": "sdxl"}""");
        Assert.Contains("sdxl.safetensors is Nova Furry XL (IL v17.0) on Civitai", listed.Text);
        Assert.Contains("Recommended settings", listed.Text);

        // Said once in a task.
        Assert.DoesNotContain("on Civitai", (await Call(tool, """{"action": "models", "search": "sdxl"}""")).Text);
    }

    [Fact]
    public async Task A_run_well_under_the_checkpoints_size_is_said_with_its_card()
    {
        var workflow = """
            {"1": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": "sdxl.safetensors"}},
             "2": {"class_type": "CLIPTextEncode", "inputs": {"text": "a bunny", "clip": ["1", 1]}},
             "3": {"class_type": "CLIPTextEncode", "inputs": {"text": "", "clip": ["1", 1]}},
             "4": {"class_type": "EmptyLatentImage", "inputs": {"width": 512, "height": 760, "batch_size": 1}},
             "5": {"class_type": "KSampler", "inputs": {"model": ["1", 0], "positive": ["2", 0], "negative": ["3", 0], "latent_image": ["4", 0],
                   "seed": 1, "steps": 20, "cfg": 3, "sampler_name": "euler_ancestral", "scheduler": "normal", "denoise": 1}},
             "6": {"class_type": "VAEDecode", "inputs": {"samples": ["5", 0], "vae": ["1", 2]}},
             "7": {"class_type": "SaveImage", "inputs": {"images": ["6", 0], "filename_prefix": "bunny"}}}
            """;
        var tool = new ComfyTool();

        var ran = await Call(tool, new JsonObject { ["action"] = "run", ["workflow"] = workflow }.ToJsonString());

        Assert.False(ran.IsError, ran.Text);
        Assert.Contains("Note: it made its pictures at 512x760, under half the size sdxl.safetensors is made for (Illustrious, an SDXL model", ran.Text);
        Assert.Contains("sdxl.safetensors is Nova Furry XL (IL v17.0) on Civitai", ran.Text);

        // At its size: no note, and the card isn't said again.
        var again = await Call(tool, new JsonObject { ["action"] = "run", ["workflow"] = workflow.Replace("512", "832").Replace("760", "1216") }.ToJsonString());
        Assert.DoesNotContain("under half the size", again.Text);
        Assert.DoesNotContain("on Civitai", again.Text);
    }
}

/// <summary>The tests that point <see cref="ComfyModelSearch.CivitaiBase"/> at a stand-in, one at a time: it is shared by all.</summary>
internal static class CivitaiStandIn
{
    public const string Name = "Civitai stand-in";
}
