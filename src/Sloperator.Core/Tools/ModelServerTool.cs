using System.Text.Json.Nodes;
using Sloperator.Core.Llm;
using Sloperator.Core.Settings;

namespace Sloperator.Core.Tools;

/// <summary>The app's side of <see cref="ModelServerTool"/>: the server, key and model in its top bar.</summary>
public interface IModelServerSwitch
{
    /// <summary>The server in use, as the top bar shows it.</summary>
    ServerSettings Current { get; }

    /// <summary>
    /// Puts them in the top bar, as typing them there would, and checks the server. The run going
    /// on keeps its model; the user's next message goes to the new one. An empty model is
    /// Sloperator's own pick, the strongest one the server offers.
    /// </summary>
    Task SwitchAsync(string baseUrl, string apiKey, string model);
}

/// <summary>
/// The agent connects Sloperator itself to another model: a local LM Studio, Claude, OpenRouter, or
/// back to Kilo's free ones. Asked to on Kilo's free Nemotron, it had nothing to do it with:
/// Sloperator's window is left out of its screenshots, so for five minutes it clicked through other
/// apps looking for a settings screen and switched nothing.
/// </summary>
/// <param name="http">Makes the client the server is checked with; a new one each time when null (tests give their own).</param>
public sealed class ModelServerTool(Func<HttpClient>? http = null) : Tool
{
    public override string Name => "model_server";

    public override string Description =>
        "The model server Sloperator itself runs on (you are that model): action=show says the server, model and the models it offers; " +
        "action=connect switches Sloperator to another server, API key and model, the way the user would in Sloperator's top bar " +
        "(which you can't see or click: Sloperator's window is left out of screenshots). Use it when the user asks to connect, switch to or use " +
        "another model or AI: LM Studio on this PC is http://localhost:1234/v1 (Ollama http://localhost:11434/v1), Claude is api.anthropic.com, " +
        "OpenRouter https://openrouter.ai/api/v1, Kilo's free models " + KiloGateway.Address + ". " +
        "The server is checked first and nothing changes if it doesn't answer or turns the key down. This task finishes on the model you are; " +
        "the user's next message goes to the new one.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "show", "connect"), true),
        ("address", Str("For connect: the server's address, such as http://localhost:1234/v1"), false),
        ("api_key", Str("For connect: the API key the user gave for that server; leave it out for a server without one (an address in use or used before keeps its key)"), false),
        ("model", Str("For connect: the model to use, as the server lists it; leave it out for the strongest one the server offers"), false));

    public override string Describe(JsonObject args) => (GetString(args, "action") ?? "") switch
    {
        // Never the key itself: the tool card shows this.
        "connect" => $"Connect Sloperator to {GetString(args, "address")}" + (GetString(args, "model") is { Length: > 0 } m ? $", model {m}" : "") +
                     (GetString(args, "api_key") is { Length: > 0 } ? ", with an API key" : ""),
        _ => "Show Sloperator's model server",
    };

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.ModelServer is not { } sw)
            return ToolResult.Error("Sloperator's model server can't be changed from this run: the user sets it in the top bar of Sloperator's window.");
        var current = sw.Current;
        switch (RequireString(args, "action").Trim().ToLowerInvariant())
        {
            case "show":
            {
                var probe = await ProbeAsync(new ServerSettings { BaseUrl = current.BaseUrl, ApiKey = current.ApiKey, Model = current.Model }, ct).ConfigureAwait(false);
                var text = $"Sloperator runs on {Named(current.ModelInUse)} at {current.BaseUrl}, " + (current.ApiKey.Length > 0 ? "with an API key." : "without an API key.");
                return new ToolResult(text + (probe.Reachable ? "\n" + ModelList(probe.Models) : $"\nThe server doesn't answer now: {probe.Message}"));
            }
            case "connect":
                return await ConnectAsync(args, sw, current, ct).ConfigureAwait(false);
            default:
                throw new ToolArgumentException("action must be show or connect.");
        }
    }

    private async Task<ToolResult> ConnectAsync(JsonObject args, IModelServerSwitch sw, ServerSettings current, CancellationToken ct)
    {
        var address = RequireString(args, "address").Trim();
        // The same address again keeps its key, so "use model X" needs no key from the user; so does
        // one in the top bar's saved servers ("switch back to Claude").
        var key = GetString(args, "api_key")?.Trim() is { Length: > 0 } given ? given
            : SameServer(address, current.BaseUrl) ? current.ApiKey
            : current.Saved.FirstOrDefault(s => SameServer(address, s.BaseUrl))?.ApiKey ?? "";
        var wanted = GetString(args, "model")?.Trim() ?? "";
        var probe = await ProbeAsync(new ServerSettings { BaseUrl = address, ApiKey = key }, ct).ConfigureAwait(false);
        if (probe.KeyRefused)
            return ToolResult.Error($"Nothing changed: {probe.Message} Ask the user for the right API key.");
        if (!probe.Reachable)
            return ToolResult.Error($"Nothing changed: {probe.Message}{AppHint(address)} Check the address with the user, and that the server runs.");
        if (probe.Models.Count == 0)
            return ToolResult.Error("Nothing changed: the server answers but offers no models to chat with. In LM Studio a model has to be downloaded first.");

        var model = "";
        if (wanted.Length > 0)
        {
            var match = probe.Models.FirstOrDefault(m => m.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            var like = probe.Models.Where(m => m.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match is null && like.Count != 1)
                return ToolResult.Error($"Nothing changed: the server has no model \"{wanted}\"" + (like.Count > 1 ? " but several like it" : "") + ".\n" +
                                        ModelList(like.Count > 1 ? like : probe.Models) + "\nConnect again with one of them, or without model for the strongest.");
            model = match ?? like[0];
        }
        var url = probe.BaseUrl ?? address;
        await sw.SwitchAsync(url, key, model).ConfigureAwait(false);

        var inUse = model.Length > 0 ? model : probe.Model ?? probe.Models[0];
        var how = model.Length > 0 ? "" : " (the strongest it offers; pass model to pick another)";
        var vision = probe.Vision == false && (model.Length == 0 || model == probe.Model)
            ? $" Note: the server says {inUse} doesn't take images, so on it Sloperator can't see or use the screen; tell the user, or connect a model that sees images." : "";
        return new ToolResult($"Sloperator is now connected to {url} and uses {inUse}{how}" + (key.Length > 0 ? ", with the API key." : ".") + vision +
                              " The top bar shows it. This task finishes on the model it started with; the user's next message goes to the new one, " +
                              "so tell the user it is done and that they can send their next request.");
    }

    private async Task<ProbeResult> ProbeAsync(ServerSettings server, CancellationToken ct)
    {
        using var client = http?.Invoke() ?? LlmHttp.Create(TimeSpan.FromSeconds(15));
        return await ServerProbe.CheckAsync(client, server, ct).ConfigureAwait(false);
    }

    private static bool SameServer(string a, string b) =>
        ServerAddress.Candidates(a).Intersect(ServerAddress.Candidates(b), StringComparer.OrdinalIgnoreCase).Any();

    /// <summary>Nothing on LM Studio's or Ollama's own port: its server is off more often than not.</summary>
    private static string AppHint(string address) =>
        ServerAddress.Candidates(address).FirstOrDefault() is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Port switch
            {
                1234 => " If it is LM Studio, its server has to be switched on (Developer tab, Status: Running)" +
                        (uri.IsLoopback ? "." : ", with Serve on Local Network on for other PCs to reach it."),
                11434 => " If it is Ollama, the Ollama app has to run.",
                _ => "",
            }
            : "";

    private static string Named(string model) => model.Length > 0 ? model : "no model yet";

    private static string ModelList(IReadOnlyList<string> models) =>
        models.Count == 0 ? "It offers no models." : $"Models it offers ({models.Count}): " + string.Join(", ", models.Take(60)) + (models.Count > 60 ? ", …" : "");
}
