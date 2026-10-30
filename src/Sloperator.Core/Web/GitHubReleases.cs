using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sloperator.Core.Web;

/// <summary>A repository's newest release, as <see cref="GitHubReleases.LatestAsync(string, string, CancellationToken)"/> found it.</summary>
/// <param name="Tag">The release's tag, as written ("1.2.0", "v0.3.9").</param>
/// <param name="Page">The release's page, with its notes.</param>
/// <param name="Asset">The file asked for, among the release's files; null when the release doesn't have it (yet).</param>
/// <param name="Sha256">The file's SHA-256, when GitHub says it.</param>
public sealed record GitHubRelease(string Tag, Uri Page, Uri? Asset, string? Sha256);

/// <summary>
/// The newest release of a GitHub repository and one of its files: ComfyUI's portable build, and
/// Sloperator's own exe for updating itself.
/// </summary>
public static partial class GitHubReleases
{
    /// <summary>
    /// Where the releases are looked up: GitHub, or the stand-in server named by
    /// SLOPERATOR_RELEASES_SERVER, which tests set to serve both GitHub's API and its pages.
    /// </summary>
    private static readonly (Uri Api, Uri Site) Servers = Environment.GetEnvironmentVariable("SLOPERATOR_RELEASES_SERVER") is { Length: > 0 } stand
        && Uri.TryCreate(stand.TrimEnd('/') + "/", UriKind.Absolute, out var standIn)
            ? (standIn, standIn)
            : (new Uri("https://api.github.com/"), new Uri("https://github.com/"));

    /// <summary>
    /// The newest release of <paramref name="repository"/> ("owner/name") with its link to
    /// <paramref name="asset"/> and the file's SHA-256 when GitHub says it: from GitHub's API, or,
    /// when that refuses (60 requests an hour without an account, often reached behind a VPN), from
    /// the release's own page. Null when neither answers.
    /// </summary>
    public static Task<GitHubRelease?> LatestAsync(string repository, string asset, CancellationToken ct) =>
        LatestAsync(Downloader.Http, Servers.Api, Servers.Site, repository, asset, ct);

    internal static async Task<GitHubRelease?> LatestAsync(HttpClient http, Uri api, Uri site, string repository, string asset, CancellationToken ct)
    {
        var releases = new Uri(site, repository + "/releases");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(api, $"repos/{repository}/releases/latest"));
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false)) is JsonObject release
                && (string?)release["tag_name"] is { Length: > 0 } tag)
            {
                var page = (string?)release["html_url"] is { } html && Uri.TryCreate(html, UriKind.Absolute, out var p) ? p : new Uri($"{releases}/tag/{tag}");
                foreach (var a in (release["assets"] as JsonArray ?? []).OfType<JsonObject>())
                    if ((string?)a["name"] == asset && (string?)a["browser_download_url"] is { } link)
                        return new GitHubRelease(tag, page, new Uri(link), Digest((string?)a["digest"]));
                return new GitHubRelease(tag, page, null, null);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException && !ct.IsCancellationRequested) { }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            // .../releases/latest leads to .../releases/tag/<tag>.
            using var latest = await http.GetAsync($"{releases}/latest", timeout.Token).ConfigureAwait(false);
            var tag = latest.RequestMessage?.RequestUri?.AbsolutePath.Split('/').LastOrDefault() ?? "";
            if (latest.IsSuccessStatusCode && tag.Length > 0 && tag != "latest")
            {
                var page = await http.GetStringAsync($"{releases}/expanded_assets/{tag}", timeout.Token).ConfigureAwait(false);
                var at = page.IndexOf("/download/" + tag + "/" + asset + "\"", StringComparison.Ordinal);
                var sha = at < 0 ? null : AssetDigest().Match(page, at) is { Success: true } m && m.Index - at < 4000 ? m.Groups[1].Value : null;
                return new GitHubRelease(tag, new Uri($"{releases}/tag/{tag}"), at < 0 ? null : new Uri($"{releases}/download/{tag}/{asset}"), sha);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested) { }
        return null;

        static string? Digest(string? digest) => digest is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? d[7..] : null;
    }

    /// <summary>A tag as a version: "1.2.0" and "v1.2.0" are 1.2.0, "1.2" is 1.2.0; null when it isn't one.</summary>
    public static Version? VersionOf(string tag)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        // A pre-release's or build's suffix ("1.3.0-beta.1") isn't part of the number.
        var cut = t.IndexOfAny(['-', '+']);
        if (cut >= 0)
            t = t[..cut];
        if (!t.Contains('.'))
            t += ".0";
        return Version.TryParse(t, out var v) ? Normal(v) : null;
    }

    /// <summary>Unset parts (1.2 has no build or revision) as 0, so 1.2 and 1.2.0.0 are the same version.</summary>
    public static Version Normal(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));

    [GeneratedRegex(@"sha256:([0-9a-f]{64})")]
    private static partial Regex AssetDigest();
}
