using System.Globalization;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Llm;

/// <summary>What is known about one model a server offers; nulls where nothing says.</summary>
public sealed record ModelInfo(string Id)
{
    /// <summary>False for models that can't hold a chat: embedding, reranking and speech models.</summary>
    public bool Chat { get; init; } = true;

    /// <summary>Whether it takes images; null when nothing says.</summary>
    public bool? Vision { get; init; }

    /// <summary>Parameters, in billions; for a mixture of experts, all of them.</summary>
    public double? Params { get; init; }

    /// <summary>Parameters a mixture of experts uses for each token, in billions ("30B-A3B": 3).</summary>
    public double? ActiveParams { get; init; }

    /// <summary>Bits per weight of its quantization (Q6_K: 6, F16: 16).</summary>
    public double? Bits { get; init; }

    /// <summary>Size of its files on the server.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Loaded on the server, so it answers without loading first.</summary>
    public bool Loaded { get; init; }
}

/// <summary>
/// The model to use when none was picked by hand, or the picked one isn't on the server: the
/// strongest one the server offers, by its score on an agent benchmark (<see cref="AgentBenchmark"/>)
/// where known, else by size. Sizes and quantizations come from the server where it says (LM Studio
/// does, see <see cref="ServerProbe"/>), else from the names, which usually carry them
/// ("qwen3.8-27b@q6_k_xl").
/// </summary>
public static partial class ModelChoice
{
    /// <summary>
    /// <paramref name="picked"/> while the server offers it (a server that lists nothing is taken at
    /// its word), otherwise the strongest model it offers. Null when it offers none that can chat.
    /// </summary>
    public static string? Choose(string picked, IReadOnlyList<ModelInfo> offered) =>
        !string.IsNullOrEmpty(picked) && (offered.Count == 0 || offered.Any(m => m.Id == picked))
            ? picked
            : Strongest(offered)?.Id;

    /// <summary>
    /// The strongest model that can chat. One that takes images comes first (without them the agent
    /// can't see the screen; where the server doesn't say, the benchmark table may). Then the best
    /// score on <see cref="AgentBenchmark"/>, and a model without one after every model with one;
    /// then the most parameters, a mixture of experts counted as the dense model it is about as
    /// strong as (<see cref="DenseEquivalent"/>); then, between equal scores and sizes, a later
    /// version scored as the one it follows; then the most bits per weight; then one already
    /// loaded, which answers without loading; then the server's own order. A size or quantization
    /// nothing tells counts below every known one, and a file far too small for its size and
    /// quantization (<see cref="LooksIncomplete"/>) comes last.
    /// </summary>
    public static ModelInfo? Strongest(IEnumerable<ModelInfo> offered) =>
        offered.Where(m => m.Chat)
            .Select(m => (Model: m, Rating: AgentBenchmark.Rate(m)))
            .OrderBy(x => LooksIncomplete(x.Model))
            .ThenBy(x => SeesImages(x.Model, x.Rating) == false)
            .ThenByDescending(x => x.Rating?.Score ?? -1)
            .ThenByDescending(x => DenseEquivalent(x.Model) ?? -1)
            .ThenByDescending(x => x.Rating is { Exact: false })
            .ThenByDescending(x => x.Model.Bits ?? -1)
            .ThenByDescending(x => x.Model.Loaded)
            .Select(x => x.Model)
            .FirstOrDefault();

    /// <summary>Whether the model takes images: what the server says, else what the benchmark table says of it; null when neither says.</summary>
    private static bool? SeesImages(ModelInfo m, BenchmarkRating? rating) =>
        m.Vision ?? (rating is { Exact: true } own ? own.Basis.Vision : null);

    /// <summary>
    /// Billions of parameters of a dense model as strong as this one. A mixture of experts is about
    /// as strong as the geometric mean of its total and active parameters, a common rule of thumb:
    /// 30B-A3B is about 9.5B, 235B-A22B about 72B.
    /// </summary>
    internal static double? DenseEquivalent(ModelInfo m) =>
        m.Params is { } total && m.ActiveParams is { } active && active < total ? Math.Sqrt(total * active) : m.Params;

    /// <summary>
    /// Files under half of what the parameters take at that many bits can't be the whole model. The
    /// user's LM Studio lists such a file as "27B, 4-bit" at 2.3 GB, and it fails to load.
    /// </summary>
    internal static bool LooksIncomplete(ModelInfo m) =>
        m.SizeBytes is { } size && m.Params is { } total && m.Bits is { } bits && size < total * 1e9 * bits / 8 / 2;

    /// <summary>
    /// "27B, 6-bit, 79.8% on Terminal-Bench 2.1", "27B, 5-bit, scored as Qwen3.5 9B: 29.2% on
    /// Terminal-Bench 2.1", "30B-A3B, 4-bit", "27B"; null when nothing is known.
    /// </summary>
    public static string? Describe(ModelInfo m)
    {
        var parts = new List<string>();
        if (m.Params is { } total)
            parts.Add(m.ActiveParams is { } active ? $"{Count(total)}-A{Count(active)}" : Count(total));
        if (m.Bits is { } bits)
            parts.Add(bits.ToString("0.#", CultureInfo.InvariantCulture) + "-bit");
        if (AgentBenchmark.Rate(m) is { } rating)
        {
            var score = rating.Score.ToString("0.0", CultureInfo.InvariantCulture) + "% on " + AgentBenchmark.Name;
            parts.Add(rating.Exact ? score : $"scored as {rating.Basis.Model}: {score}");
        }
        return parts.Count > 0 ? string.Join(", ", parts) : null;

        static string Count(double billions) => billions >= 1
            ? billions.ToString("0.#", CultureInfo.InvariantCulture) + "B"
            : (billions * 1000).ToString("0", CultureInfo.InvariantCulture) + "M";
    }

    /// <summary>
    /// What a model's name tells: its size ("27b", "1.5B", "135m", "8x7b" for eight experts of 7B,
    /// "30b-a3b" for 30B with 3B active), its quantization ("q6_k_xl", "iq2_s", "f16", "4bit",
    /// "awq") and whether it chats at all ("embed", "rerank", "whisper"…).
    /// </summary>
    public static ModelInfo FromName(string id)
    {
        var (total, active) = ParseSize(id);
        return new ModelInfo(id) { Chat = !NotChat().IsMatch(id), Params = total, ActiveParams = active, Bits = ParseBits(id) };
    }

    /// <summary>
    /// Billions of parameters in a name, or in a size such as LM Studio's "30B-A3B". A name with
    /// several sizes in it ("…-7b-instruct-1m", the last being its context) has the largest.
    /// </summary>
    internal static (double? Total, double? Active) ParseSize(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return (null, null);
        double? total = null;
        foreach (Match m in SizeToken().Matches(text))
        {
            var n = Number(m.Groups["n"].Value) * (m.Groups["unit"].Value is "m" or "M" ? 0.001 : 1);
            if (m.Groups["experts"].Success)
                n *= Number(m.Groups["experts"].Value);
            total = Math.Max(total ?? 0, n);
        }
        var active = ActiveToken().Match(text);
        return (total, total is not null && active.Success ? Number(active.Groups["n"].Value) : null);
    }

    /// <summary>Bits per weight a quantization's name gives ("Q6_K_XL", "IQ2_S", "BF16", "8bit", "AWQ"); null when it gives none.</summary>
    internal static double? ParseBits(string? text)
    {
        if (string.IsNullOrEmpty(text) || QuantToken().Match(text) is not { Success: true } m)
            return null;
        return m.Groups["bits"].Success ? Number(m.Groups["bits"].Value) : 4; // AWQ and GPTQ are 4-bit
    }

    private static double Number(string text) => double.Parse(text, CultureInfo.InvariantCulture);

    // "27b", "1.5b", "135m", "8x7b"; not the 3 of "qwen3" or "a3b", nor "2026.27" in "2026.27b".
    [GeneratedRegex(@"(?<![a-z0-9])(?:(?<experts>\d{1,3})x)?(?<n>\d{1,3}\.\d{1,2}|\d{1,4})(?<unit>[bm])(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SizeToken();

    // The parameters a mixture of experts uses per token: "-a3b", "-A22B".
    [GeneratedRegex(@"(?<![a-z0-9])a(?<n>\d{1,3}(?:\.\d{1,2})?)b(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex ActiveToken();

    // GGUF types (q4_k_m, iq2_s, q8_0), floats (f16, bf16, fp8), MLX and other formats (4bit,
    // 8-bit, int4, w8a8, mxfp4); AWQ and GPTQ name no bits.
    [GeneratedRegex(@"(?<![a-z0-9])(?:i?q(?<bits>[1-8])|b?fp?(?<bits>8|16|32)|(?<bits>[2-8])-?bits?|int(?<bits>4|8)|w(?<bits>4|8)a\d+|(?:mx|nv)fp(?<bits>4|8)|awq|gptq)(?![a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantToken();

    // Embedding, reranking and speech models: they can't hold a chat.
    [GeneratedRegex(@"embed|rerank|whisper|dall-e|moderation|transcribe|(?<![a-z])tts(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex NotChat();
}
