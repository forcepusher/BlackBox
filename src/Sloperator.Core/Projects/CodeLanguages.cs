namespace Sloperator.Core.Projects;

/// <summary>
/// The languages the Code editor highlights (the highlighting AvaloniaEdit comes with), by the names its
/// language menu shows, and which one a file is by its extension. A file of a kind it doesn't know, or
/// one shown wrong, takes another picked from the menu (the user, 2026-10-31: "manually select
/// highlighting if the file format is weird"), kept for every file of its kind (<see cref="Key"/>).
/// </summary>
public static class CodeLanguages
{
    /// <summary>What the menu calls no highlighting at all.</summary>
    public const string PlainText = "Plain text";

    /// <summary>The languages offered, in the menu's order; <see cref="PlainText"/> after them.</summary>
    public static IReadOnlyList<string> Names { get; } =
        ["C#", "C++", "CSS", "Diff", "HTML", "Java", "JavaScript", "JSON", "Markdown", "PHP", "PowerShell", "Python", "SQL", "Visual Basic", "XML"];

    /// <summary>
    /// The language a file is by its extension, the nearest one offered for the kinds there is none of
    /// (TypeScript as JavaScript, XAML and project files as XML, shaders as C++); null for plain text.
    /// </summary>
    public static string? Of(string path) => Path.GetExtension(path).ToLowerInvariant() switch
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
        ".sql" => "SQL",
        ".vb" => "Visual Basic",
        ".xml" or ".xaml" or ".axaml" or ".csproj" or ".vbproj" or ".fsproj" or ".vcxproj" or ".props" or ".targets" or ".slnx" or ".resx"
            or ".config" or ".manifest" or ".nuspec" or ".svg" or ".xsd" or ".xsl" or ".xslt" or ".plist" or ".uxml" => "XML",
        ".md" or ".markdown" => "Markdown",
        ".json" or ".jsonc" or ".json5" or ".asmdef" or ".uplugin" or ".uproject" => "JSON",
        ".patch" or ".diff" => "Diff",
        _ => null,
    };

    /// <summary>
    /// What a language picked is kept by: the file's extension (".gd"), or its name when it has none
    /// ("dockerfile"), so every file of the kind opens in it.
    /// </summary>
    public static string Key(string path)
    {
        var extension = Path.GetExtension(path);
        return (extension.Length > 1 ? extension : Path.GetFileName(path)).ToLowerInvariant();
    }

    /// <summary>
    /// The language a file opens in: the one picked for its kind (<paramref name="picked"/>, by
    /// <see cref="Key"/>), else the one its extension says; null for plain text.
    /// </summary>
    public static string? For(string path, IReadOnlyDictionary<string, string> picked) =>
        picked.TryGetValue(Key(path), out var name) && (name == PlainText || Names.Contains(name)) ? name == PlainText ? null : name : Of(path);
}
