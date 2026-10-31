using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Rendering;

namespace Sloperator.App.Views;

/// <summary>
/// The Code editor panel's editor: AvaloniaEdit's (MIT), its highlighting the ones it comes with,
/// drawn in the app's syntax colours (<c>BbSyntax*</c> in App.axaml) on the dark background rather
/// than in their own colours, which are for a white one.
/// </summary>
public sealed class CodeView : TextEditor
{
    public CodeView()
    {
        TextArea.TextView.BackgroundRenderers.Add(_light);
        // Ahead of the editor's own scrolling: Ctrl+wheel zooms the text instead.
        AddHandler(PointerWheelChangedEvent, OnZoomWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    /// <summary>What a touchpad's wheel turned short of a notch, kept for the next turn.</summary>
    private double _wheel;

    /// <summary>Set while Ctrl+wheel sets the size: it keeps the line under the pointer itself.</summary>
    private bool _wheelZoom;

    /// <summary>The size set another way (the zoom buttons, Ctrl+0): the line at the top of the view stays at the top.</summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != FontSizeProperty || _wheelZoom || Document is null || !TextArea.TextView.VisualLinesValid)
            return;
        var view = TextArea.TextView;
        // Read with the old sizes, which the view has till it is laid out again.
        if (view.GetDocumentLineByVisualTop(view.VerticalOffset) is not { } top)
            return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!top.IsDeleted)
                ScrollToVerticalOffset(view.GetVisualTopByDocumentLine(top.LineNumber));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Ctrl+wheel (the user, 2026-10-31): the text a size bigger or smaller a notch, between
    /// <see cref="ViewModels.CodeEditorViewModel.MinFontSize"/> and <see cref="ViewModels.CodeEditorViewModel.MaxFontSize"/>,
    /// as VS Code's editor zooms; the line under the pointer kept where it is. Without Ctrl it scrolls as ever.
    /// </summary>
    private void OnZoomWheel(object? sender, Avalonia.Input.PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & Avalonia.Input.KeyModifiers.Control) == 0)
            return;
        e.Handled = true;
        _wheel += e.Delta.Y;
        var steps = (int)Math.Truncate(_wheel);
        if (steps == 0)
            return;
        _wheel -= steps;
        var size = Math.Clamp(FontSize + steps, ViewModels.CodeEditorViewModel.MinFontSize, ViewModels.CodeEditorViewModel.MaxFontSize);
        if (size == FontSize)
            return;
        var view = TextArea.TextView;
        var at = e.GetPosition(view).Y;
        var line = Document is null ? null : view.GetDocumentLineByVisualTop(view.VerticalOffset + at);
        // Where the line's top is from the pointer (at or above it), before the new size.
        var above = line is null ? 0 : view.GetVisualTopByDocumentLine(line.LineNumber) - view.VerticalOffset - at;
        var ratio = size / FontSize;
        _wheelZoom = true;
        try { FontSize = size; }
        finally { _wheelZoom = false; }
        if (line is null)
            return;
        UpdateLayout();
        if (!line.IsDeleted)
            ScrollToVerticalOffset(Math.Max(0, view.GetVisualTopByDocumentLine(line.LineNumber) - at - above * ratio));
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    private readonly ChangeLight _light = new();

    /// <summary>
    /// Lines the agent just changed (first to last, from 1): lit in the accent (<c>BbFresh</c>), the light
    /// fading over 2 s after 4 s, as File changes lights its fresh rows; the first left where it is in the
    /// upper half of the view, else brought a quarter of the way down, as File changes does.
    /// </summary>
    public void Light(IReadOnlyList<(int From, int To)> lines)
    {
        if (Document is not { } document || lines.Count == 0)
            return;
        _light.Light(TextArea.TextView, document, lines);
        var first = Math.Clamp(lines.Min(l => l.From), 1, document.LineCount);
        // Once the file just opened is laid out.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var top = TextArea.TextView.GetVisualTopByDocumentLine(first);
            if (top < VerticalOffset || top > VerticalOffset + ViewportHeight / 2)
                ScrollToVerticalOffset(Math.Max(0, top - ViewportHeight / 4));
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>The lit lines, drawn under the text as whole rows; their places follow edits made after.</summary>
    private sealed class ChangeLight : IBackgroundRenderer
    {
        private readonly List<(AvaloniaEdit.Document.TextAnchor From, AvaloniaEdit.Document.TextAnchor To)> _lines = [];
        private readonly Avalonia.Threading.DispatcherTimer _fade = new() { Interval = TimeSpan.FromMilliseconds(50) };
        private TextView? _view;
        private DateTime _litAt;
        private double _opacity;

        public ChangeLight() => _fade.Tick += (_, _) =>
        {
            var seconds = (DateTime.UtcNow - _litAt).TotalSeconds;
            _opacity = seconds < 4 ? 1 : Math.Max(0, 1 - (seconds - 4) / 2);
            if (_opacity == 0)
            {
                _fade.Stop();
                _lines.Clear();
            }
            _view?.InvalidateLayer(KnownLayer.Background);
        };

        public KnownLayer Layer => KnownLayer.Background;

        public void Light(TextView view, AvaloniaEdit.Document.TextDocument document, IReadOnlyList<(int From, int To)> lines)
        {
            _view = view;
            _lines.Clear();
            foreach (var (from, to) in lines)
            {
                var first = document.GetLineByNumber(Math.Clamp(from, 1, document.LineCount));
                var last = document.GetLineByNumber(Math.Clamp(to, from, document.LineCount));
                _lines.Add((document.CreateAnchor(first.Offset), document.CreateAnchor(last.Offset)));
            }
            _litAt = DateTime.UtcNow;
            _opacity = 1;
            _fade.Start();
            view.InvalidateLayer(KnownLayer.Background);
        }

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_opacity <= 0 || _lines.Count == 0 || textView.Document is not { } document
                || Application.Current?.FindResource("BbFresh") is not ISolidColorBrush fresh)
                return;
            var brush = new SolidColorBrush(fresh.Color, _opacity);
            foreach (var line in textView.VisualLines)
            {
                var number = line.FirstDocumentLine.LineNumber;
                if (!_lines.Any(l => !l.From.IsDeleted && number >= l.From.Line && number <= l.To.Line))
                    continue;
                var top = line.VisualTop - textView.VerticalOffset;
                drawingContext.FillRectangle(brush, new Rect(0, top, textView.Bounds.Width, line.Height));
            }
        }
    }

    protected override IVisualLineTransformer CreateColorizer(IHighlightingDefinition highlightingDefinition) => new DarkColorizer(highlightingDefinition);

    /// <summary>
    /// AvaloniaEdit's highlighting for a language of the Code editor's menu (<see cref="Core.Projects.CodeLanguages"/>):
    /// Markdown without its headings in bigger letters, which a code editor's lines don't take. Null for
    /// plain text, and for one AvaloniaEdit can't load.
    /// </summary>
    public static IHighlightingDefinition? Definition(string? language)
    {
        var name = language switch
        {
            "Markdown" => "MarkDown",
            "JSON" => "Json",
            "SQL" => "TSQL",
            "Diff" => "Patch",
            "Visual Basic" => "VB",
            _ => language,
        };
        try
        {
            return name is null ? null : HighlightingManager.Instance.GetDefinition(name);
        }
        catch (HighlightingDefinitionInvalidException)
        {
            return null;
        }
    }

    /// <summary>Which of the app's syntax colours (a key in App.axaml) AvaloniaEdit's colour of this name is drawn in; null for the code's own.</summary>
    public static string? SyntaxColour(string name) => DarkColorizer.Kind(name);

    /// <summary>
    /// AvaloniaEdit's highlighting with each of its named colours as one of the app's, by what it names
    /// ("Comment", "String", "Keywords", "MethodCall"…): one look for every language, readable on the dark
    /// background. Punctuation and what names nothing known take the code's own colour; sizes, fonts and
    /// backgrounds are left out, bold kept only for Markdown's strong emphasis.
    /// </summary>
    private sealed class DarkColorizer(IHighlightingDefinition definition) : HighlightingColorizer(definition)
    {
        private readonly Dictionary<HighlightingColor, HighlightingColor> _dark = [];

        protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color)
        {
            if (!_dark.TryGetValue(color, out var dark))
                _dark[color] = dark = Dark(color);
            base.ApplyColorToElement(element, dark);
        }

        private static HighlightingColor Dark(HighlightingColor color)
        {
            var name = color.Name ?? "";
            var dark = new HighlightingColor
            {
                Name = color.Name,
                FontStyle = color.FontStyle,
                FontWeight = Has(name, "strongemphasis") ? color.FontWeight : null,
                Underline = color.Underline,
                Strikethrough = color.Strikethrough,
            };
            if (Kind(name) is { } key && Application.Current?.FindResource(key) is ISolidColorBrush brush)
                dark.Foreground = new SimpleHighlightingBrush(brush.Color);
            dark.Freeze();
            return dark;
        }

        /// <summary>Which of the app's syntax colours a named colour of AvaloniaEdit's is; null for the code's own colour.</summary>
        internal static string? Kind(string name)
        {
            if (Has(name, "comment") || Has(name, "doctag") || Has(name, "javadoc"))
                return "BbSyntaxComment";
            if (Has(name, "interpolation") || Has(name, "punctuation") || Has(name, "operators") || Has(name, "slash") || Has(name, "assignment")
                || Has(name, "brace") || Has(name, "colon") || Has(name, "unchanged") || Has(name, "linebreak"))
                return null;
            if (Has(name, "string") || Has(name, "char") || Has(name, "regex") || Has(name, "attributevalue") || Has(name, "cdata") || Name(name, "value"))
                return "BbSyntaxString";
            if (Has(name, "number") || Has(name, "digit") || Has(name, "date"))
                return "BbSyntaxNumber";
            if (Has(name, "preprocessor") || Has(name, "doctype") || Has(name, "declaration") || Has(name, "aspsection"))
                return "BbSyntaxPreprocessor";
            if (Has(name, "method") || Has(name, "function") || Name(name, "command"))
                return "BbSyntaxMethod";
            if (Has(name, "attribute") || Has(name, "property") || Has(name, "fieldname") || Has(name, "variable"))
                return "BbSyntaxName";
            if (Has(name, "addedtext"))
                return "BbAdded";
            if (Has(name, "removedtext"))
                return "BbRemoved";
            if (Has(name, "keyword") || Has(name, "modifier") || Has(name, "visibility") || Has(name, "statement") || Has(name, "literal")
                || Has(name, "truefalse") || Name(name, "bool") || Name(name, "null") || Name(name, "this") || Name(name, "void") || Has(name, "constant")
                || Has(name, "controlflow") || Has(name, "exceptionhandling") || Has(name, "thisorbase") || Name(name, "friend") || Has(name, "getset") || Has(name, "tag") || Has(name, "heading") || Name(name, "selector"))
                return "BbSyntaxKeyword";
            if (Has(name, "type") || Has(name, "intrinsic") || Name(name, "class") || Has(name, "namespace") || Name(name, "package") || Has(name, "entit"))
                return "BbSyntaxType";
            if (Has(name, "link") || Has(name, "image") || Has(name, "blockquote") || Has(name, "header") || Has(name, "position") || Has(name, "filename"))
                return "BbSyntaxName";
            return null;
        }

        private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

        private static bool Name(string name, string whole) => name.Equals(whole, StringComparison.OrdinalIgnoreCase);
    }
}
