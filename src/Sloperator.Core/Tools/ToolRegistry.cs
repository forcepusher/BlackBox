using Sloperator.Core.Llm;

namespace Sloperator.Core.Tools;

public sealed class ToolRegistry
{
    private readonly Dictionary<string, Tool> _tools = new(StringComparer.OrdinalIgnoreCase);

    public ToolRegistry(IEnumerable<Tool> tools)
    {
        foreach (var t in tools)
            _tools[t.Name] = t;
    }

    /// <param name="extra">Tools from outside Sloperator (MCP servers), offered after its own.</param>
    /// <param name="includeIndex">Whether index_folder is offered: off only to measure what the index changes (sloperator-cli --no-index).</param>
    public static ToolRegistry CreateDefault(bool includeScreenTools, IEnumerable<Tool>? extra = null, bool includeWebTools = true, bool includeIndex = true) =>
        new(DefaultTools(includeScreenTools, includeWebTools, includeIndex).Concat(extra ?? []));

    private static IEnumerable<Tool> DefaultTools(bool includeScreenTools, bool includeWebTools, bool includeIndex)
    {
        if (includeScreenTools)
        {
            yield return new ScreenshotTool();
            yield return new ZoomTool();
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
        yield return new DiffTool();
        if (includeIndex)
            yield return new IndexFolderTool();
        yield return new ShellTool();
        yield return new SshTool();
        yield return new CopyTool();
        yield return new ComfyTool();
        yield return new VideoGeneratorTool();
        if (includeWebTools)
        {
            yield return new WebSearchTool();
            yield return new FetchPageTool();
        }
    }

    public IReadOnlyCollection<Tool> All => _tools.Values;

    /// <summary>
    /// Tools of an MCP server the agent set up during the run, offered from its next request on.
    /// The tool list changes then, so the server works through the whole prompt once again.
    /// </summary>
    public void Add(IEnumerable<Tool> tools)
    {
        foreach (var t in tools)
            _tools[t.Name] = t;
    }

    /// <summary>The tools of an MCP server that was removed; false when it had none here.</summary>
    /// <remarks>
    /// The rest are put back in order: tools added later would take the removed ones' places, and
    /// the list then differed from the next message's, which the server read whole once more.
    /// </remarks>
    public bool RemoveServer(string serverName)
    {
        var kept = _tools.Values.Where(t => t is not Mcp.McpTool m || !m.ServerName.Equals(serverName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (kept.Count == _tools.Count)
            return false;
        _tools.Clear();
        foreach (var t in kept)
            _tools[t.Name] = t;
        return true;
    }

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
