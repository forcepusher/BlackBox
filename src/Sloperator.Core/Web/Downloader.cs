using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Sloperator.Core.Web;

/// <summary>A download that could not be done, said so the model can act on it (another link, a login it needs).</summary>
public sealed class DownloadException(string message) : Exception(message);

/// <summary>
/// Big files from the web: ComfyUI itself, its models, the 7-Zip that unpacks it. Each is written
/// to a .part file beside its place, picked up where it stopped after a dropped connection, a
/// stall or a Stop, checked against the SHA-256 its source gives, and only then given its name, so
/// a half file never looks like a whole one.
/// </summary>
public static class Downloader
{
    /// <summary>How long a download may receive nothing before it is taken up again where it stopped.</summary>
    internal static TimeSpan StallLimit { get; set; } = TimeSpan.FromSeconds(60);

    public static readonly HttpClient Http = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Sloperator/0.1 (+https://github.com/forcepusher/Sloperator)");
        return client;
    }

    /// <summary>What a link serves: the file's name (from the server, else the link) and size, without downloading it.</summary>
    public static async Task<(string Name, long? Size, Uri Final)> ProbeAsync(Uri url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new DownloadException(Refusal(url, response));
            var final = response.RequestMessage?.RequestUri ?? url;
            return (NameOf(response, url, final), response.Content.Headers.ContentLength, final);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DownloadException($"{url.Host} didn't answer within a minute.");
        }
        catch (HttpRequestException ex)
        {
            throw new DownloadException($"{url.Host} can't be reached: {ex.Message}");
        }
    }

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="target"/>. <paramref name="progress"/>
    /// hears how it goes about once a second ("Downloading x.safetensors… 812 of 2,034 MB, 6.1 MB/s,
    /// 3:20 left"). Returns the file's SHA-256.
    /// </summary>
    /// <exception cref="DownloadException">The server refused it, sent a web page instead, or it arrived damaged.</exception>
    public static async Task<string> DownloadAsync(Uri url, string target, string? sha256, Action<string>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        var part = target + ".part";
        var name = Path.GetFileName(target);
        for (var attempt = 1; ; attempt++)
        {
            var have = File.Exists(part) ? new FileInfo(part).Length : 0;
            try
            {
                if (await FetchAsync(url, part, have, name, progress, ct).ConfigureAwait(false))
                    break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException && !ct.IsCancellationRequested && attempt < 10)
            {
                progress?.Invoke($"The download of {name} was cut off ({ex.Message.Clip(80)}); going on where it stopped…");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(20, 2 * attempt)), ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new DownloadException($"The download of {name} from {url.Host} failed: {ex.Message}");
            }
        }

        progress?.Invoke($"Checking {name}…");
        string hash;
        await using (var stream = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (sha256 is { Length: > 0 } expected && !hash.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part);
            throw new DownloadException($"{name} arrived damaged: its SHA-256 is {hash}, not {expected} as its source says. It was deleted; download it again.");
        }
        File.Move(part, target, overwrite: true);
        return hash;
    }

    /// <summary>One go at the download, from byte <paramref name="have"/> on. False when the part file had to be started over.</summary>
    private static async Task<bool> FetchAsync(Uri url, string part, long have, string name, Action<string>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0)
            request.Headers.Range = new RangeHeaderValue(have, null);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(StallLimit * 2);
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connect.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{url.Host} didn't answer");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
            {
                // Asked for more than there is: the part file is complete, unless it is longer than the file.
                if (response.Content.Headers.ContentRange?.Length is { } length && length == have)
                    return true;
                File.Delete(part);
                return false;
            }
            if (!response.IsSuccessStatusCode)
                throw new DownloadException(Refusal(url, response));
            // A login wall or a model's page answers with a web page where the file was expected.
            if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
                throw new DownloadException($"{url} is a web page, not a file{(have > 0 ? "" : " (a login page, or the page about the file rather than its download link)")}. " +
                                            "Use the file's direct download link (on Hugging Face …/resolve/main/<file>, on Civitai https://civitai.com/api/download/models/<version id>).");
            var resumed = response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed)
                have = 0;
            long? total = response.Content.Headers.ContentLength is { } rest ? rest + have : null;

            await using var file = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[1 << 20];
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (lastReport, startHave) = (TimeSpan.Zero, have);
            while (true)
            {
                int read;
                using (var stall = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    stall.CancelAfter(StallLimit);
                    try
                    {
                        read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException($"nothing came for {StallLimit.TotalSeconds:0} s");
                    }
                }
                if (read == 0)
                    break;
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                have += read;
                if (clock.Elapsed - lastReport >= TimeSpan.FromSeconds(1))
                {
                    lastReport = clock.Elapsed;
                    progress?.Invoke(Progress(name, have, total, (have - startHave) / Math.Max(0.5, clock.Elapsed.TotalSeconds)));
                }
            }
            if (total is { } expected && have < expected)
                throw new IOException($"the connection closed at {Mb(have)} of {Mb(expected)}");
            return true;
        }
    }

    private static string Progress(string name, long have, long? total, double perSecond)
    {
        var speed = perSecond >= 1 << 20 ? $"{perSecond / 1048576:0.0} MB/s" : $"{perSecond / 1024:0} KB/s";
        if (total is not { } t || t <= 0)
            return $"Downloading {name}… {Mb(have)}, {speed}";
        var left = perSecond > 0 ? TimeSpan.FromSeconds((t - have) / perSecond) : TimeSpan.Zero;
        var done = t >= 1L << 30 ? $"{have / 1073741824.0:0.00} of {t / 1073741824.0:0.00} GB" : $"{have / 1048576.0:N0} of {t / 1048576.0:N0} MB";
        return $"Downloading {name}… {done}, {speed}, {Chats.ChatTimes.Stopwatch(left)} left";
    }

    private static string Mb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.00} GB" : $"{bytes / 1048576.0:N0} MB";

    /// <summary>Why the server said no, in words the model can act on.</summary>
    private static string Refusal(Uri url, HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var why = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when url.Host.Contains("civitai", StringComparison.OrdinalIgnoreCase) =>
                "the model's creator lets only signed-in Civitai users download it; pick another model, or ask the user for a Civitai API key",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden when url.Host.Contains("huggingface", StringComparison.OrdinalIgnoreCase) =>
                "it is a gated model: its page on Hugging Face asks to accept its license while signed in; pick another, or ask the user to",
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "it needs a login",
            HttpStatusCode.NotFound => "there is no such file there (check the link)",
            HttpStatusCode.TooManyRequests => "too many downloads from this PC just now; wait a minute and try again",
            _ => response.ReasonPhrase ?? "",
        };
        return $"{url.Host} answered {status} to {url}: {why}.";
    }

    /// <summary>
    /// The file's name: as the server gives it (Content-Disposition), else the last part of the link
    /// asked for when it names a file, else of the link it led to. Hugging Face's files come from a
    /// storage link named by their hash: v1-5-pruned-emaonly-fp16.safetensors was saved as
    /// "908c39bf…", without its extension, so ComfyUI didn't list it.
    /// </summary>
    internal static string NameOf(HttpResponseMessage response, Uri asked, Uri final)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var name = disposition?.FileNameStar ?? disposition?.FileName;
        // .NET refuses a header that ends in ";" as Hugging Face's does, and then has none.
        if (string.IsNullOrWhiteSpace(name) && response.Content.Headers.NonValidated.TryGetValues("Content-Disposition", out var raw))
        {
            var text = string.Join(";", raw);
            if (System.Text.RegularExpressions.Regex.Match(text, @"filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)") is { Success: true } star)
                name = Uri.UnescapeDataString(star.Groups[1].Value.Trim());
            else if (System.Text.RegularExpressions.Regex.Match(text, @"filename\s*=\s*""?([^"";]+)""?") is { Success: true } plain)
                name = plain.Groups[1].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            static string Last(Uri u) => Uri.UnescapeDataString(u.AbsolutePath.Split('/').LastOrDefault(s => s.Length > 0) ?? "");
            name = Path.HasExtension(Last(asked)) || !Path.HasExtension(Last(final)) && Last(asked).Length > 0 ? Last(asked) : Last(final);
        }
        name = name.Trim().Trim('"');
        if (name.Length == 0)
            name = "download";
        return string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    }
}
