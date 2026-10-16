using System.Globalization;
using System.Text.RegularExpressions;

namespace BlackBox.Core.Llm;

/// <summary>A model's score on <see cref="AgentBenchmark"/>.</summary>
/// <param name="Score">Percent of the benchmark's tasks solved.</param>
/// <param name="Basis">The listed model the score is from: this one, or one it is a later version or a larger size of.</param>
/// <param name="Exact">The score is this model's own, not that of an earlier or smaller one.</param>
internal sealed record BenchmarkRating(double Score, AgentBenchmark.Entry Basis, bool Exact);

/// <summary>
/// How well open models do as agents, so the auto-pick goes by that rather than by size alone,
/// which took the user's Gemma 4 31B over Qwen3.8 27B: on this benchmark Qwen3.8 27B solves 79.8%
/// of the tasks, Gemma 4 31B 43.4%.
/// </summary>
/// <remarks>
/// The scores are Terminal-Bench 2.1 (89 tasks done in a terminal: building, fixing, setting up,
/// finding out) as Artificial Analysis ran it, every model the same way: the Terminus 2 agent,
/// three runs each, reasoning on as the model ships. Read on 2026-10-16 from
/// https://artificialanalysis.ai/evaluations/terminalbench-2-1; only its own runs, no estimates.
/// The vendors' numbers can't rank one family against another: Google publishes no Terminal-Bench
/// score for Gemma, Qwen's cards use different versions of it, and the harness alone moved Qwen3.6
/// 27B from 44.9 (vals.ai) to 59.3 (its card) on the same version.
/// Listed are the open models that solve at least a fifth of the tasks; most others solve under a
/// tenth (Llama 3.3 70B 4.9%, Gemma 3 27B 4.5%), so a model not listed is ranked by its size, after
/// the listed ones. Quantized and QAT builds count as the model they are built from.
/// </remarks>
internal static partial class AgentBenchmark
{
    public const string Name = "Terminal-Bench 2.1";

    /// <param name="Family">As <see cref="Lineage"/> names it.</param>
    /// <param name="Version">The family's version: 3.8 for Qwen3.8, 4 for Gemma 4; 0 for a family without one.</param>
    /// <param name="Params">Billions of parameters, all of them for a mixture of experts; null for a version that comes in one size.</param>
    /// <param name="ActiveParams">For a mixture of experts, the billions it uses per token.</param>
    /// <param name="Vision">Takes images.</param>
    internal sealed record Entry(string Model, string Family, double Version, double? Params, double? ActiveParams, double Score, bool Vision);

    internal static readonly Entry[] Table =
    [
        //  Model                       Family            Version  Params  Active  Score  Vision
        new("Qwen3.8 27B",              "qwen",           3.8,     27,     null,   79.8,  true),
        new("Qwen3.6 27B",              "qwen",           3.6,     27,     null,   60.7,  true),
        new("Qwen3.6 35B A3B",          "qwen",           3.6,     35,     3,      44.9,  true),
        new("Qwen3.5 397B A17B",        "qwen",           3.5,     397,    17,     51.3,  true),
        new("Qwen3.5 122B A10B",        "qwen",           3.5,     122,    10,     47.6,  true),
        new("Qwen3.5 9B",               "qwen",           3.5,     9,      null,   29.2,  true),
        new("Qwen3.5 4B",               "qwen",           3.5,     4,      null,   25.8,  true),
        new("Gemma 4 31B",              "gemma",          4,       31,     null,   43.4,  true),
        new("Gemma 4 26B A4B",          "gemma",          4,       26,     4,      39.0,  true),
        new("Gemma 4 12B",              "gemma",          4,       12,     null,   27.3,  true),
        new("gpt-oss-120b",             "gpt-oss",        0,       120,    null,   26.2,  false),
        new("Mistral Small 4",          "mistral-small",  4,       null,   null,   21.0,  true),
        new("Devstral Small 2",         "devstral-small", 2,       null,   null,   29.6,  true),
        new("DeepSeek V3.2",            "deepseek",       3.2,     null,   null,   46.8,  false),
        new("MiniMax-M2.7",             "minimax",        2.7,     null,   null,   55.4,  false),
        new("MiniMax-M3",               "minimax",        3,       null,   null,   65.2,  true),
        new("Kimi K2.5",                "kimi",           2.5,     null,   null,   45.7,  true),
        new("Kimi K2.6",                "kimi",           2.6,     null,   null,   65.9,  true),
    ];

    /// <summary>
    /// The model's score: its own, or for a later version or a larger size of a listed model (a
    /// Qwen3.9 27B, a Qwen3.5 27B), the best of those it follows, as it is at least as strong.
    /// Null for a model the table can't say anything about: another family, an earlier version, a
    /// fine-tune under its own name, or a size the name and the server don't tell.
    /// </summary>
    internal static BenchmarkRating? Rate(ModelInfo m)
    {
        if (Lineage(m.Id) is not (var family, var version))
            return null;
        var kin = Table.Where(e => e.Family == family).ToList();
        if (kin.FirstOrDefault(e => e.Version == version && SameSize(e, m)) is { } own)
            return new BenchmarkRating(own.Score, own, Exact: true);
        var size = ModelChoice.DenseEquivalent(m);
        var basis = kin.Where(e => e.Version <= version && (e.Params is null || size >= Dense(e) * 0.9)).MaxBy(e => e.Score);
        return basis is null ? null : new BenchmarkRating(basis.Score, basis, Exact: false);
    }

    /// <summary>The family and version a model's name gives ("qwen", 3.8 for "qwen3.8-27b@q6_k_xl"), or null for a family not in the table.</summary>
    internal static (string Family, double Version)? Lineage(string id)
    {
        var name = id.Replace('_', '-').Replace(' ', '-');
        foreach (var (family, pattern) in Families)
            if (pattern.Match(name) is { Success: true } m)
                return (family, m.Groups["v"].Success ? double.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture) : 0);
        return null;
    }

    /// <summary>Within a tenth of the listed size, a mixture of experts with the same active size.</summary>
    private static bool SameSize(Entry e, ModelInfo m) =>
        e.Params is not { } total
        || m.Params is { } p && Near(p, total)
           && (e.ActiveParams is { } active ? m.ActiveParams is { } a && Near(a, active) : m.ActiveParams is null);

    private static bool Near(double value, double listed) => Math.Abs(value - listed) <= 0.1 * listed;

    private static double? Dense(Entry e) => ModelChoice.DenseEquivalent(new ModelInfo(e.Model) { Params = e.Params, ActiveParams = e.ActiveParams });

    // A one-digit version, so a date such as Mistral's "2506" isn't one: "qwen3.8", "gemma-4",
    // "gemma3:27b", "mistral-small-3.2", "deepseek-v3.2", "kimi-k2.6", "minimax-m3". A distill
    // named after its teacher ("deepseek-r1-distill-qwen-32b") names no Qwen version.
    private static readonly (string Family, Regex Pattern)[] Families =
    [
        ("qwen", Pattern(@"qwen(?<v>\d(?:\.\d)?)")),
        ("gemma", Pattern(@"gemma-?(?<v>\d(?:\.\d)?)")),
        ("gpt-oss", Pattern("gpt-oss")),
        ("mistral-small", Pattern(@"mistral-small-(?<v>\d(?:\.\d)?)")),
        ("devstral-small", Pattern(@"devstral-small-(?<v>\d(?:\.\d)?)")),
        ("deepseek", Pattern(@"deepseek-v(?<v>\d(?:\.\d)?)")),
        ("minimax", Pattern(@"minimax-m(?<v>\d(?:\.\d)?)")),
        ("kimi", Pattern(@"kimi-k(?<v>\d(?:\.\d)?)")),
    ];

    private static Regex Pattern(string family) =>
        new($@"(?<![a-z]){family}(?![\d.]|[a-z])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
