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
            return Valid(OpenAiChatClient.NormalizeBaseUrl(text));

        text = text.TrimStart('/');
        var http = OpenAiChatClient.NormalizeBaseUrl("http://" + text);
        var https = OpenAiChatClient.NormalizeBaseUrl("https://" + text);
        return IsLocal(text) ? Valid(http, https) : Valid(https, http);
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
