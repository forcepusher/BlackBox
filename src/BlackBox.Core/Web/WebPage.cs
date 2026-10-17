using System.Net;
using System.Text;
using AngleSharp;

namespace BlackBox.Core.Web;

public sealed record WebPageText(string Url, string Title, string Text);

public sealed class WebPageException(string message) : Exception(message);

/// <summary>
/// Downloads a page and reads its text (see <see cref="PageText"/>). A page that turns away plain
/// HTTP requests, or that is an empty shell its scripts fill in, is loaded in the headless browser
/// instead when there is one. Keeps the last few pages, so reading on in a long page doesn't
/// download it again.
/// </summary>
public static class WebPage
{
    private const int CacheSize = 8;
    /// <summary>Less text than this from an HTML page means its content is built by scripts (or it is an error page).</summary>
    private const int ScriptShellChars = 300;

    private static readonly LinkedList<(WebPageText Page, DateTime At)> Cache = new();
    private static readonly Lock CacheLock = new();

    /// <param name="maxAge">
    /// How old a kept copy may be to be used: long enough to read on in a page in parts, short
    /// for a new look. Kept for ever, a page fetched again after a change (a local server restarted
    /// with new code, a status page watched) came back as it had been the first time.
    /// </param>
    public static async Task<WebPageText> ReadAsync(string url, HeadlessBrowser? browser, CancellationToken ct, TimeSpan? maxAge = null)
    {
        var uri = ParseAddress(url);
        var key = uri.GetLeftPart(UriPartial.Query);
        var oldest = DateTime.UtcNow - (maxAge ?? TimeSpan.FromMinutes(1));
        lock (CacheLock)
        {
            if (Cache.FirstOrDefault(p => p.Page.Url == key) is { Page: not null } cached)
            {
                Cache.Remove(cached);
                if (cached.At >= oldest)
                {
                    Cache.AddFirst(cached);
                    return cached.Page;
                }
            }
        }

        var page = await DownloadAsync(uri, browser, ct).ConfigureAwait(false) with { Url = key };
        // A page with no text (blocked, not loaded yet) is fetched again next time, not kept.
        if (page.Text.Length > 0)
            lock (CacheLock)
            {
                Cache.AddFirst((page, DateTime.UtcNow));
                while (Cache.Count > CacheSize)
                    Cache.RemoveLast();
            }
        return page;
    }

    internal static Uri ParseAddress(string url)
    {
        url = url.Trim().Trim('<', '>', '"', '\'');
        // "docs.unity3d.com/..." without a scheme.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is "file" && !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate("https://" + url, UriKind.Absolute, out uri) || !uri.Host.Contains('.'))
                throw new WebPageException($"Not a web address: {url}");
        }
        if (uri.Scheme is not ("http" or "https"))
            throw new WebPageException($"Only http and https pages can be read, not {uri.Scheme}. Use read_file for files on this PC.");
        return uri;
    }

    private static async Task<WebPageText> DownloadAsync(Uri uri, HeadlessBrowser? browser, CancellationToken ct)
    {
        (WebPageText Text, bool IsHtml) page;
        try
        {
            page = await HttpAsync(uri, ct).ConfigureAwait(false);
        }
        catch (RefusedException ex)
        {
            if (browser is null)
                throw new WebPageException(ex.Message);
            return await BrowserAsync(uri, browser, ct).ConfigureAwait(false) is { Text.Length: >= ScriptShellChars } loaded && !IsBlockPage(loaded)
                ? loaded
                : throw new WebPageException(ex.Message);
        }
        if (page.IsHtml && page.Text.Text.Length < ScriptShellChars && browser is not null)
        {
            try
            {
                if (await BrowserAsync(uri, browser, ct).ConfigureAwait(false) is { } loaded && loaded.Text.Length > page.Text.Text.Length)
                    return loaded;
            }
            catch (WebPageException) { }
        }
        return page.Text;
    }

    /// <summary>A short "access denied" or bot-check page instead of the content.</summary>
    private static bool IsBlockPage(WebPageText page) =>
        page.Text.Length < 2_000 && System.Text.RegularExpressions.Regex.IsMatch(page.Title + " " + page.Text[..Math.Min(400, page.Text.Length)],
            @"forbidden|access denied|blocked|just a moment|attention required|captcha|are you a robot|verify you are human|too many requests",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The site answered, but not with the page: it turns away automated readers or wants a login.</summary>
    private sealed class RefusedException(string message) : Exception(message);

    private static async Task<(WebPageText Text, bool IsHtml)> HttpAsync(Uri uri, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await WebHttp.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new WebPageException($"Could not reach {uri.Host}: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new WebPageException($"{uri.Host} did not answer in time.");
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests
                or HttpStatusCode.ServiceUnavailable)
                throw new RefusedException($"HTTP {(int)response.StatusCode}: {uri.Host} turns away automated readers or needs a login. Try another search result.");
            if (!response.IsSuccessStatusCode)
                throw new WebPageException(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone
                    ? $"HTTP {(int)response.StatusCode}: there is no such page (any more)."
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");

            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            byte[] body;
            try
            {
                body = await WebHttp.ReadBodyAsync(response, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new WebPageException($"{uri.Host} sent the page too slowly; it didn't arrive in {WebHttp.Client.Timeout.TotalSeconds:0} seconds.");
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException)
            {
                throw new WebPageException($"The connection to {uri.Host} broke while the page came in: {ex.Message}");
            }

            if (mediaType is "application/pdf" || body.AsSpan().StartsWith("%PDF"u8))
            {
                try
                {
                    return (new(finalUri.ToString(), Uri.UnescapeDataString(finalUri.Segments[^1]), Tools.DocumentText.Pdf(new MemoryStream(body))), false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    throw new WebPageException($"This PDF could not be read ({ex.Message}). Try another result, or download it with run_shell and look at it on screen.");
                }
            }

            if (mediaType.Length > 0 && !mediaType.Contains("html") && !mediaType.Contains("xml"))
            {
                if (!mediaType.StartsWith("text/") && !mediaType.Contains("json") && !mediaType.Contains("javascript"))
                    throw new WebPageException($"This is not a web page but a {mediaType} file. Download it with run_shell if you need it.");
                // Plain text, Markdown, JSON, source code: as it is.
                return (new(finalUri.ToString(), finalUri.Segments[^1], Decode(body, response.Content.Headers.ContentType?.CharSet)), false);
            }

            var contentType = response.Content.Headers.ContentType?.ToString() ?? "text/html";
            return (await ParseAsync(finalUri, r => r.Content(new MemoryStream(body)).Header("Content-Type", contentType), ct).ConfigureAwait(false), true);
        }
    }

    private static async Task<WebPageText> BrowserAsync(Uri uri, HeadlessBrowser browser, CancellationToken ct)
    {
        var html = await browser.GetDomAsync(uri, runScripts: true, ct).ConfigureAwait(false);
        return await ParseAsync(uri, r => r.Content(html), ct).ConfigureAwait(false);
    }

    private static async Task<WebPageText> ParseAsync(Uri address, Action<AngleSharp.Io.VirtualResponse> response, CancellationToken ct)
    {
        var context = BrowsingContext.New(Configuration.Default);
        var (title, text) = await ExtractAsync(keepNoscript: false).ConfigureAwait(false);
        if (text.Length < ScriptShellChars)
        {
            // Extracting takes the document apart, so the second reading parses the page again.
            var withNoscript = await ExtractAsync(keepNoscript: true).ConfigureAwait(false);
            if (withNoscript.Text.Length > text.Length * 2)
                text = withNoscript.Text;
        }
        return new(address.ToString(), title, text);

        async Task<(string Title, string Text)> ExtractAsync(bool keepNoscript)
        {
            using var document = await context.OpenAsync(r => response(r.Address(address)), ct).ConfigureAwait(false);
            return PageText.Extract(document, keepNoscript);
        }
    }

    private static string Decode(byte[] body, string? charset)
    {
        try
        {
            if (!string.IsNullOrEmpty(charset))
                return Encoding.GetEncoding(charset.Trim('"')).GetString(body);
        }
        catch (ArgumentException) { }
        return Encoding.UTF8.GetString(body);
    }
}
