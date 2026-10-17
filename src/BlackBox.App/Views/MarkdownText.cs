using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using BlackBox.Core.Chats;

namespace BlackBox.App.Views;

/// <summary>
/// Shows a reply's Markdown formatted, in place of its raw text:
/// <c>views:MarkdownText.Source="{Binding ShownText}"</c> on a (selectable) text block.
/// Models answer in Markdown, and "**Total:**" or a table of pipes read badly as plain text.
/// </summary>
public static class MarkdownText
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Source", typeof(MarkdownText));

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    static MarkdownText() => SourceProperty.Changed.AddClassHandler<TextBlock>((block, e) => Render(block, e.NewValue as string ?? ""));

    public static string? GetSource(TextBlock block) => block.GetValue(SourceProperty);

    public static void SetSource(TextBlock block, string? value) => block.SetValue(SourceProperty, value);

    private static void Render(TextBlock block, string markdown)
    {
        var inlines = new InlineCollection();
        List<List<MdSpan>> lines;
        try { lines = MarkdownLite.Render(markdown); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Markdown the renderer trips on shows as written: this runs at every streamed piece of
            // a reply, on the window's thread.
            CrashLog.Write("showing a reply's Markdown", ex);
            lines = markdown.Replace("\r\n", "\n").Split('\n').Select(l => new List<MdSpan> { new(l, MdStyle.None) }).ToList();
        }
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0) inlines.Add(new LineBreak());
            foreach (var span in lines[i])
                inlines.Add(Run(block, span));
        }
        block.Inlines = inlines;
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
        if (span.Style.HasFlag(MdStyle.Heading))
            run.FontSize = block.FontSize + 2;
        if (span.Style.HasFlag(MdStyle.Muted) && Application.Current?.TryFindResource("BbMuted", out var muted) == true && muted is IBrush brush)
            run.Foreground = brush;
        return run;
    }
}
