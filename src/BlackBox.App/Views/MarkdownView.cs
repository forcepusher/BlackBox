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
                Children.Insert(i, block.IsCode ? new CodeBox(block) : TextPart(block.Text));
            }
        }
        while (Children.Count > blocks.Count)
            Children.RemoveAt(Children.Count - 1);
    }

    private static SelectableTextBlock TextPart(string markdown)
    {
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        MarkdownText.SetSource(text, markdown);
        return text;
    }

    /// <summary>A fenced code block: the language it names and Copy over its code, which wraps.</summary>
    private sealed class CodeBox : Border
    {
        private readonly TextBlock _language = new() { VerticalAlignment = VerticalAlignment.Center };
        private readonly SelectableTextBlock _code = new() { FontFamily = Mono, TextWrapping = TextWrapping.Wrap };
        private readonly Button _copy = new() { Content = "Copy" };
        private string _text = "";

        // Styled as the Border it is: a style's type matches only the exact type.
        protected override Type StyleKeyOverride => typeof(Border);

        public CodeBox(MdBlock block)
        {
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
