using BlackBox.Core.Llm;

namespace BlackBox.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(IEnumerable<Tool> tools)
    {
        foreach (var t in tools)
            _tools[t.Name] = t;
    }

    /// <param name="extra">Tools from outside BlackBox (MCP servers), offered after its own.</param>
    public static ToolRegistry CreateDefault(bool includeScreenTools, IEnumerable<Tool>? extra = null, bool includeWebTools = true) =>
        new(DefaultTools(includeScreenTools, includeWebTools).Concat(extra ?? []));

    private static IEnumerable<Tool> DefaultTools(bool includeScreenTools, bool includeWebTools)
    {
        if (includeScreenTools)
        {
            yield return new ScreenshotTool();
            yield return new MouseClickTool();
            yield return new MouseMoveTool();
            yield return new MouseDragTool();
            yield return new ScrollTool();
            yield return new TypeTextTool();
            yield return new PressKeysTool();
            yield return new WaitTool();
        }
        yield return new ReadFileTool();
        yield return new WriteFileTool();
        yield return new EditFileTool();
        yield return new ListDirectoryTool();
        yield return new ShellTool();
        yield return new SshTool();
        yield return new CopyTool();
        if (includeWebTools)
        {
            yield return new WebSearchTool();
            yield return new FetchPageTool();
        }
    }

    public IReadOnlyCollection<Tool> All => _tools.Values;

    public Tool? Find(string name) => _tools.GetValueOrDefault(name);

    /// <summary>
    /// The one tool whose name ends in <c>__name</c>, for a model that calls an MCP tool by its
    /// bare name (<c>search</c> instead of <c>brave__search</c>). Null when there is none or more than one.
    /// </summary>
    public Tool? FindBySuffix(string name)
    {
        var wanted = Loose(name);
        Tool? found = null;
        foreach (var t in _tools.Values)
        {
            var at = t.Name.LastIndexOf("__", StringComparison.Ordinal);
            if (at < 0 || Loose(t.Name[(at + 2)..]) != wanted)
                continue;
            if (found is not null)
                return null;
            found = t;
        }
        return found;

        static string Loose(string s) => s.Trim().Replace('-', '_').ToLowerInvariant();
    }

    public IReadOnlyList<ToolDefinition> Definitions() =>
        _tools.Values.Select(t => new ToolDefinition { Name = t.Name, Description = t.Description, Parameters = t.Parameters }).ToList();
}
