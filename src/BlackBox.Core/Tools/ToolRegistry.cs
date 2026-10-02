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

    public static ToolRegistry CreateDefault(bool includeScreenTools) => new(DefaultTools(includeScreenTools));

    public static IEnumerable<Tool> DefaultTools(bool includeScreenTools)
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
        yield return new ListWindowsTool();
        yield return new FocusWindowTool();
        yield return new ReadFileTool();
        yield return new WriteFileTool();
        yield return new EditFileTool();
        yield return new ListDirectoryTool();
        yield return new ShellTool();
    }

    public IReadOnlyCollection<Tool> All => _tools.Values;

    public Tool? Find(string name) => _tools.GetValueOrDefault(name);

    public IReadOnlyList<ToolDefinition> Definitions() =>
        _tools.Values.Select(t => new ToolDefinition { Name = t.Name, Description = t.Description, Parameters = t.Parameters }).ToList();
}
