using System.Text.Json.Nodes;
using BlackBox.Core.Comfy;
using BlackBox.Core.Settings;
using BlackBox.Core.Tools;
using Xunit;

namespace BlackBox.Core.Tests;

// What people use now, by task: searched for "anime", Civitai ranks by downloads of all time and
// years-old SD 1.5 models came first, while the month's most downloaded were on bases no model's
// memory knew.
[Collection(CivitaiStandIn.Name)]
public class PopularModelTests : IDisposable
{
    private readonly FileServer _civitai = new();
    private readonly string _before = ComfyModelSearch.CivitaiBase;
    private readonly string _home = Directory.CreateTempSubdirectory("bb-popular-").FullName;
    private readonly ToolContext _ctx;
    private readonly string _comfyDir;

    public PopularModelTests()
    {
        ComfyModelSearch.CivitaiBase = _civitai.Base;
        var paths = new AppPaths(_home);
        _comfyDir = paths.ComfyDir;
        Directory.CreateDirectory(_comfyDir);
        // No ComfyUI: the list doesn't need one.
        File.WriteAllText(Path.Combine(_comfyDir, "state.json"), new JsonObject { ["url"] = "http://127.0.0.1:9/" }.ToJsonString());
        _ctx = new ToolContext { Platform = new FakePlatform(), Settings = new AgentSettings(), WorkingDirectory = _home, Comfy = ComfyHub.For(_comfyDir), ScratchDirectory = paths.ScratchDir };
        _civitai.Json("/api/v1/models", """
            {"items": [
              {"id": 1, "name": "MiaoMiao Harem", "type": "Checkpoint", "nsfw": false, "tags": ["anime", "girls", "base model"], "stats": {"downloadCount": 9},
               "modelVersions": [{"id": 11, "name": "2.9B", "baseModel": "Anima", "files": [{"name": "miao.safetensors", "sizeKB": 5000000, "primary": true, "downloadUrl": "https://x/1"}]}]},
              {"id": 2, "name": "Explicit Mix", "type": "Checkpoint", "nsfw": false, "tags": ["anime", "porn"],
               "modelVersions": [{"id": 21, "baseModel": "Illustrious", "files": [{"name": "x.safetensors", "primary": true, "downloadUrl": "https://x/2"}]}]},
              {"id": 3, "name": "Nova Furry AM", "type": "Checkpoint", "nsfw": false, "tags": ["anime", "furry", "anthro"],
               "modelVersions": [{"id": 31, "name": "v4.0", "baseModel": "Anima", "files": [{"name": "novaFurryAM.safetensors", "sizeKB": 5600000, "primary": true, "downloadUrl": "https://x/3"}]}]},
              {"id": 4, "name": "ReKemono", "type": "Checkpoint", "nsfw": false, "tags": ["furry", "kemono"],
               "modelVersions": [{"id": 41, "name": "v11", "baseModel": "Illustrious", "files": [{"name": "rekemono.safetensors", "sizeKB": 6800000, "primary": true, "downloadUrl": "https://x/4"}]}]},
              {"id": 5, "name": "Landscapes", "type": "Checkpoint", "nsfw": false, "tags": ["landscape", "photography"],
               "modelVersions": [{"id": 51, "name": "1", "baseModel": "Krea 2", "files": [{"name": "land.safetensors", "sizeKB": 12000000, "primary": true, "downloadUrl": "https://x/5"}]}]},
              {"id": 6, "name": "HuslyoRealismXL", "type": "Checkpoint", "nsfw": false, "tags": ["photorealistic", "realistic"],
               "modelVersions": [{"id": 61, "name": "V3", "baseModel": "SDXL 1.0", "files": [{"name": "huslyo.safetensors", "sizeKB": 6800000, "primary": true, "downloadUrl": "https://x/6"}]}]}
            ]}
            """);
    }

    public void Dispose()
    {
        ComfyModelSearch.CivitaiBase = _before;
        _civitai.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch (IOException) { }
    }

    private Task<ToolResult> Call(ComfyTool tool, string json) => tool.ExecuteAsync(JsonNode.Parse(json)!.AsObject(), _ctx, TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_months_list_is_fetched_once_a_week_and_found_by_its_tags()
    {
        var popular = new ComfyPopularModels(_comfyDir);
        var snapshot = await popular.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(snapshot);
        // Checkpoints and LoRAs, this month's order kept; tagged porn left out, though Civitai didn't mark it.
        Assert.Contains(_civitai.Requests, r => r.Contains("types=Checkpoint&sort=Most%20Downloaded&period=Month", StringComparison.Ordinal));
        Assert.Contains(_civitai.Requests, r => r.Contains("types=LORA&sort=Most%20Downloaded&period=Month", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Models, p => p.Model.Title.StartsWith("Explicit", StringComparison.Ordinal));
        Assert.Equal(["Nova Furry AM (v4.0)", "ReKemono (v11)"],
            ComfyPopularModels.Matching(snapshot.Models, ComfyNodeLibrary.Words("furry"), "checkpoint", null).Select(p => p.Model.Title));
        Assert.Equal(["Nova Furry AM (v4.0)"], ComfyPopularModels.Matching(snapshot.Models, ComfyNodeLibrary.Words("furry"), null, "Anima").Select(p => p.Model.Title));
        // No tag says "picture": the ones that hold the most of the words.
        Assert.Equal(["MiaoMiao Harem (2.9B)", "Nova Furry AM (v4.0)"],
            ComfyPopularModels.Matching(snapshot.Models, ComfyNodeLibrary.Words("anime picture"), null, null).Select(p => p.Model.Title).Distinct());
        Assert.StartsWith("Anima 2, ", ComfyPopularModels.Bases(snapshot.Models, "checkpoints"));
        // A model named by name, which isn't among them: "XL" alone doesn't make an SDXL model one.
        Assert.Empty(ComfyPopularModels.Matching(snapshot.Models, ComfyNodeLibrary.Words("Juggernaut XL"), "checkpoint", null));
        Assert.Equal(["HuslyoRealismXL (V3)"], ComfyPopularModels.Matching(snapshot.Models, ComfyNodeLibrary.Words("realistic XL"), null, null).Select(p => p.Model.Title));

        // Kept for a week, also for a new start of BlackBox; then fetched again.
        var asked = _civitai.Requests.Count;
        Assert.NotNull(await new ComfyPopularModels(_comfyDir).GetAsync(TestContext.Current.CancellationToken));
        Assert.Equal(asked, _civitai.Requests.Count);
        var file = Path.Combine(_comfyDir, "popular-models.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace($"\"fetched\":\"{snapshot.Fetched:yyyy-MM-dd}", $"\"fetched\":\"{snapshot.Fetched.AddDays(-8):yyyy-MM-dd}"));
        await new ComfyPopularModels(_comfyDir).GetAsync(TestContext.Current.CancellationToken);
        Assert.True(_civitai.Requests.Count > asked);
    }

    [Fact]
    public async Task Find_models_without_words_lists_the_months_most_downloaded_and_their_bases()
    {
        var result = await Call(new ComfyTool(), """{"action": "find_models"}""");

        Assert.False(result.IsError, result.Text);
        Assert.Contains("Civitai's most downloaded checkpoints this month", result.Text);
        Assert.Contains("The month's most downloaded are on these base models: Anima 2, ", result.Text);
        // No ComfyUI runs and none has been used from this Data folder: that isn't "not installed".
        Assert.Contains("none has been used from this BlackBox yet (action=status finds the PC's own", result.Text);
        Assert.DoesNotContain("isn't installed", result.Text);
        Assert.Contains("1. MiaoMiao Harem (2.9B): checkpoint for Anima, 4.8 GB, #1 this month (Civitai,", result.Text);
        Assert.Contains("miao.safetensors: url=https://x/1 model_folder=checkpoints", result.Text);
    }

    [Fact]
    public async Task Find_models_with_words_puts_the_months_most_downloaded_for_them_first()
    {
        var result = await Call(new ComfyTool(), """{"action": "find_models", "search": "furry"}""");

        Assert.False(result.IsError, result.Text);
        var popular = result.Text.IndexOf("Most downloaded on Civitai this month for \"furry\"", StringComparison.Ordinal);
        Assert.True(popular >= 0, result.Text);
        Assert.True(result.Text.IndexOf("1. Nova Furry AM (v4.0): checkpoint for Anima", StringComparison.Ordinal) > popular, result.Text);
        Assert.Contains("2. ReKemono (v11): checkpoint for Illustrious", result.Text);
    }
}
