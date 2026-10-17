using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Comfy;

/// <summary>A model among Civitai's most downloaded this month, with the tags that say what it is for, and its place in the list.</summary>
public sealed record PopularModel(OnlineModel Model, IReadOnlyList<string> Tags, int Rank);

/// <summary>Civitai's most downloaded checkpoints and LoRAs of a month, as fetched then.</summary>
public sealed record PopularSnapshot(DateTime Fetched, List<PopularModel> Models);

/// <summary>
/// What people make pictures with now: Civitai's 100 most downloaded checkpoints and LoRAs this
/// month, kept in Data/comfyui/popular-models.json and fetched again once it is a week old, to be
/// found by what they are for (their tags: anime, furry, photography, landscape…). Searched for
/// "anime", Civitai ranks its matches by downloads of all time, and years-old SD 1.5 models came
/// first; the month's most downloaded checkpoints in October 2026 were on bases no model's memory
/// knew (Anima, Krea 2, Z-Image Turbo, MiniMax H3), and Civitai answers a search narrowed to a
/// month with nothing. Models whose tags say porn are left out, as Civitai's own NSFW mark was off
/// on several of them.
/// </summary>
public sealed class ComfyPopularModels(string stateDirectory)
{
    private static readonly TimeSpan Life = TimeSpan.FromDays(7);
    internal const int PerKind = 100;

    /// <summary>The kinds kept, as find_models names them, with Civitai's type.</summary>
    private static readonly (string Kind, string Civitai)[] Kinds = [("checkpoint", "Checkpoint"), ("lora", "LORA")];

    private static readonly HashSet<string> Explicit = new(StringComparer.OrdinalIgnoreCase) { "porn", "hentai", "nsfw", "nude", "nudity" };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private PopularSnapshot? _snapshot;

    private string CacheFile => Path.Combine(stateDirectory, "popular-models.json");

    /// <summary>
    /// The month's popular models: the saved list, or a fresh one when it is a week old. When
    /// Civitai can't be asked, the old list (with its date) or null.
    /// </summary>
    public async Task<PopularSnapshot?> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _snapshot ??= Load();
            if (_snapshot is { } known && DateTime.UtcNow - known.Fetched < Life)
                return known;
            try
            {
                var models = new List<PopularModel>();
                foreach (var (_, type) in Kinds)
                {
                    var page = await ComfyModelSearch.GetJsonAsync($"{ComfyModelSearch.CivitaiBase}/api/v1/models?limit={PerKind}&types={type}" +
                                                                   "&sort=Most%20Downloaded&period=Month&nsfw=false", ct).ConfigureAwait(false);
                    var rank = 0;
                    foreach (var item in (page?["items"] as JsonArray ?? []).OfType<JsonObject>())
                    {
                        rank++;
                        var tags = (item["tags"] as JsonArray ?? []).Select(t => ((string?)t)?.Trim()).OfType<string>().Where(t => t.Length > 0).ToList();
                        if (!tags.Any(Explicit.Contains) && ComfyModelSearch.FromCivitai(item, null) is { } model && !models.Any(p => p.Model.Page == model.Page))
                            models.Add(new PopularModel(model, tags, rank));
                    }
                }
                _snapshot = new PopularSnapshot(DateTime.UtcNow, models);
                Save(_snapshot);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Offline or Civitai down: the list from before, however old, said with its date.
            }
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The ones of a <paramref name="kind"/> (checkpoint, LoRA; any when null) on a base (any when
    /// null) whose tags, name or base hold every word, checkpoints first, each in the month's
    /// order; when none holds them all, those that hold the most ("anime girl picture": no tag
    /// says picture).
    /// </summary>
    public static List<PopularModel> Matching(IEnumerable<PopularModel> all, IReadOnlyList<string> words, string? kind, string? baseModel)
    {
        var folder = ComfyModelSearch.KindOf(kind)?.Folder;
        var scored = all.Where(p => (folder is null || p.Model.Folder == folder) && (baseModel is null || ComfyModelSearch.SameBase(p.Model.Base, baseModel)))
            .Select(p =>
            {
                var text = ComfyNodeLibrary.Squash(string.Join(" ", p.Tags.Append(p.Model.Title).Append(p.Model.Base ?? "")));
                return (Model: p, Score: words.Count(w => text.Contains(w, StringComparison.Ordinal)));
            })
            .ToList();
        var best = scored.Count == 0 ? 0 : scored.Max(s => s.Score);
        return scored.Where(s => words.Count == 0 || s.Score > 0 && s.Score == best)
            .Select(s => s.Model).OrderBy(p => p.Model.Folder, StringComparer.Ordinal).ThenBy(p => p.Rank).ToList();
    }

    /// <summary>The bases a kind's popular models are on, the most used first: "Anima 51, Illustrious 17, Krea 2 13".</summary>
    public static string Bases(IEnumerable<PopularModel> all, string folder) =>
        string.Join(", ", all.Where(p => p.Model.Folder == folder && p.Model.Base is { Length: > 0 })
            .GroupBy(p => p.Model.Base!).OrderByDescending(g => g.Count()).Take(8).Select(g => $"{g.Key} {g.Count()}"));

    private PopularSnapshot? Load()
    {
        try
        {
            return File.Exists(CacheFile) ? JsonSerializer.Deserialize(File.ReadAllText(CacheFile), PopularJson.Default.PopularSnapshot) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private void Save(PopularSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(stateDirectory);
            AtomicFile.Write(CacheFile, JsonSerializer.Serialize(snapshot, PopularJson.Default.PopularSnapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PopularSnapshot))]
internal sealed partial class PopularJson : JsonSerializerContext;
