using AngleSharp.Html.Parser;
using BlackBox.Core.Tools;
using BlackBox.Core.Web;
using Xunit;

namespace BlackBox.Core.Tests;

public class WebToolsTests
{
    private static string Extract(string html, bool keepNoscript = false) =>
        PageText.Extract(new HtmlParser().ParseDocument(html), keepNoscript).Text;

    [Fact]
    public async Task A_page_read_again_later_is_fetched_again_and_reading_on_uses_the_copy()
    {
        // Kept for ever, a local server's page fetched again after a change came back as it had
        // been the first time.
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new System.Net.HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var served = 0;
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var ctx = await listener.GetContextAsync();
                var body = System.Text.Encoding.UTF8.GetBytes($"<html><body><h1>Status</h1><p>Build number {++served} is live. " + new string('x', 400) + "</p></body></html>");
                ctx.Response.ContentType = "text/html; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        }, TestContext.Current.CancellationToken);
        var url = $"http://127.0.0.1:{port}/status";

        Assert.Contains("Build number 1", (await WebPage.ReadAsync(url, null, TestContext.Current.CancellationToken)).Text);
        Assert.Contains("Build number 1", (await WebPage.ReadAsync(url, null, TestContext.Current.CancellationToken, TimeSpan.FromMinutes(15))).Text);
        Assert.Contains("Build number 2", (await WebPage.ReadAsync(url, null, TestContext.Current.CancellationToken, TimeSpan.Zero)).Text);
        listener.Stop();
    }

    [Fact]
    public void DuckDuckGo_results_are_read_and_ads_dropped()
    {
        const string html = """
            <div class="result results_links result--ad"><a class="result__a" href="https://duckduckgo.com/y.js?ad_domain=x">Ad</a></div>
            <div class="result results_links"><h2><a class="result__a" href="https://docs.unity3d.com/ScriptReference/KeyCode.LeftArrow.html">Unity - Scripting API: <b>KeyCode.LeftArrow</b></a></h2>
              <a class="result__snippet">Left   arrow key.</a></div>
            <div class="result"><a class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fa%3Fb%3D1&amp;rut=x">Redirected</a></div>
            """;
        var hits = WebSearch.ParseDuckDuckGo(html);
        Assert.Equal(2, hits.Count);
        Assert.Equal(new SearchHit("Unity - Scripting API: KeyCode.LeftArrow", "https://docs.unity3d.com/ScriptReference/KeyCode.LeftArrow.html", "Left arrow key."), hits[0]);
        Assert.Equal("https://example.com/a?b=1", hits[1].Url);
    }

    [Fact]
    public void Bing_tracking_links_lead_to_the_page()
    {
        // u=a1 + base64url of the address.
        Assert.Equal("https://docs.unity3d.com/6000.0/Documentation/ScriptReference/KeyCode.html",
            WebSearch.ResolveBingLink("https://www.bing.com/ck/a?!&&p=abc&ptn=3&u=a1aHR0cHM6Ly9kb2NzLnVuaXR5M2QuY29tLzYwMDAuMC9Eb2N1bWVudGF0aW9uL1NjcmlwdFJlZmVyZW5jZS9LZXlDb2RlLmh0bWw&ntb=1"));
        Assert.Equal("https://example.com/", WebSearch.ResolveBingLink("https://example.com/"));
        Assert.Null(WebSearch.ResolveBingLink("https://www.bing.com/images/search?q=x"));

        const string html = """
            <ol id="b_results"><li class="b_algo"><h2><a href="https://www.bing.com/ck/a?u=a1aHR0cHM6Ly9leGFtcGxlLmNvbS94">Example</a></h2>
              <div class="b_caption"><p class="b_lineclamp2">The snippet.</p></div></li></ol>
            """;
        Assert.Equal([new SearchHit("Example", "https://example.com/x", "The snippet.")], WebSearch.ParseBing(html));
    }

    [Fact]
    public void Page_text_keeps_the_content_and_drops_the_furniture()
    {
        const string html = """
            <html><head><title>KeyCode</title><style>p{}</style></head><body>
            <nav><a href="/">Home</a><a href="/a">A</a></nav>
            <div class="sidebar"><a>Manual</a></div>
            <main>
              <h1>KeyCode</h1>
              <div class="suggest-success hide"><h2>Success!</h2><p>Thank you</p></div>
              <div class="hidden md:block">Shown on desktops</div>
              <p>Key codes returned by   <code>Event.keyCode</code>.</p>
              <pre class="codeExampleCS">using UnityEngine;<br><br>public class A {}</pre>
              <ul><li>One<ul><li>Nested</li></ul></li><li>Two</li></ul>
              <table><tr><th>Property</th><th>Description</th></tr><tr><td>LeftArrow</td><td>Left arrow key.</td></tr></table>
              <p>Padding so that main is clearly the content of this page, as it is on real pages with prose in them.</p>
            </main>
            <footer>Copyright</footer><script>var x;</script>
            </body></html>
            """;
        var text = Extract(html);
        Assert.Equal("""
            # KeyCode

            Shown on desktops

            Key codes returned by `Event.keyCode`.

            ```
            using UnityEngine;

            public class A {}
            ```

            - One
              - Nested
            - Two

            | Property | Description |
            | LeftArrow | Left arrow key. |

            Padding so that main is clearly the content of this page, as it is on real pages with prose in them.
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void Code_drawn_as_one_element_per_line_keeps_its_lines()
    {
        // docs.unity.com: each line a span that CSS shows as a block, no newlines in the markup.
        const string lines = "<pre><div><span class='line'><span>using UnityEngine;</span></span><span class='line'><span></span></span>" +
                             "<span class='line'><span>public class A {}</span></span></div></pre>";
        Assert.Equal("```\nusing UnityEngine;\n\npublic class A {}\n```", Extract(lines));
        // Highlighted tokens on one line stay on one line.
        const string tokens = "<pre><code><span class='kw'>var</span><span class='sp'> </span><span class='id'>x</span><span class='op'> = 1;</span></code></pre>";
        Assert.Equal("```\nvar x = 1;\n```", Extract(tokens));
    }

    [Fact]
    public void Noscript_content_is_read_only_when_asked()
    {
        const string html = "<body><div id=app></div><noscript><p>The forum post, served to browsers without JavaScript.</p></noscript></body>";
        Assert.Equal("", Extract(html));
        Assert.Equal("The forum post, served to browsers without JavaScript.", Extract(html, keepNoscript: true));
    }

    [Fact]
    public void Long_pages_come_in_parts_that_end_at_a_line()
    {
        var lines = Enumerable.Range(0, 400).Select(i => $"Line {i:000} of the page, padded out to be a bit longer.");
        var page = new WebPageText("https://example.com/", "Example", string.Join('\n', lines));
        var first = FetchPageTool.Part(page, 0);
        Assert.StartsWith("# Example\nhttps://example.com/\n(characters 0-", first);
        Assert.Contains("fetch_page with start=", first);
        var next = int.Parse(System.Text.RegularExpressions.Regex.Match(first, @"start=(\d+)").Groups[1].Value);
        Assert.Equal('\n', page.Text[next]);
        var second = FetchPageTool.Part(page, next);
        Assert.Contains("\n\nLine ", second); // starts on a whole line
        Assert.Contains("past its end", FetchPageTool.Part(page, page.Text.Length + 5));
    }

    [Fact]
    public void Find_returns_the_matching_lines_with_their_heading()
    {
        var text = "# KeyCode\n\nIntro.\n\n## Properties\n\n| UpArrow | Up arrow key. |\n| LeftArrow | Left arrow key. |\n| Insert | Insert key. |\n\n```\n# not a heading\n```";
        var page = new WebPageText("https://example.com/", "KeyCode", text);

        var found = FetchPageTool.Find(page, "leftarrow");
        Assert.Contains("1 line mentions \"leftarrow\"", found);
        Assert.Contains("under \"Properties\"", found);
        Assert.Contains("| LeftArrow | Left arrow key. |", found);
        Assert.DoesNotContain("Intro.", found);

        // No line has the phrase: lines with all of its words.
        Assert.Contains("| UpArrow | Up arrow key. |", FetchPageTool.Find(page, "arrow up"));
        Assert.Contains("Nothing on this page mentions", FetchPageTool.Find(page, "joystick"));

        // A long line is cut around the match, not at its start.
        var longLine = new string('a', 2000) + " LeftArrow " + new string('b', 2000);
        var cut = FetchPageTool.Around(longLine, ["LeftArrow"], 600);
        Assert.Contains("LeftArrow", cut);
        Assert.True(cut.Length <= 602);
    }

    [Fact]
    public void Web_tools_are_offered_unless_turned_off()
    {
        Assert.NotNull(ToolRegistry.CreateDefault(false).Find("web_search"));
        Assert.NotNull(ToolRegistry.CreateDefault(false).Find("fetch_page"));
        Assert.Null(ToolRegistry.CreateDefault(false, includeWebTools: false).Find("web_search"));
        Assert.Contains("web_search", Agent.SystemPrompt.Build("Windows", null, "C:\\", "", webSearch: true));
        Assert.DoesNotContain("web_search", Agent.SystemPrompt.Build("Windows", null, "C:\\", ""));
    }

    [Fact]
    public void Addresses_without_a_scheme_are_read_as_https()
    {
        Assert.Equal("https://docs.unity3d.com/x.html", WebPage.ParseAddress("docs.unity3d.com/x.html").ToString());
        Assert.Throws<WebPageException>(() => WebPage.ParseAddress("file:///C:/secret.txt"));
        Assert.Throws<WebPageException>(() => WebPage.ParseAddress("not an address"));
    }
}
