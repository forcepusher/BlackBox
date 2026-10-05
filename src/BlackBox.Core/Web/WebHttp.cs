using System.Net;

namespace BlackBox.Core.Web;

/// <summary>
/// The one HTTP client the web tools share. It presents itself as a current desktop browser, since
/// search engines and many sites answer anything else with an error or an empty page. Cookies live
/// in memory only and go away with the process.
/// </summary>
internal static class WebHttp
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    /// <summary>Pages larger than this are cut off. Some documentation pages carry megabytes of script data (docs.unity.com: 7.5 MB).</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    public static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        return client;
    }

    /// <summary>Reads at most <see cref="MaxBytes"/> of a response body.</summary>
    public static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < MaxBytes && (read = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            buffer.Write(chunk, 0, read);
        return buffer.ToArray();
    }
}
