namespace BlackBox.Core.Settings;

/// <summary>A local model BlackBox knows how to download and run.</summary>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    string Description,
    string ModelPath,
    string ModelUrl,
    string MmprojPath,
    string MmprojUrl,
    long DownloadBytes,
    int ContextSize);

public static class ModelCatalog
{
    public const string CustomId = "custom";

    public static readonly ModelInfo Qwen38IQ2S = new(
        Id: "qwen3.8-27b-iq2s",
        DisplayName: "Qwen3.8 27B (IQ2_S)",
        Description: "Default. Strongest at multi-step desktop tasks; thinks before acting.",
        // Kept at the root of Models/ so existing downloads keep working.
        ModelPath: "Models/Qwen3.8-27B-UD-IQ2_S.gguf",
        ModelUrl: "https://huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/Qwen3.8-27B-UD-IQ2_S.gguf",
        MmprojPath: "Models/mmproj-F16.gguf",
        MmprojUrl: "https://huggingface.co/unsloth/Qwen3.8-27B-GGUF/resolve/main/mmproj-F16.gguf",
        DownloadBytes: 8_371_970_048 + 927_607_488,
        ContextSize: 81920);

    public static readonly ModelInfo Gemma4_12BQat = new(
        Id: "gemma-4-12b-qat",
        DisplayName: "Gemma 4 12B QAT (Q4_K_XL)",
        Description: "Smaller and faster; quantization-aware trained, so 4-bit loses little quality.",
        ModelPath: "Models/gemma-4-12B-it-qat/gemma-4-12B-it-qat-UD-Q4_K_XL.gguf",
        ModelUrl: "https://huggingface.co/unsloth/gemma-4-12B-it-qat-GGUF/resolve/main/gemma-4-12B-it-qat-UD-Q4_K_XL.gguf",
        MmprojPath: "Models/gemma-4-12B-it-qat/mmproj-F16.gguf",
        MmprojUrl: "https://huggingface.co/unsloth/gemma-4-12B-it-qat-GGUF/resolve/main/mmproj-F16.gguf",
        DownloadBytes: 6_716_356_800 + 175_115_840,
        ContextSize: 81920);

    public static IReadOnlyList<ModelInfo> All { get; } = [Qwen38IQ2S, Gemma4_12BQat];

    public static ModelInfo? Find(string? id) => All.FirstOrDefault(m => m.Id == id);

    /// <summary>Points the local settings at a catalog model (files, URLs, context).</summary>
    public static void Apply(ModelInfo model, LocalModelSettings local)
    {
        local.SelectedModel = model.Id;
        local.ModelPath = model.ModelPath;
        local.ModelUrl = model.ModelUrl;
        local.MmprojPath = model.MmprojPath;
        local.MmprojUrl = model.MmprojUrl;
        local.ContextSize = model.ContextSize;
    }
}
