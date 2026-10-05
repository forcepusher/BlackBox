using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace BlackBox.Core.Web;

public sealed record SearchHit(string Title, string Url, string Snippet);

public sealed class WebSearchException(string message) : Exception(message);

/// <summary>
/// Web search without an API key. DuckDuckGo's HTML page (the one it serves browsers without
/// JavaScript) answers a plain HTTP request in a fraction of a second, but refuses a computer that
/// searched about ten times in a few minutes. Then, or when it finds nothing, Bing is searched in
/// the PC's own browser run headless (<see cref="HeadlessBrowser"/>), which takes a second or two.
/// Bing can't be asked directly: to a plain HTTP request it returns results for the first word of
/// the query only. Brave, Mojeek and Startpage answer one with a captcha.
/// </summary>
public static class WebSearch
{
    /// <summary>After DuckDuckGo refuses, it is left alone this long, so each search doesn't first wait for another refusal.</summary>
    private static readonly TimeSpan DuckDuckGoPause = TimeSpan.FromMinutes(10);
    private static DateTime _duckDuckGoRefusedAt = DateTime.MinValue;

    public static async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, HeadlessBrowser? browser, CancellationToken ct)
    {
        query = query.Trim();
        if (query.Length == 0)
            throw new WebSearchException("The search query is empty.");

        string? failure = null;
        if (DateTime.UtcNow - _duckDuckGoRefusedAt > DuckDuckGoPause)
        {
            try
            {
                var hits = await DuckDuckGoAsync(query, ct).ConfigureAwait(false);
                if (hits.Count > 0 || browser is null)
                    return Dedupe(hits).Take(maxResults).ToList();
            }
            catch (WebSearchException ex)
            {
                failure = ex.Message;
            }
        }
        else
            failure = "DuckDuckGo refused this computer's searches a few minutes ago (it limits how often one computer may search).";

        if (browser is null)
            throw new WebSearchException(failure + " No Edge or Chrome was found to search Bing with instead; wait a few minutes and try again.");
        try
        {
            var html = await browser.GetDomAsync(new Uri("https://www.bing.com/search?q=" + Uri.EscapeDataString(query)), runScripts: false, ct).ConfigureAwait(false);
            return Dedupe(ParseBing(html)).Take(maxResults).ToList();
        }
        catch (WebPageException ex)
        {
            throw new WebSearchException((failure is null ? "" : failure + " ") + "Bing: " + ex.Message);
        }
    }

    private static async Task<List<SearchHit>> DuckDuckGoAsync(string query, CancellationToken ct)
    {
        const string endpoint = "https://html.duckduckgo.com/html/";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new FormUrlEncodedContent([new("q", query), new("kl", "wt-wt")]),
            };
            request.Headers.Referrer = new Uri(endpoint);
            using var response = await WebHttp.Client.SendAsync(request, ct).ConfigureAwait(false);
            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            // A refusal comes as 202 or 403 with a captcha page instead of results.
            if (!response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted
                || html.Contains("anomaly-modal", StringComparison.Ordinal))
            {
                _duckDuckGoRefusedAt = DateTime.UtcNow;
                throw new WebSearchException($"DuckDuckGo refused the search (HTTP {(int)response.StatusCode}); it limits how often one computer may search.");
            }
            return ParseDuckDuckGo(html);
        }
        catch (HttpRequestException ex)
        {
            throw new WebSearchException($"Could not reach DuckDuckGo: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebSearchException("DuckDuckGo did not answer in time.");
        }
    }

    /// <summary>html.duckduckgo.com: one div.result per hit, ads marked result--ad.</summary>
    internal static List<SearchHit> ParseDuckDuckGo(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var hits = new List<SearchHit>();
        foreach (var result in doc.QuerySelectorAll("div.result"))
        {
            if (result.ClassList.Contains("result--ad"))
                continue;
            var link = result.QuerySelector("a.result__a");
            if (link is null || ResolveDuckDuckGoLink(link.GetAttribute("href")) is not { } url)
                continue;
            hits.Add(new(Clean(link.TextContent), url, Clean(result.QuerySelector(".result__snippet")?.TextContent)));
        }
        return hits;
    }

    /// <summary>
    /// The target of a DuckDuckGo result link: either the page itself or its redirect
    /// (//duckduckgo.com/l/?uddg=&lt;url&gt;). Ad links go through y.js and are dropped.
    /// </summary>
    internal static string? ResolveDuckDuckGoLink(string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
            return null;
        if (href.StartsWith("//", StringComparison.Ordinal))
            href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return null;
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
            return uri.AbsolutePath == "/l/" ? HttpUrl(System.Web.HttpUtility.ParseQueryString(uri.Query)["uddg"]) : null;
        return HttpUrl(uri.ToString());
    }

    /// <summary>Bing's page as the browser built it: one li.b_algo per hit.</summary>
    internal static List<SearchHit> ParseBing(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var hits = new List<SearchHit>();
        foreach (var result in doc.QuerySelectorAll("li.b_algo"))
        {
            var link = result.QuerySelector("h2 a");
            if (link is null || ResolveBingLink(link.GetAttribute("href")) is not { } url)
                continue;
            var snippet = result.QuerySelector("p[class*='b_lineclamp']") ?? result.QuerySelector(".b_caption p") ?? result.QuerySelector(".b_caption");
            hits.Add(new(Clean(link.TextContent), url, Clean(snippet?.TextContent)));
        }
        return hits;
    }

    /// <summary>
    /// The target of a Bing result link: the page itself, or Bing's click tracker
    /// (bing.com/ck/a?...&amp;u=a1&lt;base64url of the address&gt;).
    /// </summary>
    internal static string? ResolveBingLink(string? href)
    {
        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri))
            return null;
        if (!uri.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase))
            return HttpUrl(uri.ToString());
        var u = System.Web.HttpUtility.ParseQueryString(uri.Query)["u"];
        if (u is not { Length: > 2 } || !u.StartsWith("a1", StringComparison.Ordinal))
            return null;
        var b64 = u[2..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
        try
        {
            return HttpUrl(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string? HttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.ToString() : null;

    private static IEnumerable<SearchHit> Dedupe(IEnumerable<SearchHit> hits)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hit in hits)
            if (seen.Add(hit.Url.TrimEnd('/')))
                yield return hit;
    }

    private static string Clean(string? text) =>
        string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
