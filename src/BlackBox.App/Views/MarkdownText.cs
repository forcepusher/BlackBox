using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using BlackBox.Core.Chats;

namespace BlackBox.App.Views;

/// <summary>
/// Shows a reply's Markdown formatted, in place of its raw text:
/// <c>views:MarkdownText.Source="{Binding ShownText}"</c> on a (selectable) text block.
/// Models answer in Markdown, and "**Total:**" or a table of pipes read badly as plain text.
/// <c>views:MarkdownText.Plain</c> shows text that isn't Markdown as written. Both make the web
/// addresses in it links: a click opens the page in the browser, the address shows in a tooltip.
/// </summary>
public static class MarkdownText
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Source", typeof(MarkdownText));

    public static readonly AttachedProperty<string?> PlainProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Plain", typeof(MarkdownText));

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    /// <summary>Where each link is in a block's text, by character, for the clicks on it.</summary>
    private static readonly ConditionalWeakTable<TextBlock, List<(int Start, int End, string Url)>> Links = new();

    static MarkdownText()
    {
        SourceProperty.Changed.AddClassHandler<TextBlock>((block, e) => Render(block, e.NewValue as string ?? "", markdown: true));
        PlainProperty.Changed.AddClassHandler<TextBlock>((block, e) => Render(block, e.NewValue as string ?? "", markdown: false));
    }

    public static string? GetSource(TextBlock block) => block.GetValue(SourceProperty);

    public static void SetSource(TextBlock block, string? value) => block.SetValue(SourceProperty, value);

    public static string? GetPlain(TextBlock block) => block.GetValue(PlainProperty);

    public static void SetPlain(TextBlock block, string? value) => block.SetValue(PlainProperty, value);

    private static void Render(TextBlock block, string text, bool markdown)
    {
        List<List<MdSpan>> lines;
        try { lines = markdown ? MarkdownLite.Render(text) : MarkdownLite.Plain(text); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Markdown the renderer trips on shows as written: this runs at every streamed piece of
            // a reply, on the window's thread.
            CrashLog.Write("showing a reply's Markdown", ex);
            lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => new List<MdSpan> { new(l, MdStyle.None) }).ToList();
        }
        Show(block, lines);
    }

    /// <summary>Shows runs already styled (a table's cell) in <paramref name="block"/>, its links working as in a reply's text.</summary>
    public static void SetSpans(TextBlock block, List<MdSpan> spans) => Show(block, [spans]);

    private static void Show(TextBlock block, List<List<MdSpan>> lines)
    {
        var inlines = new InlineCollection();
        // Lines end in a "\n" of their own rather than a LineBreak, so that a character's place in
        // the block's text, which a click is hit-tested to, is the sum of the runs before it.
        var links = new List<(int, int, string)>();
        var at = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
            {
                inlines.Add(new Run("\n"));
                at++;
            }
            foreach (var span in lines[i])
            {
                inlines.Add(Run(block, span));
                if (span.Url != null)
                    links.Add((at, at + span.Text.Length, span.Url));
                at += span.Text.Length;
            }
        }
        block.Inlines = inlines;
        Links.AddOrUpdate(block, links);
        if (links.Count > 0)
            WatchClicks(block);
        // What screen readers (and UI Automation) read: the text as shown, without the markup.
        AutomationProperties.SetName(block, string.Join("\n", lines.Select(l => string.Concat(l.Select(s => s.Text)))));
    }

    private static Run Run(TextBlock block, MdSpan span)
    {
        var run = new Run(span.Text);
        if (span.Style.HasFlag(MdStyle.Bold))
            run.FontWeight = FontWeight.SemiBold;
        if (span.Style.HasFlag(MdStyle.Italic))
            run.FontStyle = FontStyle.Italic;
        if (span.Style.HasFlag(MdStyle.Code))
        {
            run.FontFamily = Mono;
            run.FontSize = block.FontSize - 1;
        }
        if (span.Style.HasFlag(MdStyle.Strike))
            run.TextDecorations = TextDecorations.Strikethrough;
        if (span.Style.HasFlag(MdStyle.Heading))
            run.FontSize = block.FontSize + 2;
        if (span.Style.HasFlag(MdStyle.Muted) && Application.Current?.TryFindResource("BbMuted", out var muted) == true && muted is IBrush brush)
            run.Foreground = brush;
        if (span.Url != null)
        {
            if (Application.Current?.TryFindResource("BbAccent", out var accent) == true && accent is IBrush link)
                run.Foreground = link;
            run.TextDecorations = TextDecorations.Underline;
        }
        return run;
    }

    // ---- links: the hand over them, their address as the tooltip, a click opening it -----------

    private static readonly ConditionalWeakTable<TextBlock, object> Watched = new();

    private static void WatchClicks(TextBlock block)
    {
        if (Watched.TryGetValue(block, out _)) return;
        Watched.Add(block, new object());
        Point? pressedAt = null;
        // Off a link the block's own cursor comes back: a selectable block's text cursor. Setting
        // null there gave the arrow, so text with a link in it showed the arrow, text without one
        // the text cursor (the user, 2026-10-26). Its own is known once it is in the window, styled.
        Cursor? own = null;
        var known = false;
        var hand = new Cursor(StandardCursorType.Hand);
        block.PointerMoved += (_, e) =>
        {
            if (!known)
                (own, known) = (block.Cursor, true);
            var url = LinkAt(block, e.GetPosition(block));
            var cursor = url != null ? hand : own;
            if (!ReferenceEquals(block.Cursor, cursor))
                block.Cursor = cursor;
            if (!Equals(ToolTip.GetTip(block), url))
                ToolTip.SetTip(block, url);
        };
        block.PointerExited += (_, _) => block.Cursor = own;
        // A selectable block takes the press for selecting, so these see handled events too. Only a
        // click opens a link: a drag over it selects its text, as anywhere else.
        block.AddHandler(InputElement.PointerPressedEvent, (_, e) =>
        {
            pressedAt = e.GetCurrentPoint(block).Properties.IsLeftButtonPressed ? e.GetPosition(block) : null;
        }, handledEventsToo: true);
        block.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
        {
            var start = pressedAt;
            pressedAt = null;
            if (start is not { } p || e.InitialPressMouseButton != MouseButton.Left) return;
            var point = e.GetPosition(block);
            if (Math.Abs(point.X - p.X) > 4 || Math.Abs(point.Y - p.Y) > 4) return;
            // A double-click selects the word under it rather than opening the page twice.
            if (block is SelectableTextBlock { SelectionStart: var s, SelectionEnd: var t } && s != t) return;
            if (LinkAt(block, point) is { } url)
                Open(block, url);
        }, handledEventsToo: true);
    }

    private static string? LinkAt(TextBlock block, Point point)
    {
        if (!Links.TryGetValue(block, out var links) || links.Count == 0) return null;
        var padding = block.Padding;
        var at = new Point(point.X - padding.Left, point.Y - padding.Top);
        var layout = block.TextLayout;
        // HitTestPoint gives the caret place nearest the point (its IsInside is false on every line
        // but a block's first); the character under the point is the one before it when the point
        // is left of it, and the point must be on that character's own box.
        var caret = layout.HitTestPoint(at).TextPosition;
        var index = caret > 0 && at.X < layout.HitTestTextPosition(caret).X ? caret - 1 : caret;
        if (!layout.HitTestTextRange(index, 1).Any(r => r.Inflate(new Thickness(0.5, 1)).Contains(at)))
            return null;
        foreach (var (start, end, url) in links)
            if (index >= start && index < end)
                return url;
        return null;
    }

    private static void Open(TextBlock block, string url)
    {
        if (!MarkdownLite.IsWebLink(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        if (TopLevel.GetTopLevel(block)?.Launcher is { } launcher)
            _ = launcher.LaunchUriAsync(uri);
    }
}
