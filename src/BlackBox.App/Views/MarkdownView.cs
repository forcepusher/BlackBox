using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BlackBox.Core.Chats;

namespace BlackBox.App.Views;

/// <summary>
/// A reply's Markdown as the chat shows it: <c>&lt;views:MarkdownView Source="{Binding ShownText}" /&gt;</c>.
/// Its text is in selectable blocks formatted by <see cref="MarkdownText"/>; each fenced code block
/// is a box of its own, in monospace, with its language and a Copy button over it.
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
    }

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
            if (i < Children.Count && Children[i] is SelectableTextBlock text && !block.IsCode)
                MarkdownText.SetSource(text, block.Text);
            else if (i < Children.Count && Children[i] is CodeBox box && block.IsCode)
                box.Show(block);
            else
            {
                if (i < Children.Count)
                    Children.RemoveAt(i);
                Children.Insert(i, block.IsCode ? new CodeBox(block, KeepsSelection) : TextPart(block.Text, KeepsSelection));
            }
        }
        while (Children.Count > blocks.Count)
            Children.RemoveAt(Children.Count - 1);
    }

    private static SelectableTextBlock TextPart(string markdown, bool keep)
    {
        // Its margin is its padding turned inside out: the text sits where it did, and a drag that
        // starts up to 12 pixels left or right of it, or 4 over or under it, selects from its first
        // or last letter, so the user needn't land on the letter itself.
        var text = keep ? new KeptSelectionTextBlock() : new SelectableTextBlock();
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
            _code = keep ? new KeptSelectionTextBlock() : new SelectableTextBlock();
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
