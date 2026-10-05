using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace BlackBox.Core.Web;

/// <summary>
/// Turns a web page into the plain text a model reads best: the main content only (no menus,
/// sidebars, cookie banners or footers), with headings as "#", lists as "-", code blocks fenced
/// with ``` and table rows as "| a | b |". Links keep only their text, so the result is a fraction
/// of the page's size.
/// </summary>
public static partial class PageText
{
    /// <summary>Elements that never hold content worth reading.</summary>
    private const string NoiseSelector =
        "script, style, noscript, template, svg, canvas, iframe, object, embed, button, select, input, textarea, " +
        "nav, footer, aside, dialog, [hidden], [aria-hidden='true'], " +
        "[role='navigation'], [role='banner'], [role='contentinfo'], [role='search'], [role='dialog'], [role='complementary']";

    /// <summary>Class or id words of page furniture.</summary>
    [GeneratedRegex(@"(^|[\s_-])(nav|navbar|navigation|menu|sidebar|side-bar|sidenav|breadcrumbs?|footer|cookies?|consent|gdpr|banner|share|sharing|social|related|advert|ads|promo|newsletter|subscribe|feedback|skip-link|toc)($|[\s_-])",
        RegexOptions.IgnoreCase)]
    private static partial Regex FurnitureName();

    /// <summary>Class names that hide an element (Bootstrap, Tailwind, common hand-written ones).</summary>
    private static readonly HashSet<string> HidingClasses = new(StringComparer.OrdinalIgnoreCase)
        { "hide", "hidden", "d-none", "is-hidden", "invisible", "visually-hidden", "sr-only", "screen-reader-text" };

    /// <param name="keepNoscript">
    /// Read what pages show browsers without JavaScript. Usually a "please enable JavaScript" line,
    /// but some sites (Discourse forums) put their whole content there and leave the page empty.
    /// </param>
    public static (string Title, string Text) Extract(IDocument doc, bool keepNoscript = false)
    {
        var title = Collapse(doc.Title ?? "");
        if (doc.Body is not { } body)
            return (title, "");

        foreach (var e in body.QuerySelectorAll(keepNoscript ? NoiseSelector.Replace("noscript, ", "") : NoiseSelector).ToList())
            e.Remove();
        foreach (var e in body.QuerySelectorAll("[style], [class]").Where(IsHidden).ToList())
            e.Remove();

        var root = FindMainContent(body);
        RemoveFurniture(root);

        var writer = new TextWriter();
        writer.Render(root);
        var text = writer.ToString();
        if (title.Length == 0 && root.QuerySelector("h1") is { } h1)
            title = Collapse(h1.TextContent);
        return (title, text);
    }

    /// <summary>
    /// Hidden by its style or a hiding class. A Tailwind "hidden md:block" shows on wider screens,
    /// so a hiding class next to a screen-size class doesn't count.
    /// </summary>
    private static bool IsHidden(IElement e)
    {
        if (e.GetAttribute("style") is { } style && Regex.IsMatch(style, @"display\s*:\s*none|visibility\s*:\s*hidden", RegexOptions.IgnoreCase))
            return true;
        return e.ClassList.Any(HidingClasses.Contains) && !e.ClassList.Any(c => c.Contains(':'));
    }

    /// <summary>
    /// The element that holds the page's own content: &lt;main&gt; (or role=main), else the
    /// largest &lt;article&gt;, else the body. A candidate with almost no text (an empty shell
    /// a script fills in) doesn't count. When one article holds most of the main element's text
    /// (a README on a GitHub page, a post on a blog), it is the content.
    /// </summary>
    private static IElement FindMainContent(IElement body)
    {
        IElement? Best(IElement within, string selector, double share)
        {
            var length = TextLength(within);
            return within.QuerySelectorAll(selector)
                .Select(e => (Element: e, Length: TextLength(e)))
                .Where(c => c.Length >= 200 && c.Length >= length * share)
                .OrderByDescending(c => c.Length)
                .Select(c => c.Element)
                .FirstOrDefault();
        }
        var root = Best(body, "main, [role='main']", 0.2) ?? Best(body, "article", 0.2) ?? Best(body, "#content, #main-content, #main, .main-content", 0.2) ?? body;
        return root.LocalName == "article" ? root : Best(root, "article", 0.6) ?? root;
    }

    /// <summary>
    /// Drops menus and sidebars inside the content: blocks whose class or id names page furniture,
    /// and lists or divs that are mostly links. Nothing that holds most of the text is dropped, and
    /// when the content itself is mostly links (an index page), link-heavy blocks stay.
    /// </summary>
    private static void RemoveFurniture(IElement root)
    {
        var total = Math.Max(1, TextLength(root));
        var linkHeavyPage = LinkLength(root) > total / 2;
        foreach (var e in root.QuerySelectorAll("*").ToList())
        {
            if (e.ParentElement is null || !root.Contains(e))
                continue; // already removed with an ancestor
            if (e.LocalName is "main" or "article" or "pre" or "code" or "table" or "tbody" or "tr" or "td" or "th")
                continue;
            var length = TextLength(e);
            if (length > total / 2)
                continue;
            var name = $"{e.Id} {e.GetAttribute("class")}";
            if (name.Length > 1 && FurnitureName().IsMatch(name))
            {
                e.Remove();
                continue;
            }
            if (!linkHeavyPage && e.LocalName is "ul" or "ol" or "div" or "section" or "dl" && length > 0
                && e.QuerySelectorAll("a").Length >= 3 && LinkLength(e) > length * 0.7)
                e.Remove();
        }
    }

    private static int TextLength(IElement e) => CountNonSpace(e.TextContent);

    private static int LinkLength(IElement e) => e.QuerySelectorAll("a").Sum(a => CountNonSpace(a.TextContent));

    private static int CountNonSpace(string s)
    {
        var n = 0;
        foreach (var c in s)
            if (!char.IsWhiteSpace(c)) n++;
        return n;
    }

    /// <summary>The text of a &lt;pre&gt; as shown: some sites break its lines with &lt;br&gt; or a block per line rather than newlines.</summary>
    private static string PreText(IElement pre)
    {
        var sb = new StringBuilder();
        void Walk(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText t)
                    sb.Append(t.Data);
                else if (child is IElement e)
                {
                    if (e.LocalName == "br")
                    {
                        sb.Append('\n');
                        continue;
                    }
                    var block = e.LocalName is "div" or "p" or "li" or "tr";
                    if (block && sb.Length > 0 && sb[^1] != '\n')
                        sb.Append('\n');
                    Walk(e);
                    if (block && sb.Length > 0 && sb[^1] != '\n')
                        sb.Append('\n');
                }
            }
        }
        Walk(pre);
        if (sb.ToString().Trim('\n').Contains('\n'))
            return sb.ToString();

        // No line breaks at all: lines as sibling elements that CSS shows as blocks (docs.unity.com
        // renders code as one span per line). The element with the most children holds the lines,
        // if they are all alike; highlighted tokens on one line have different classes.
        var lines = pre.QuerySelectorAll("*").Append(pre).MaxBy(e => e.Children.Length);
        return lines is { Children.Length: >= 3 }
               && lines.Children.Select(c => (c.LocalName, c.ClassName)).Distinct().Count() == 1
            ? string.Join('\n', lines.Children.Select(c => c.TextContent))
            : sb.ToString();
    }

    internal static string Collapse(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Writes the DOM as text, collapsing whitespace like a browser does except inside &lt;pre&gt;.</summary>
    private sealed class TextWriter
    {
        private readonly StringBuilder _sb = new();
        /// <summary>Line breaks owed before the next text: 1 = new line, 2 = blank line.</summary>
        private int _pendingBreak;
        private bool _pendingSpace;
        private string _indent = "";

        public override string ToString() => _sb.ToString().Trim();

        private void Break(int lines) => _pendingBreak = Math.Max(_pendingBreak, lines);

        private void Write(string text)
        {
            if (text.Length == 0)
                return;
            if (_sb.Length > 0 && _pendingBreak > 0)
            {
                while (_sb.Length > 0 && _sb[^1] == ' ')
                    _sb.Length--;
                _sb.Append('\n', _pendingBreak);
                _sb.Append(_indent);
            }
            else if (_pendingSpace && _sb.Length > 0 && _sb[^1] is not (' ' or '\n'))
                _sb.Append(' ');
            _pendingBreak = 0;
            _pendingSpace = false;
            _sb.Append(text);
        }

        private void Inline(string text)
        {
            if (text.Length == 0)
                return;
            if (char.IsWhiteSpace(text[0]))
                _pendingSpace = true;
            var collapsed = Collapse(text);
            if (collapsed.Length > 0)
                Write(collapsed);
            if (char.IsWhiteSpace(text[^1]))
                _pendingSpace = true;
        }

        public void Render(INode node)
        {
            foreach (var child in node.ChildNodes)
            {
                switch (child)
                {
                    case IText t:
                        Inline(t.Data);
                        break;
                    case IElement e:
                        RenderElement(e);
                        break;
                }
            }
        }

        private void RenderElement(IElement e)
        {
            switch (e.LocalName)
            {
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                {
                    // Without the "¶" anchor links some documentation generators add (Sphinx).
                    var heading = Collapse(e.TextContent.Replace("¶", ""));
                    if (heading.Length == 0) return;
                    Break(2);
                    Write(new string('#', e.LocalName[1] - '0') + " " + heading);
                    Break(2);
                    return;
                }
                case "br":
                    Break(1);
                    return;
                case "hr" or "img" or "picture" or "video" or "audio":
                    return;
                case "pre":
                {
                    var code = PreText(e).Replace("\r\n", "\n").Trim('\n');
                    if (code.Trim().Length == 0) return;
                    Break(2);
                    Write("```\n" + code + "\n```");
                    Break(2);
                    return;
                }
                case "code" or "kbd" or "samp" or "tt":
                {
                    if (e.Closest("pre") is not null) break;
                    var code = Collapse(e.TextContent);
                    if (code.Length == 0) return;
                    if (e.PreviousSibling is IText { Data.Length: > 0 } prev && char.IsWhiteSpace(prev.Data[^1]))
                        _pendingSpace = true;
                    Write("`" + code + "`");
                    return;
                }
                case "table":
                    RenderTable(e);
                    return;
                case "li":
                {
                    Break(1);
                    var marker = e.ParentElement?.LocalName == "ol" ? $"{IndexIn(e)}. " : "- ";
                    Write(marker);
                    var outer = _indent;
                    _indent += "  ";
                    Render(e);
                    _indent = outer;
                    Break(1);
                    return;
                }
                case "ul" or "ol":
                {
                    Break(_indent.Length == 0 ? 2 : 1);
                    Render(e);
                    Break(_indent.Length == 0 ? 2 : 1);
                    return;
                }
                case "dt":
                    Break(1);
                    Render(e);
                    Break(1);
                    return;
                case "dd":
                {
                    Break(1);
                    var outer = _indent;
                    _indent += "  ";
                    Render(e);
                    _indent = outer;
                    Break(1);
                    return;
                }
                case "blockquote":
                {
                    Break(2);
                    var outer = _indent;
                    _indent += "> ";
                    Write("> ");
                    Render(e);
                    _indent = outer;
                    Break(2);
                    return;
                }
            }

            if (IsBlock(e.LocalName))
            {
                Break(e.LocalName is "p" or "section" or "article" or "header" or "figure" or "details" ? 2 : 1);
                Render(e);
                Break(e.LocalName is "p" or "section" or "article" or "header" or "figure" or "details" ? 2 : 1);
            }
            else
                Render(e);
        }

        private void RenderTable(IElement table)
        {
            Break(2);
            foreach (var row in table.QuerySelectorAll("tr"))
            {
                if (row.Closest("table") != table)
                    continue; // a nested table's row; its cell text comes with the outer cell
                var cells = row.Children.Where(c => c.LocalName is "td" or "th").Select(c => Collapse(c.TextContent).Replace("|", "\\|")).ToList();
                if (cells.All(c => c.Length == 0))
                    continue;
                Break(1);
                Write("| " + string.Join(" | ", cells) + " |");
            }
            Break(2);
        }

        private static int IndexIn(IElement li)
        {
            var start = li.ParentElement is IHtmlOrderedListElement ol ? ol.Start : 1;
            var i = 0;
            for (var s = li.PreviousElementSibling; s is not null; s = s.PreviousElementSibling)
                if (s.LocalName == "li") i++;
            return start + i;
        }

        private static bool IsBlock(string tag) => tag is
            "p" or "div" or "section" or "article" or "main" or "header" or "footer" or "aside" or "nav" or
            "form" or "fieldset" or "figure" or "figcaption" or "details" or "summary" or "address" or
            "dl" or "center" or "caption";
    }
}
