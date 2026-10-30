using Avalonia.Input.Platform;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sloperator.Core.Chats;

namespace Sloperator.App.Views;

/// <summary>
/// A reply's Markdown as the chat shows it: <c>&lt;views:MarkdownView Source="{Binding ShownText}" /&gt;</c>.
/// Its text is in selectable blocks formatted by <see cref="MarkdownText"/>; each fenced code block
/// is a box of its own, in monospace, with its language and a Copy button over it, and each table a
/// grid of its own (<see cref="MarkdownTable"/>). Text that keeps its selection (File inspector,
/// which picks a table's cells by where they are in its lines) shows tables as lined-up text.
/// </summary>
public class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Source));

    private static readonly FontFamily Mono = new("Cascadia Mono,Consolas,monospace");

    static MarkdownView()
    {
        SourceProperty.Changed.AddClassHandler<MarkdownView>((view, _) => view.Update());
    }

    public MarkdownView()
    {
        Spacing = 10;
        AddHandler(PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnMoved, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, _) => _anchor = null, handledEventsToo: true);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(LostFocusEvent, OnLostFocus, handledEventsToo: true);
    }

    // ---- selecting across blocks ----------------------------------------------------------------
    // A reply is many text blocks (paragraphs, list items, cells, code). A drag that starts in one
    // and goes on into others selects from where it started to where it is, through every block
    // between, and Ctrl+C copies it all, a block's text on a line of its own. File inspector, whose
    // text keeps a selection of one block, doesn't.

    private SelectableTextBlock? _anchor;
    private int _anchorAt;

    /// <summary>Every text block of the reply, in reading order.</summary>
    private List<SelectableTextBlock> AllTexts =>
        this.GetVisualDescendants().OfType<SelectableTextBlock>().Where(t => t.IsEffectivelyVisible).ToList();

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (KeepsSelection || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var block = (e.Source as Visual)?.FindAncestorOfType<SelectableTextBlock>(includeSelf: true);
        foreach (var other in AllTexts)
            if (!ReferenceEquals(other, block) && other.SelectionStart != other.SelectionEnd)
                other.ClearSelection();
        _anchor = block;
        _anchorAt = block is null ? 0 : CharAt(block, e.GetPosition(block));
    }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_anchor is not { } anchor || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var texts = AllTexts;
        var from = texts.IndexOf(anchor);
        var point = e.GetPosition(this);
        var target = Nearest(texts, point);
        var to = target is null ? -1 : texts.IndexOf(target);
        if (from < 0 || to < 0)
            return;
        for (var i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            var length = text.Inlines?.Text?.Length ?? 0;
            if (i == from && i == to)
                continue; // the block selects within itself
            if (i == from)
                (text.SelectionStart, text.SelectionEnd) = (_anchorAt, to > from ? length : 0);
            else if (i == to)
                (text.SelectionStart, text.SelectionEnd) = (to > from ? 0 : length, CharAt(text, e.GetPosition(text)));
            else if (i > Math.Min(from, to) && i < Math.Max(from, to))
                (text.SelectionStart, text.SelectionEnd) = (0, length);
            else if (text.SelectionStart != text.SelectionEnd)
                text.ClearSelection();
        }
    }

    /// <summary>Ctrl+C with a selection over several blocks copies all of it.</summary>
    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (KeepsSelection || !(e.KeyModifiers == KeyModifiers.Control && e.Key is Key.C or Key.Insert))
            return;
        var selected = AllTexts.Where(t => t.SelectionStart != t.SelectionEnd).ToList();
        if (selected.Count < 2)
            return;
        e.Handled = true;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(string.Join(Environment.NewLine, selected.Select(t => t.SelectedText)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another program holds the clipboard; the next try will do.
        }
    }

    /// <summary>The block a drag started in lost its own selection with the focus: the rest of it goes too.</summary>
    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (KeepsSelection || _anchor is not null || e.Source is not SelectableTextBlock left)
            return;
        foreach (var text in AllTexts)
            if (!ReferenceEquals(text, left) && !text.IsFocused && text.SelectionStart != text.SelectionEnd)
                text.ClearSelection();
    }

    /// <summary>The block under a point of the view, or the nearest: by rows first, then across.</summary>
    private SelectableTextBlock? Nearest(List<SelectableTextBlock> texts, Point point)
    {
        SelectableTextBlock? best = null;
        var bestDistance = double.MaxValue;
        foreach (var text in texts)
        {
            if (text.TranslatePoint(default, this) is not { } at)
                continue;
            var box = new Rect(at, text.Bounds.Size);
            var dy = point.Y < box.Top ? box.Top - point.Y : point.Y > box.Bottom ? point.Y - box.Bottom : 0;
            var dx = point.X < box.Left ? box.Left - point.X : point.X > box.Right ? point.X - box.Right : 0;
            var distance = dy * 10000 + dx;
            if (distance < bestDistance)
                (best, bestDistance) = (text, distance);
        }
        return best;
    }

    /// <summary>The place between characters nearest a point of a block.</summary>
    private static int CharAt(SelectableTextBlock block, Point point) =>
        block.TextLayout.HitTestPoint(new Point(point.X - block.Padding.Left, point.Y - block.Padding.Top)).TextPosition;

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Its text keeps a selection when it loses focus (<see cref="KeptSelectionTextBlock"/>): set before <see cref="Source"/>.</summary>
    public bool KeepsSelection { get; set; }

    /// <summary>Its blocks' text, each where it is among <see cref="MarkdownLite.Blocks(string)"/>.</summary>
    private IEnumerable<(int Block, SelectableTextBlock Text)> Texts =>
        Children.Select((c, i) => (i, c is CodeBox box ? box.Code : c as SelectableTextBlock))
            .Where(t => t.Item2 is not null).Select(t => (t.i, t.Item2!));

    /// <summary>
    /// What is selected: the block it is in, among <see cref="MarkdownLite.Blocks(string)"/>, and its
    /// characters in the block's lines as shown; the focused block's, else the first with a selection.
    /// </summary>
    public (int Block, int Start, int End)? Selection =>
        Texts.Where(t => t.Text.SelectionStart != t.Text.SelectionEnd)
            .OrderBy(t => t.Text.IsFocused ? 0 : 1)
            .Select(t => ((int, int, int)?)(t.Block, Math.Min(t.Text.SelectionStart, t.Text.SelectionEnd), Math.Max(t.Text.SelectionStart, t.Text.SelectionEnd)))
            .FirstOrDefault();

    /// <summary>Clears the selection in every block but <paramref name="except"/>.</summary>
    public void ClearSelection(SelectableTextBlock? except = null)
    {
        foreach (var (_, text) in Texts)
            if (!ReferenceEquals(text, except) && text.SelectionStart != text.SelectionEnd)
                text.ClearSelection();
    }

    private void Update()
    {
        var source = Source ?? "";
        List<MdBlock> blocks;
        try { blocks = MarkdownLite.Blocks(source); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write("cutting a reply at its code blocks", ex);
            blocks = [new MdBlock(source, false)];
        }
        // A streaming reply comes here at every piece of it: the parts already shown keep their
        // controls (and a selection in them) and only get their new text.
        for (var i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var parts = block.IsCode || KeepsSelection ? null : Parts(block.Text);
            if (i < Children.Count && Children[i] is SelectableTextBlock text && !block.IsCode && parts is null)
                MarkdownText.SetSource(text, block.Text);
            else if (i < Children.Count && Children[i] is MarkdownParts panel && parts is not null)
                panel.Show(parts);
            else if (i < Children.Count && Children[i] is CodeBox box && block.IsCode)
                box.Show(block);
            else
            {
                if (i < Children.Count)
                    Children.RemoveAt(i);
                Children.Insert(i, block.IsCode ? new CodeBox(block, KeepsSelection) : parts is not null ? new MarkdownParts(parts) : TextPart(block.Text, KeepsSelection));
            }
        }
        while (Children.Count > blocks.Count)
            Children.RemoveAt(Children.Count - 1);
    }

    /// <summary>A block of text as the parts a page shows (<see cref="MarkdownLite.Parts"/>); null when that fails, and it shows as one text block.</summary>
    private static List<MdPart>? Parts(string markdown)
    {
        try { return MarkdownLite.Parts(markdown); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CrashLog.Write("cutting a reply into paragraphs", ex);
            return null;
        }
    }

    internal static SelectableTextBlock TextPart(string markdown, bool keep = false)
    {
        // Its margin is its padding turned inside out: the text sits where it did, and a drag that
        // starts up to 12 pixels left or right of it, or 4 over or under it, selects from its first
        // or last letter, so the user needn't land on the letter itself.
        var text = keep ? new KeptSelectionTextBlock() : new SelectableText();
        text.TextWrapping = TextWrapping.Wrap;
        text.LineHeight = 21;
        text.Margin = new Thickness(-12, -4);
        text.Padding = new Thickness(12, 4);
        MarkdownText.SetSource(text, markdown);
        return text;
    }

    /// <summary>A fenced code block: the language it names and Copy over its code, which wraps.</summary>
    private sealed class CodeBox : Border
    {
        private readonly TextBlock _language = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly SelectableTextBlock _code;
        private readonly Button _copy = new() { Content = "Copy" };
        private string _text = "";

        // Styled as the Border it is: a style's type matches only the exact type.
        protected override Type StyleKeyOverride => typeof(Border);

        public SelectableTextBlock Code => _code;

        public CodeBox(MdBlock block, bool keep)
        {
            _code = keep ? new KeptSelectionTextBlock() : new SelectableText();
            _code.FontFamily = Mono;
            _code.TextWrapping = TextWrapping.Wrap;
            Classes.Add("code");
            _language.Classes.Add("muted");
            _code.Classes.Add("code");
            _copy.Classes.Add("copyCode");
            ToolTip.SetTip(_copy, "Copy the code");
            _copy.Click += OnCopy;
            var header = new DockPanel { Classes = { "codeHeader" } };
            DockPanel.SetDock(_copy, Dock.Right);
            header.Children.Add(_copy);
            header.Children.Add(_language);
            var panel = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);
            panel.Children.Add(_code);
            Child = panel;
            Show(block);
        }

        public void Show(MdBlock block)
        {
            _text = block.Text;
            _language.Text = block.Language;
            // A block in a list item is indented with the item's text (MarkdownParts' 24 a level).
            var left = block.Indent >= 2 ? Math.Max(1, block.Indent / 3) * 24 : 0;
            if (Margin.Left != left)
                Margin = new Thickness(left, 0, 0, 0);
            MarkdownText.SetPlain(_code, block.Text);
        }

        private async void OnCopy(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                    await clipboard.SetTextAsync(_text);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Another program holds the clipboard; the next try will do.
                return;
            }
            _copy.Content = "Copied";
            DispatcherTimer.RunOnce(() => _copy.Content = "Copy", TimeSpan.FromSeconds(1.5));
        }
    }
}
