using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Sloperator.Core.Chats;

namespace Sloperator.App.Views;

/// <summary>
/// A block of a reply's text as a page shows it (<see cref="MarkdownLite.Parts"/>), one part under
/// another: paragraphs, headings by their size, list items whose lines wrap under their own text
/// rather than under the bullet, quotes with a bar beside them, rules, tables and footnotes. Each
/// part's text is a selectable block; <see cref="MarkdownView"/> lets a drag select across them.
/// </summary>
public sealed class MarkdownParts : StackPanel
{
    /// <summary>How far each level of a nested list goes in: the marker's column and the gap after it.</summary>
    private const double ListIndent = 24;

    public MarkdownParts(List<MdPart> parts) => Show(parts);

    public void Show(List<MdPart> parts)
    {
        // A streaming reply comes here at every piece of it: a part already shown as the same kind
        // keeps its control (and a selection in it) and only gets its new text.
        for (var i = 0; i < parts.Count; i++)
        {
            var part = parts[i];
            var key = Key(part);
            if (i < Children.Count && Children[i] is Control shown && Equals(shown.Tag, key))
                Fill(shown, part);
            else
            {
                if (i < Children.Count)
                    Children.RemoveAt(i);
                var made = Make(part);
                made.Tag = key;
                Children.Insert(i, made);
            }
            Space((Control)Children[i], i == 0 ? null : parts[i - 1], part);
        }
        while (Children.Count > parts.Count)
            Children.RemoveAt(Children.Count - 1);
    }

    /// <summary>What decides a part's control; a part of another key gets a new one.</summary>
    private static string Key(MdPart part) => $"{part.Kind} {part.Level} {part.Marker}";

    private static Control Make(MdPart part)
    {
        switch (part.Kind)
        {
            case MdKind.Table:
                return new MarkdownTable(part.Table!) { Margin = new Thickness(part.Level * ListIndent, 0, 0, 0) };
            case MdKind.Rule:
                return new Border { Height = 1, Classes = { "mdRule" } };
            case MdKind.Heading:
            {
                var text = MarkdownView.TextPart(part.Text);
                var (size, line) = part.Level switch { 1 => (22, 28), 2 => (19, 24), 3 => (16, 21), _ => (14, 21) };
                text.FontSize = size;
                text.LineHeight = line;
                text.FontWeight = FontWeight.SemiBold;
                if (part.Level == 6)
                    text.Classes.Add("mdMuted");
                return text;
            }
            case MdKind.Item:
                return Item(part.Marker, part.Text, part.Level, muted: false);
            case MdKind.Note:
                return Item(part.Marker, part.Text, 0, muted: true);
            case MdKind.Quote:
            {
                Control inner = part.Marker.Length > 0 ? Item(part.Marker, part.Text, 0, muted: true) : Muted(MarkdownView.TextPart(part.Text));
                // A bar for each quote it is in: "> > text" has two.
                for (var depth = 0; depth < Math.Max(1, part.Level); depth++)
                    inner = new Border { Classes = { "mdQuote" }, Child = inner };
                return inner;
            }
            default:
            {
                var text = MarkdownView.TextPart(part.Text);
                if (part.Level > 0)
                    text.Margin = new Thickness(part.Level * ListIndent - 12, -4, -12, -4);
                return text;
            }
        }
    }

    /// <summary>A list item: its marker, then its text, whose lines all start where its first does.</summary>
    private static Grid Item(string marker, string markdown, int level, bool muted)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(level * ListIndent, 0, 0, 0) };
        var text = MarkdownView.TextPart(markdown);
        if (muted)
            Muted(text);
        Grid.SetColumn(text, 1);
        Control bullet = marker is "☐" or "☑" ? Box(marker == "☑") : new TextBlock
        {
            Text = marker,
            LineHeight = 21,
            MinWidth = 16,
            Margin = new Thickness(0, 0, 8, 0),
            TextAlignment = marker == "•" ? TextAlignment.Center : TextAlignment.Right,
        };
        if (muted)
            bullet.Classes.Add("mdMuted");
        grid.Children.Add(bullet);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>
    /// A task list's box, drawn: the font's ☑ is a coloured emoji, and its ☐ a size of its own. 12 square,
    /// in the bullet's 16 and centred on the first line; ticked, on the accent colour.
    /// </summary>
    private static Border Box(bool ticked) => new()
    {
        Width = 12,
        Height = 12,
        Margin = new Thickness(2, 4, 10, 0),
        VerticalAlignment = VerticalAlignment.Top,
        CornerRadius = new CornerRadius(2),
        BorderThickness = new Thickness(1),
        Classes = { ticked ? "mdTicked" : "mdBox" },
        Child = ticked ? new TextBlock { Text = "✓", FontSize = 10, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#1A1A1A")) } : null,
    };

    private static T Muted<T>(T block) where T : TextBlock
    {
        block.Classes.Add("mdMuted");
        return block;
    }

    /// <summary>Gives a shown part its new text.</summary>
    private static void Fill(Control control, MdPart part)
    {
        switch (control)
        {
            case MarkdownTable table when part.Table is { } t:
                table.Show(t);
                break;
            case SelectableTextBlock text:
                MarkdownText.SetSource(text, part.Text);
                break;
            case Grid { Children: [_, SelectableTextBlock itemText] }:
                MarkdownText.SetSource(itemText, part.Text);
                break;
            case Border { Child: { } child } when part.Kind == MdKind.Quote:
                Fill(child, part);
                break;
        }
    }

    /// <summary>
    /// The room over a part: a list's items and a quote's lines close together (unless a blank line set them apart), more over a heading
    /// than under it, 12 between paragraphs (where a blank line was). Whole multiples of 4.
    /// </summary>
    private static void Space(Control control, MdPart? before, MdPart part)
    {
        double gap = before is not { } b ? 0
            : part.Kind == MdKind.Heading ? (part.Level <= 2 ? 20 : 16)
            : b.Kind == MdKind.Heading ? 8
            : b.Kind == part.Kind && part.Kind is MdKind.Item or MdKind.Note or MdKind.Quote && !part.Spaced ? 4
            : b.Kind == MdKind.Item && part.Kind == MdKind.Table && part.Level > 0 ? 8
            : 12;
        // A text block reaches 4 over its text (TextPart), so its top margin is 4 less.
        var top = control is SelectableTextBlock ? gap - 4 : gap;
        var m = control.Margin;
        if (m.Top != top)
            control.Margin = new Thickness(m.Left, top, m.Right, m.Bottom);
    }
}
