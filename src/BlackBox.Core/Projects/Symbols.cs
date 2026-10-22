using System.Text.RegularExpressions;

namespace BlackBox.Core.Projects;

/// <summary>A definition in a file: a class, a function, a heading… and the line it starts on (1-based).</summary>
/// <param name="Depth">How far in it is indented, so a method can be told from the class it belongs to.</param>
internal readonly record struct Symbol(string Name, string Kind, int Line, int Depth)
{
    /// <summary>Kinds that hold others: a file's map lists what is in them under them.</summary>
    public bool IsType => Kind is "class" or "struct" or "interface" or "enum" or "record" or "trait" or "impl" or "module" or "namespace" or "object" or "protocol" or "extension" or "actor" or "type";
}

/// <summary>
/// Finds the definitions in source files by language, line by line with regular expressions:
/// the kind of map a "repo map" shows an assistant, and what search ranks first when a query names
/// one. Not a parser: a definition written across lines, or a language it doesn't know, is missed,
/// which costs a line of the map, not a wrong answer.
/// </summary>
internal static partial class Symbols
{
    /// <summary>The language of a file, by its extension or name; null for one that isn't source code or text worth a map.</summary>
    public static string? Language(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) return "Dockerfile";
        if (name.Equals("Makefile", StringComparison.OrdinalIgnoreCase) || name.Equals("GNUmakefile", StringComparison.OrdinalIgnoreCase)) return "Make";
        if (name.Equals("CMakeLists.txt", StringComparison.OrdinalIgnoreCase)) return "CMake";
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".cs" or ".csx" => "C#",
            ".fs" or ".fsx" => "F#",
            ".vb" => "Visual Basic",
            ".java" => "Java",
            ".kt" or ".kts" => "Kotlin",
            ".scala" => "Scala",
            ".js" or ".mjs" or ".cjs" or ".jsx" => "JavaScript",
            ".ts" or ".mts" or ".cts" or ".tsx" => "TypeScript",
            ".vue" => "Vue",
            ".svelte" => "Svelte",
            ".py" or ".pyw" or ".pyi" => "Python",
            ".go" => "Go",
            ".rs" => "Rust",
            ".c" or ".h" => "C",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".hh" or ".hxx" or ".inl" or ".ipp" => "C++",
            ".m" or ".mm" => "Objective-C",
            ".swift" => "Swift",
            ".dart" => "Dart",
            ".php" => "PHP",
            ".rb" or ".rake" => "Ruby",
            ".lua" => "Lua",
            ".gd" => "GDScript",
            ".gdshader" or ".shader" or ".hlsl" or ".glsl" or ".cginc" or ".compute" or ".usf" or ".ush" or ".wgsl" or ".vert" or ".frag" => "Shader",
            ".ps1" or ".psm1" or ".psd1" => "PowerShell",
            ".sh" or ".bash" or ".zsh" => "Shell",
            ".bat" or ".cmd" => "Batch",
            ".sql" => "SQL",
            ".r" => "R",
            ".jl" => "Julia",
            ".ex" or ".exs" => "Elixir",
            ".erl" => "Erlang",
            ".hs" => "Haskell",
            ".clj" or ".cljs" => "Clojure",
            ".zig" => "Zig",
            ".nim" => "Nim",
            ".pl" or ".pm" => "Perl",
            ".html" or ".htm" => "HTML",
            ".css" => "CSS",
            ".scss" or ".sass" or ".less" => "CSS",
            ".xaml" or ".axaml" => "XAML",
            ".xml" or ".xsd" or ".xsl" or ".csproj" or ".fsproj" or ".vbproj" or ".vcxproj" or ".props" or ".targets" or ".slnx" or ".config" or ".resx" or ".plist" or ".uplugin" or ".uproject" => "XML",
            ".json" or ".jsonc" or ".json5" => "JSON",
            ".yaml" or ".yml" => "YAML",
            ".toml" => "TOML",
            ".ini" or ".cfg" or ".conf" or ".properties" or ".editorconfig" or ".env" or ".tscn" or ".tres" or ".godot" => "Config",
            ".md" or ".markdown" or ".mdx" => "Markdown",
            ".rst" => "reStructuredText",
            ".tex" => "LaTeX",
            ".txt" or ".text" or ".log" => "Text",
            ".csv" or ".tsv" => "CSV",
            ".gradle" => "Gradle",
            ".proto" => "Protobuf",
            ".graphql" or ".gql" => "GraphQL",
            ".tf" or ".hcl" => "Terraform",
            ".sln" => "Solution",
            ".pdf" => "PDF",
            ".docx" => "Word",
            ".xlsx" => "Excel",
            ".pptx" => "PowerPoint",
            _ => null,
        };
    }

    /// <summary>The definitions in <paramref name="lines"/> of a file in <paramref name="language"/>.</summary>
    public static List<Symbol> Find(string? language, IReadOnlyList<string> lines)
    {
        var rules = RulesFor(language);
        var found = new List<Symbol>();
        if (rules.Length == 0)
            return found;
        var fence = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length is 0 or > 400)
                continue;
            // A heading in a Markdown code block is a comment in the code it shows.
            if (language == "Markdown" && line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fence = !fence;
                continue;
            }
            if (fence)
                continue;
            foreach (var (regex, kind) in rules)
            {
                var m = regex.Match(line);
                if (!m.Success)
                    continue;
                var name = m.Groups["name"].Value.Trim();
                // In C-like languages a call or a statement can look like a definition; "fn new" is one.
                if (name.Length == 0 || Keywords.Contains(name) && language is "C#" or "Java" or "Scala" or "JavaScript" or "TypeScript" or "Vue" or "Svelte"
                        or "C" or "C++" or "Objective-C" or "Shader" or "Kotlin" or "Swift" or "Dart" or "PHP")
                    continue;
                var k = m.Groups["kind"].Success ? KindOf(m.Groups["kind"].Value) : kind;
                if (k == "function" && Indent(line) > 0 && language is not ("Markdown" or "SQL"))
                    k = "method";
                found.Add(new Symbol(name.Length > 80 ? name[..80] : name, k, i + 1, language == "Markdown" ? m.Groups["kind"].Length : Indent(line)));
                break;
            }
        }
        return found;
    }

    private static int Indent(string line)
    {
        var n = 0;
        foreach (var c in line)
        {
            if (c == ' ') n++;
            else if (c == '\t') n += 4;
            else break;
        }
        return n;
    }

    private static string KindOf(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.StartsWith('#')) return "heading";
        if (w.StartsWith("record")) return "record";
        if (w.StartsWith("enum")) return "enum";
        return w switch
        {
            "fn" or "func" or "fun" or "def" or "function" or "procedure" or "sub" => "function",
            "mod" or "module" => "module",
            "class_name" => "class",
            _ => w,
        };
    }

    /// <summary>Words a pattern can mistake for a name: a call to if (...) is not a method named if.</summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "foreach", "while", "switch", "catch", "using", "lock", "return", "new", "nameof", "typeof", "sizeof",
        "default", "base", "this", "await", "else", "do", "try", "fixed", "checked", "unchecked", "when", "with", "function",
        "super", "throw", "yield", "delete", "void", "elif", "and", "or", "not", "in", "is", "match", "case",
        "readonly", "static", "const", "volatile", "async", "unsafe", "extern", "override", "virtual", "sealed", "abstract", "partial",
        "public", "private", "protected", "internal", "ref", "out", "params", "operator", "implicit", "explicit", "var", "get", "set",
        "init", "value", "string", "int", "bool", "object", "dynamic", "event", "delegate", "where", "select", "from", "let", "fun",
        "fn", "def", "func", "class", "struct", "interface", "enum", "record", "namespace", "import", "export", "package", "module",
        "require", "echo", "print", "template", "typename", "goto", "continue", "break", "pass", "raise", "except", "finally",
        "lambda", "global", "assert", "true", "false", "null", "None", "nil", "undefined", "self", "cls",
    };

    private static (Regex Regex, string Kind)[] RulesFor(string? language) => language switch
    {
        "C#" => [(CsType(), ""), (CsMethod(), "function"), (CsProperty(), "property")],
        "Java" or "Scala" => [(JavaType(), ""), (JavaMethod(), "function")],
        "Kotlin" => [(KotlinType(), ""), (KotlinFun(), "function")],
        "JavaScript" or "TypeScript" or "Vue" or "Svelte" => [(JsType(), ""), (JsFunction(), "function"), (JsArrow(), "function"), (JsMethod(), "function")],
        "Python" => [(PyClass(), "class"), (PyDef(), "function")],
        "Go" => [(GoType(), ""), (GoFunc(), "function")],
        "Rust" => [(RustType(), ""), (RustImpl(), "impl"), (RustFn(), "function")],
        "C" or "C++" or "Objective-C" or "Shader" => [(CType(), ""), (CFunction(), "function")],
        "Swift" => [(SwiftType(), ""), (SwiftFunc(), "function")],
        "Dart" => [(DartType(), "")],
        "PHP" => [(PhpType(), ""), (PhpFunction(), "function")],
        "Ruby" => [(RubyType(), ""), (RubyDef(), "function")],
        "Lua" => [(LuaFunction(), "function"), (LuaAssigned(), "function")],
        "GDScript" => [(GdClass(), ""), (GdFunc(), "function"), (GdSignal(), "signal")],
        "PowerShell" => [(PsClass(), "class"), (PsFunction(), "function")],
        "Shell" => [(ShFunction(), "function")],
        "SQL" => [(SqlCreate(), "")],
        "XAML" => [(XamlName(), "element")],
        "Markdown" => [(MdHeading(), "heading")],
        "F#" => [(FsType(), "type"), (FsLet(), "function")],
        "Visual Basic" => [(VbDefinition(), "")],
        _ => [],
    };

    private const string CsMods = @"(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|new|override|virtual|async|extern|file|required|ref|volatile|const)\s+)";

    [GeneratedRegex(@"^\s*" + CsMods + @"*(?<kind>class|struct|interface|enum|record(?:\s+struct|\s+class)?|namespace)\s+(?<name>[\w.]+)")]
    private static partial Regex CsType();

    // Not "new" alone: "new ToolResult(text," starting a call written across lines is no definition.
    // A line ending in ";" with no "=>" is a call or an abstract one; "=> x;" is a method's body.
    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|override|virtual|async|extern|file)\s+)+(?:new\s+)?(?:[\w<>\[\],.?()*& ]+?\s+)?(?<name>\w+)\s*(?:<[^<>()=;]*>)?\s*\((?![^=]*;\s*$)")]
    private static partial Regex CsMethod();

    [GeneratedRegex(@"^\s*" + CsMods + @"+[\w<>\[\],.?()* ]+?\s+(?<name>\w+)\s*(?:\{\s*(?:get|set|init)|=>)")]
    private static partial Regex CsProperty();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|static|final|abstract|sealed|non-sealed|strictfp|case)\s+)*(?<kind>class|interface|enum|record|@interface|trait|object)\s+(?<name>\w+)")]
    private static partial Regex JavaType();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|static|final|abstract|synchronized|native|default|override)\s+)+(?:<[^>]*>\s*)?(?:[\w<>\[\],.? ]+?\s+)?(?<name>\w+)\s*\([^;]*$")]
    private static partial Regex JavaMethod();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|open|abstract|sealed|data|enum|inner|value|annotation|companion)\s+)*(?<kind>class|interface|object)\s+(?<name>\w+)")]
    private static partial Regex KotlinType();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|internal|open|override|suspend|inline|operator|infix|tailrec|abstract)\s+)*fun\s+(?:<[^>]*>\s*)?(?:[\w.]+\.)?(?<name>\w+)\s*\(")]
    private static partial Regex KotlinFun();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:declare\s+)?(?:abstract\s+)?(?<kind>class|interface|enum|type|namespace|module)\s+(?<name>[\w$]+)\b(?!\s*[:.])")]
    private static partial Regex JsType();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:default\s+)?(?:async\s+)?function\s*\*?\s*(?<name>[\w$]+)\s*[(<]")]
    private static partial Regex JsFunction();

    [GeneratedRegex(@"^\s*(?:export\s+)?(?:const|let|var)\s+(?<name>[\w$]+)\s*(?::[^=]+)?=\s*(?:async\s+)?(?:function\b|\([^)]*\)\s*(?::[^=]+)?=>|[\w$]+\s*=>)")]
    private static partial Regex JsArrow();

    [GeneratedRegex(@"^\s+(?:(?:public|private|protected|static|async|readonly|override|abstract|get|set)\s+)*(?<name>[\w$]+)\s*(?:<[^>]*>)?\([^)]*\)\s*(?::\s*[^{;=]+)?\{\s*$")]
    private static partial Regex JsMethod();

    [GeneratedRegex(@"^\s*class\s+(?<name>\w+)")]
    private static partial Regex PyClass();

    [GeneratedRegex(@"^\s*(?:async\s+)?def\s+(?<name>\w+)")]
    private static partial Regex PyDef();

    [GeneratedRegex(@"^type\s+(?<name>\w+)\s+(?<kind>struct|interface)")]
    private static partial Regex GoType();

    [GeneratedRegex(@"^func\s+(?:\([^)]*\)\s*)?(?<name>\w+)")]
    private static partial Regex GoFunc();

    [GeneratedRegex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?<kind>struct|enum|trait|mod|union)\s+(?<name>\w+)")]
    private static partial Regex RustType();

    [GeneratedRegex(@"^\s*impl(?:<[^>]*>)?\s+(?:[\w:<>]+\s+for\s+)?(?<name>[\w:]+)")]
    private static partial Regex RustImpl();

    [GeneratedRegex(@"^\s*(?:pub(?:\([^)]*\))?\s+)?(?:const\s+)?(?:async\s+)?(?:unsafe\s+)?(?:extern\s+""[^""]*""\s+)?fn\s+(?<name>\w+)")]
    private static partial Regex RustFn();

    [GeneratedRegex(@"^\s*(?:template\s*<[^>]*>\s*)?(?:typedef\s+)?(?<kind>class|struct|union|enum(?:\s+class)?|namespace)\s+(?:[A-Z_][A-Z0-9_]*\s+)*(?<name>\w+)\s*(?:final\s*)?(?::(?!:)|\{|$)")]
    private static partial Regex CType();

    [GeneratedRegex(@"^(?:[\w:<>,*&~]+\s+)+\**&?(?<name>[\w:~]+)\s*\((?!.*;\s*$)[^;]*$")]
    private static partial Regex CFunction();

    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|open|fileprivate|final|indirect)\s+)*(?<kind>class|struct|enum|protocol|extension|actor)\s+(?<name>\w+)")]
    private static partial Regex SwiftType();

    [GeneratedRegex(@"^\s*(?:(?:public|private|internal|open|fileprivate|static|class|final|override|mutating|@\w+)\s+)*func\s+(?<name>\w+)")]
    private static partial Regex SwiftFunc();

    [GeneratedRegex(@"^\s*(?:abstract\s+)?(?<kind>class|mixin|enum|extension)\s+(?<name>\w+)")]
    private static partial Regex DartType();

    [GeneratedRegex(@"^\s*(?:(?:abstract|final|readonly)\s+)*(?<kind>class|interface|trait|enum)\s+(?<name>\w+)")]
    private static partial Regex PhpType();

    [GeneratedRegex(@"^\s*(?:(?:public|private|protected|static|abstract|final)\s+)*function\s+&?(?<name>\w+)")]
    private static partial Regex PhpFunction();

    [GeneratedRegex(@"^\s*(?<kind>class|module)\s+(?<name>[\w:]+)")]
    private static partial Regex RubyType();

    [GeneratedRegex(@"^\s*def\s+(?:self\.)?(?<name>\w+[?!=]?)")]
    private static partial Regex RubyDef();

    [GeneratedRegex(@"^\s*(?:local\s+)?function\s+(?<name>[\w.:]+)")]
    private static partial Regex LuaFunction();

    [GeneratedRegex(@"^\s*(?:local\s+)?(?<name>[\w.]+)\s*=\s*function\b")]
    private static partial Regex LuaAssigned();

    [GeneratedRegex(@"^(?<kind>class_name|class)\s+(?<name>\w+)")]
    private static partial Regex GdClass();

    [GeneratedRegex(@"^\s*(?:static\s+)?func\s+(?<name>\w+)")]
    private static partial Regex GdFunc();

    [GeneratedRegex(@"^signal\s+(?<name>\w+)")]
    private static partial Regex GdSignal();

    [GeneratedRegex(@"^\s*class\s+(?<name>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex PsClass();

    [GeneratedRegex(@"^\s*(?:function|filter)\s+(?:global:|script:)?(?<name>[\w-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PsFunction();

    [GeneratedRegex(@"^\s*(?:function\s+(?<name>[\w-]+)|(?<name>[\w-]+)\s*\(\)\s*\{?)")]
    private static partial Regex ShFunction();

    [GeneratedRegex(@"^\s*create\s+(?:or\s+replace\s+)?(?:temp(?:orary)?\s+)?(?<kind>table|view|function|procedure|index|trigger|type)\s+(?:if\s+not\s+exists\s+)?(?<name>[\w.\[\]""`]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SqlCreate();

    [GeneratedRegex(@"\bx:Name=""(?<name>\w+)""")]
    private static partial Regex XamlName();

    [GeneratedRegex(@"^(?<kind>#{1,6})\s+(?<name>.+?)\s*#*\s*$")]
    private static partial Regex MdHeading();

    [GeneratedRegex(@"^\s*(?:type|and)\s+(?:private\s+)?(?<name>\w+)")]
    private static partial Regex FsType();

    [GeneratedRegex(@"^\s*(?:let|member)\s+(?:rec\s+|inline\s+|private\s+|this\.|_\.|x\.)*(?<name>\w+)\s*(?:\(|\w)")]
    private static partial Regex FsLet();

    [GeneratedRegex(@"^\s*(?:(?:Public|Private|Protected|Friend|Shared|Overrides|Overridable|MustOverride|Partial|NotInheritable|MustInherit)\s+)*(?<kind>Class|Module|Structure|Interface|Enum|Sub|Function|Property)\s+(?<name>\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex VbDefinition();
}
