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
    protected override Type StyleKeyOverride => typeof(TextEditor);

    protected override IVisualLineTransformer CreateColorizer(IHighlightingDefinition highlightingDefinition) => new DarkColorizer(highlightingDefinition);

    /// <summary>
    /// The highlighting for a file, by its extension: AvaloniaEdit's own, and for the kinds it has none
    /// of, the nearest it has (TypeScript as JavaScript, XAML and project files as XML, shaders as C++).
    /// Markdown without its headings in bigger letters, which a code editor's lines don't take. Null
    /// for the rest (and TeX, whose definition AvaloniaEdit can't load).
    /// </summary>
    public static IHighlightingDefinition? For(string path)
    {
        var name = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" or ".csx" => "C#",
            ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".mts" or ".cts" or ".tsx" => "JavaScript",
            ".html" or ".htm" => "HTML",
            ".css" or ".scss" or ".less" => "CSS",
            ".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx" or ".inl" or ".hlsl" or ".glsl" or ".shader" or ".cginc" or ".compute" or ".usf" or ".ush" => "C++",
            ".java" or ".kt" or ".kts" => "Java",
            ".ps1" or ".psm1" or ".psd1" => "PowerShell",
            ".php" => "PHP",
            ".py" or ".pyw" => "Python",
            ".sql" => "TSQL",
            ".vb" => "VB",
            ".xml" or ".xaml" or ".axaml" or ".csproj" or ".vbproj" or ".fsproj" or ".vcxproj" or ".props" or ".targets" or ".slnx" or ".resx"
                or ".config" or ".manifest" or ".nuspec" or ".svg" or ".xsd" or ".xsl" or ".xslt" or ".plist" or ".uxml" => "XML",
            ".md" or ".markdown" => "MarkDown",
            ".json" or ".jsonc" or ".json5" or ".asmdef" or ".uplugin" or ".uproject" => "Json",
            ".patch" or ".diff" => "Patch",
            ".boo" => "Boo",
            _ => null,
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
