using System.Net;
using System.Text;
using Sloperator.Core.Web;
using Xunit;

namespace Sloperator.Core.Tests;

/// <summary>Sloperator updating itself: the newest release found on GitHub, and the new exe put in place of the running one.</summary>
public class SelfUpdateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("bb-update-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private static readonly Uri ApiBase = new("https://api.example.test/"), SiteBase = new("https://site.example.test/");

    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("1.2.0", "1.2.0.0")]
    [InlineData("v1.2.0", "1.2.0.0")]
    [InlineData("V2", "2.0.0.0")]
    [InlineData("1.3", "1.3.0.0")]
    [InlineData("1.3.0-beta.1", "1.3.0.0")]
    [InlineData("1.3.0+build.7", "1.3.0.0")]
    public void A_tag_reads_as_its_version(string tag, string version) =>
        Assert.Equal(Version.Parse(version), GitHubReleases.VersionOf(tag));

    [Theory]
    [InlineData("latest")]
    [InlineData("")]
    [InlineData("release-one")]
    public void A_tag_that_is_no_version_is_none(string tag) => Assert.Null(GitHubReleases.VersionOf(tag));

    [Theory]
    [InlineData("1.2.0", "1.1.0.0", true)]
    [InlineData("v1.1.1", "1.1.0", true)]
    [InlineData("1.1.0", "1.1.0.0", false)]
    [InlineData("1.1", "1.1.0.0", false)]
    [InlineData("1.0.9", "1.1.0.0", false)]
    [InlineData("2.0.0", "10.0.0.0", false)] // compared as numbers, not as text
    [InlineData("latest", "1.1.0.0", false)]
    public void Only_a_later_version_is_newer(string tag, string running, bool newer) =>
        Assert.Equal(newer, SelfUpdate.Newer(new GitHubRelease(tag, SiteBase, new Uri(SiteBase, "x.exe"), null), Version.Parse(running)));

    [Fact]
    public void A_newer_release_without_the_exe_yet_is_no_update()
    {
        var release = new GitHubRelease("9.0.0", SiteBase, null, null);
        Assert.True(SelfUpdate.IsNewer(release, new Version(1, 1, 0)));
        Assert.False(SelfUpdate.Newer(release, new Version(1, 1, 0)));
    }

    [Fact]
    public async Task The_api_gives_the_tag_page_link_and_sha256()
    {
        var github = new FakeGitHub
        {
            Api = """
                  {"tag_name":"1.2.0","html_url":"https://github.com/forcepusher/Sloperator/releases/tag/1.2.0",
                   "assets":[{"name":"notes.txt","browser_download_url":"https://x.test/notes.txt"},
                             {"name":"Sloperator.exe","browser_download_url":"https://github.com/forcepusher/Sloperator/releases/download/1.2.0/Sloperator.exe",
                              "digest":"sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}]}
                  """,
        };
        var release = await GitHubReleases.LatestAsync(new HttpClient(github), ApiBase, SiteBase, "forcepusher/Sloperator", "Sloperator.exe", TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release.Tag);
        Assert.Equal("https://github.com/forcepusher/Sloperator/releases/tag/1.2.0", release.Page.AbsoluteUri);
        Assert.Equal("https://github.com/forcepusher/Sloperator/releases/download/1.2.0/Sloperator.exe", release.Asset?.AbsoluteUri);
        Assert.Equal(Sha, release.Sha256);
        Assert.Equal(["https://api.example.test/repos/forcepusher/Sloperator/releases/latest"], github.Asked);
    }

    [Fact]
    public async Task A_release_the_api_lists_without_the_exe_has_no_asset_and_the_pages_are_not_asked()
    {
        var github = new FakeGitHub { Api = """{"tag_name":"v1.3.0","assets":[{"name":"Sloperator.zip","browser_download_url":"https://x.test/s.zip"}]}""" };
        var release = await GitHubReleases.LatestAsync(new HttpClient(github), ApiBase, SiteBase, "forcepusher/Sloperator", "Sloperator.exe", TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal("v1.3.0", release.Tag);
        Assert.Null(release.Asset);
        // No html_url: the release's page is made from its tag.
        Assert.Equal("https://site.example.test/forcepusher/Sloperator/releases/tag/v1.3.0", release.Page.AbsoluteUri);
        Assert.Single(github.Asked);
    }

    [Fact]
    public async Task Past_the_api_limit_the_release_page_gives_the_tag_link_and_sha256()
    {
        var github = new FakeGitHub
        {
            ApiStatus = HttpStatusCode.Forbidden, // "API rate limit exceeded"
            LatestTag = "1.2.0",
            Assets = $"""<li><a href="/forcepusher/Sloperator/releases/download/1.2.0/Sloperator.exe" rel="nofollow">Sloperator.exe</a> <span>sha256:{Sha}</span></li>""",
        };
        var release = await GitHubReleases.LatestAsync(new HttpClient(github), ApiBase, SiteBase, "forcepusher/Sloperator", "Sloperator.exe", TestContext.Current.CancellationToken);

        Assert.NotNull(release);
        Assert.Equal("1.2.0", release.Tag);
        Assert.Equal("https://site.example.test/forcepusher/Sloperator/releases/download/1.2.0/Sloperator.exe", release.Asset?.AbsoluteUri);
        Assert.Equal("https://site.example.test/forcepusher/Sloperator/releases/tag/1.2.0", release.Page.AbsoluteUri);
        Assert.Equal(Sha, release.Sha256);
        Assert.Equal(["https://api.example.test/repos/forcepusher/Sloperator/releases/latest",
            "https://site.example.test/forcepusher/Sloperator/releases/latest",
            "https://site.example.test/forcepusher/Sloperator/releases/tag/1.2.0",
            "https://site.example.test/forcepusher/Sloperator/releases/expanded_assets/1.2.0"], github.Asked);
    }

    [Fact]
    public async Task A_release_page_without_the_exe_has_no_asset()
    {
        var github = new FakeGitHub { ApiStatus = HttpStatusCode.Forbidden, LatestTag = "1.2.0", Assets = "<li>Source code (zip)</li>" };
        var release = await GitHubReleases.LatestAsync(new HttpClient(github), ApiBase, SiteBase, "forcepusher/Sloperator", "Sloperator.exe", TestContext.Current.CancellationToken);

        Assert.Equal("1.2.0", release?.Tag);
        Assert.Null(release?.Asset);
    }

    [Fact]
    public async Task Neither_answering_is_no_release()
    {
        var github = new FakeGitHub { ApiStatus = HttpStatusCode.Forbidden };
        Assert.Null(await GitHubReleases.LatestAsync(new HttpClient(github), ApiBase, SiteBase, "forcepusher/Sloperator", "Sloperator.exe", TestContext.Current.CancellationToken));
    }

    [Fact]
    public void The_new_exe_goes_in_place_and_the_old_one_aside()
    {
        var exe = Write("Sloperator (1).exe", "old");
        Write("Sloperator (1).exe.new", "new");

        Assert.Null(SelfUpdate.Swap(exe));

        Assert.Equal("new", File.ReadAllText(exe));
        Assert.Equal("old", File.ReadAllText(SelfUpdate.OldFile(exe)));
        Assert.False(File.Exists(SelfUpdate.NewFile(exe)));
    }

    [Fact]
    public void The_swap_puts_the_old_exe_back_when_the_new_one_cannot_take_its_place()
    {
        var exe = Write("Sloperator.exe", "old");
        var fresh = Write("Sloperator.exe.new", "new");
        string? failed;
        // Held open without sharing deletes, the new exe can't be renamed.
        using (new FileStream(fresh, FileMode.Open, FileAccess.Read, FileShare.Read))
            failed = SelfUpdate.Swap(exe);

        Assert.NotNull(failed);
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Equal("new", File.ReadAllText(fresh));
        Assert.False(File.Exists(SelfUpdate.OldFile(exe)));
    }

    [Fact]
    public void A_swap_with_nothing_downloaded_changes_nothing()
    {
        var exe = Write("Sloperator.exe", "old");
        Assert.NotNull(SelfUpdate.Swap(exe));
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.False(File.Exists(SelfUpdate.OldFile(exe)));
    }

    [Fact]
    public void A_swap_replaces_an_old_exe_a_start_could_not_delete()
    {
        var exe = Write("Sloperator.exe", "1.1");
        Write("Sloperator.exe.old", "1.0");
        Write("Sloperator.exe.new", "1.2");

        Assert.Null(SelfUpdate.Swap(exe));
        Assert.Equal("1.2", File.ReadAllText(exe));
        Assert.Equal("1.1", File.ReadAllText(SelfUpdate.OldFile(exe)));
    }

    [Fact]
    public void Unswap_takes_the_new_exe_back_out_for_later()
    {
        var exe = Write("Sloperator.exe", "old");
        Write("Sloperator.exe.new", "new");
        Assert.Null(SelfUpdate.Swap(exe));

        Assert.True(SelfUpdate.Unswap(exe));
        Assert.Equal("old", File.ReadAllText(exe));
        Assert.Equal("new", File.ReadAllText(SelfUpdate.NewFile(exe)));
        Assert.False(File.Exists(SelfUpdate.OldFile(exe)));
    }

    [Fact]
    public void A_start_deletes_what_an_update_left_but_what_is_still_held()
    {
        var exe = Write("Sloperator.exe", "exe");
        var old = Write("Sloperator.exe.old", "old");
        var fresh = Write("Sloperator.exe.new", "new");
        var part = Write("Sloperator.exe.new.part", "ne");
        var other = Write("notes.txt", "mine");

        using (new FileStream(old, FileMode.Open, FileAccess.Read, FileShare.Read))
            SelfUpdate.CleanUp(exe);
        Assert.True(File.Exists(old)); // the version before, still ending
        Assert.False(File.Exists(fresh));
        Assert.False(File.Exists(part));
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(other));

        SelfUpdate.CleanUp(exe);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void A_folder_of_ones_own_takes_files_and_a_missing_one_does_not()
    {
        Assert.True(SelfUpdate.CanWrite(_root));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
        Assert.False(SelfUpdate.CanWrite(Path.Combine(_root, "not there")));
    }

    [Fact]
    public void An_exe_has_its_file_version_and_a_text_file_none()
    {
        var dotnet = Environment.ProcessPath!;
        Assert.NotNull(SelfUpdate.FileVersion(dotnet));
        Assert.Null(SelfUpdate.FileVersion(Write("notes.txt", "not an exe")));
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>GitHub's API and pages as the lookup asks them, keeping what was asked.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public string? Api { get; init; }
        public HttpStatusCode ApiStatus { get; init; } = HttpStatusCode.OK;
        public string? LatestTag { get; init; }
        public string Assets { get; init; } = "";
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Asked.Add(url.AbsoluteUri);
            HttpResponseMessage Answer(HttpStatusCode status, string body, string type) =>
                new(status) { Content = new StringContent(body, Encoding.UTF8, type), RequestMessage = request };
            if (url.Host == ApiBase.Host)
                return Task.FromResult(ApiStatus == HttpStatusCode.OK && Api is not null
                    ? Answer(HttpStatusCode.OK, Api, "application/json")
                    : Answer(ApiStatus, """{"message":"API rate limit exceeded"}""", "application/json"));
            if (LatestTag is null)
                return Task.FromResult(Answer(HttpStatusCode.ServiceUnavailable, "", "text/html"));
            if (url.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal))
            {
                // GitHub's redirect to the tag's page, as HttpClient follows it.
                var redirected = new HttpRequestMessage(HttpMethod.Get, new Uri(url, $"tag/{LatestTag}"));
                Asked.Add(redirected.RequestUri!.AbsoluteUri);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>release</html>"), RequestMessage = redirected });
            }
            if (url.AbsolutePath.EndsWith($"/expanded_assets/{LatestTag}", StringComparison.Ordinal))
                return Task.FromResult(Answer(HttpStatusCode.OK, Assets, "text/html"));
            return Task.FromResult(Answer(HttpStatusCode.NotFound, "", "text/html"));
        }
    }
}
