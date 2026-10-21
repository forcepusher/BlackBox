using System.Text.RegularExpressions;

namespace BlackBox.Core.Llm;

/// <summary>
/// Turns whatever the user pastes as the server address into the base URLs worth trying.
/// </summary>
/// <remarks>
/// "host:1234", "http://host:1234", "host:1234/v1/", ".../v1/chat/completions" and ".../v1/models"
/// all mean the same server. Without a scheme, both https and http are candidates; the probe tries
/// them and keeps the one that answers. Local addresses list http first, public ones https first.
/// </remarks>
public static partial class ServerAddress
{
    /// <summary>The base URLs to try, best first; none when the text can't be a server's address (half-typed "http://").</summary>
    public static IReadOnlyList<string> Candidates(string input)
    {
        var text = input.Trim();
        if (text.Length == 0)
            return [];
        if (Scheme().IsMatch(text))
            return WithApi(Valid(OpenAiChatClient.NormalizeBaseUrl(text)));

        text = text.TrimStart('/');
        var http = OpenAiChatClient.NormalizeBaseUrl("http://" + text);
        var https = OpenAiChatClient.NormalizeBaseUrl("https://" + text);
        return WithApi(IsLocal(text) ? Valid(http, https) : Valid(https, http));
    }

    /// <summary>
    /// The API addresses of services whose website people type instead: "openrouter.ai" asked for
    /// https://openrouter.ai/v1/models, got the site's 404 page, and the banner showed its HTML.
    /// </summary>
    private static readonly Dictionary<string, string> ApiOfWebsite = new(StringComparer.OrdinalIgnoreCase)
    {
        ["openrouter.ai"] = "https://openrouter.ai/api/v1",
        ["kilo.ai"] = KiloGateway.Address, ["app.kilo.ai"] = KiloGateway.Address, ["kilocode.ai"] = KiloGateway.Address,
        ["openai.com"] = "https://api.openai.com/v1", ["platform.openai.com"] = "https://api.openai.com/v1",
        ["groq.com"] = "https://api.groq.com/openai/v1", ["console.groq.com"] = "https://api.groq.com/openai/v1",
        ["together.ai"] = "https://api.together.xyz/v1", ["together.xyz"] = "https://api.together.xyz/v1",
        ["mistral.ai"] = "https://api.mistral.ai/v1", ["console.mistral.ai"] = "https://api.mistral.ai/v1",
        ["deepseek.com"] = "https://api.deepseek.com/v1", ["platform.deepseek.com"] = "https://api.deepseek.com/v1",
        ["x.ai"] = "https://api.x.ai/v1", ["console.x.ai"] = "https://api.x.ai/v1",
        ["fireworks.ai"] = "https://api.fireworks.ai/inference/v1",
        ["anthropic.com"] = "https://api.anthropic.com/v1", ["console.anthropic.com"] = "https://api.anthropic.com/v1",
        ["claude.com"] = "https://api.anthropic.com/v1", ["platform.claude.com"] = "https://api.anthropic.com/v1", ["claude.ai"] = "https://api.anthropic.com/v1",
    };

    /// <summary>A service's website (any page of it) gets its API address tried first; the address as typed stays a candidate.</summary>
    private static string[] WithApi(string[] urls)
    {
        if (urls.Length == 0 || !Uri.TryCreate(urls[0], UriKind.Absolute, out var uri))
            return urls;
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return ApiOfWebsite.TryGetValue(host, out var api) && !urls.Contains(api, StringComparer.OrdinalIgnoreCase) ? [api, .. urls] : urls;
    }

    /// <summary>An API key rather than an address ("sk-or-v1-…", "gsk_…", "AIza…"): long, and without a dot, colon or slash.</summary>
    public static bool LooksLikeKey(string input)
    {
        var text = input.Trim();
        return text.Length >= 24 && !text.Any(c => c is '.' or ':' or '/' or '\\' || char.IsWhiteSpace(c)) && text.Any(char.IsDigit) && text.Any(char.IsLetter);
    }

    private static string[] Valid(params string[] urls) =>
        urls.Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0).ToArray();

    private static bool IsLocal(string address)
    {
        var host = address.Split('/', 2)[0].Split(':', 2)[0].Trim('[', ']').ToLowerInvariant();
        return host is "localhost" or "127.0.0.1" or "::1" || !host.Contains('.') || host.EndsWith(".local")
            || host.StartsWith("10.") || host.StartsWith("192.168.") || Regex.IsMatch(host, @"^172\.(1[6-9]|2\d|3[01])\.");
    }

    [GeneratedRegex(@"^[a-z][a-z0-9+.-]*://", RegexOptions.IgnoreCase)]
    private static partial Regex Scheme();
}
