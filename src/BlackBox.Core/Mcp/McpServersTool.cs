using System.Text;
using System.Text.Json.Nodes;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Mcp;

/// <summary>What the agent needs to set up MCP servers itself: the running servers, their file, and the run's tool list.</summary>
public sealed class McpSetup(McpHost host, string configFile)
{
    public McpHost Host { get; } = host;

    /// <summary>Data/mcp.json, where a server the agent adds is kept for later tasks and shows in Settings.</summary>
    public string ConfigFile { get; } = configFile;

    /// <summary>The run's tools, which a new server's tools join.</summary>
    public ToolRegistry? Registry { get; set; }

    /// <summary>
    /// Called when a server's tools joined or left the run: the system prompt, which lists the
    /// servers, is made again then. Made at the next message instead, it changed the start of the
    /// prompt a second time, and the server read the whole chat again once more.
    /// </summary>
    public Action? ToolsChanged { get; set; }
}

/// <summary>
/// The agent adds, removes and lists its own MCP servers: the ones the user sees and edits in Settings.
/// A program with an MCP server (Blender, a database, a browser) is far better driven through it
/// than through its window; before, the agent could only write Data/mcp.json and the servers in it
/// started with the app, so a server it set up was there in the next run at the earliest. Now a
/// server it adds starts at once, its tools join the run, and it is kept for later tasks.
/// </summary>
public sealed class McpServersTool : Tool
{
    /// <summary>A first start can mean uvx or npx downloading the server and a runtime for it.</summary>
    internal static readonly TimeSpan DefaultStartTimeout = TimeSpan.FromMinutes(5);

    public override string Name => "mcp_servers";

    public override string Description =>
        "Add, remove or list your MCP servers, the ones in the user's Settings. A program that has an MCP server (Blender, a database, a browser…) " +
        "is far better driven through it than through its window, and this is the way to use one: action=add starts the server, saves it in " +
        "the user's Settings for later tasks and gives you its tools at once (named name__tool). Give name and either command with args " +
        "(a program to start; best its full path, e.g. C:\\...\\uvx.exe with args [\"some-mcp-server\"]; env and cwd if it needs them) or " +
        "url (a server to connect to). A server that fails to start isn't kept: the result says why. action=remove stops one and takes " +
        "it out of Settings; action=list shows them all.";

    public override ToolRisk Risk => ToolRisk.Risky;

    public override JsonObject Parameters => Schema(
        ("action", Str("What to do", "add", "remove", "list"), true),
        ("name", Str("The server's name; its tools are called name__tool. Short, such as \"blender\""), false),
        ("command", Str("For a server you start: the program, best with its full path"), false),
        ("args", StrArray("The program's arguments"), false),
        ("env", new JsonObject { ["type"] = "object", ["description"] = "Environment variables it needs, name to value", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }, false),
        ("cwd", Str("The folder to start it in"), false),
        ("url", Str("For a server to connect to: its http(s) address"), false),
        ("headers", new JsonObject { ["type"] = "object", ["description"] = "HTTP headers for url, such as Authorization", ["additionalProperties"] = new JsonObject { ["type"] = "string" } }, false),
        ("timeout_seconds", Int("How long it may take to start (default 300: a first start may download it)"), false));

    public override string Describe(JsonObject args) => (GetString(args, "action") ?? "") switch
    {
        "add" => $"Add MCP server {GetString(args, "name")}: {GetString(args, "url") ?? string.Join(" ", new[] { GetString(args, "command") }.Concat(GetStringList(args, "args") ?? []))}",
        "remove" => $"Remove MCP server {GetString(args, "name")}",
        _ => "List MCP servers",
    };

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        if (ctx.Mcp is not { } mcp)
            return ToolResult.Error("MCP servers can't be set up in this run.");
        switch (RequireString(args, "action").Trim().ToLowerInvariant())
        {
            case "list":
                return new ToolResult(List(mcp));
            case "remove":
            {
                var name = RequireString(args, "name").Trim();
                var stopped = await mcp.Host.RemoveAsync(name).ConfigureAwait(false);
                var text = McpConfig.WithoutServer(McpConfig.Load(mcp.ConfigFile), name);
                if (text is not null) McpConfig.Save(mcp.ConfigFile, text);
                var dropped = mcp.Registry?.RemoveServer(name) == true;
                if (stopped || dropped)
                    mcp.ToolsChanged?.Invoke();
                return stopped || text is not null ? new ToolResult($"Removed the MCP server \"{name}\".") : ToolResult.Error($"There is no MCP server \"{name}\". {List(mcp)}");
            }
            case "add":
                return await AddAsync(args, mcp, ct).ConfigureAwait(false);
            default:
                throw new ToolArgumentException("action must be add, remove or list.");
        }
    }

    private static async Task<ToolResult> AddAsync(JsonObject args, McpSetup mcp, CancellationToken ct)
    {
        var name = RequireString(args, "name").Trim();
        // The entry as it goes in mcp.json, checked the way the file is.
        var entry = new JsonObject();
        if (GetString(args, "command") is { Length: > 0 } command) entry["command"] = command.Trim();
        if (GetStringList(args, "args") is { Count: > 0 } list) entry["args"] = new JsonArray(list.Select(a => (JsonNode)a).ToArray());
        if (args["env"] is JsonObject env && env.Count > 0) entry["env"] = env.DeepClone();
        if (GetString(args, "cwd") is { Length: > 0 } cwd) entry["cwd"] = cwd.Trim();
        if (GetString(args, "url") is { Length: > 0 } url) entry["url"] = url.Trim();
        if (args["headers"] is JsonObject headers && headers.Count > 0) entry["headers"] = headers.DeepClone();
        McpServerConfig config;
        try
        {
            config = McpConfig.Parse(new JsonObject { ["mcpServers"] = new JsonObject { [name] = entry.DeepClone() } }.ToJsonString()).Servers.Single();
        }
        catch (McpConfigException ex)
        {
            return ToolResult.Error(ex.Message);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(GetNumber(args, "timeout_seconds") ?? DefaultStartTimeout.TotalSeconds, 10, 900));
        var (status, tools) = await mcp.Host.AddAsync(config, timeout, ct).ConfigureAwait(false);
        if (status.State != McpServerState.Connected)
        {
            // One of that name that ran before was stopped for it: its tools go too, or calls to
            // them went to a server no longer there.
            if (mcp.Registry?.RemoveServer(name) == true)
                mcp.ToolsChanged?.Invoke();
            return ToolResult.Error($"The MCP server \"{name}\" did not start, so it wasn't kept: {status.Error} " +
                                    "Check the command and its arguments in the server's instructions; run it with run_shell to see what it says.");
        }

        // Its tools join the run first: when saving failed (another entry in mcp.json the file
        // check refuses, a file held open), the result said they were usable, and every call to
        // them was an unknown tool.
        mcp.Registry?.RemoveServer(name);
        mcp.Registry?.Add(tools);
        mcp.ToolsChanged?.Invoke();
        try
        {
            McpConfig.Save(mcp.ConfigFile, McpConfig.WithServer(McpConfig.Load(mcp.ConfigFile), name, entry));
        }
        catch (Exception ex) when (ex is McpConfigException or IOException or UnauthorizedAccessException)
        {
            return new ToolResult(Started(name, status, tools, saved: false) + $"\nIt could not be saved in Settings ({ex.Message}), so it runs only until BlackBox closes.");
        }
        return new ToolResult(Started(name, status, tools, saved: true));
    }

    private static string Started(string name, McpServerStatus status, IReadOnlyList<McpTool> tools, bool saved)
    {
        var sb = new StringBuilder($"The MCP server \"{name}\" runs" + (saved ? " and is saved in the user's Settings for later tasks. " : ". "));
        sb.Append(tools.Count == 0 ? "It offers no tools." : $"Its {tools.Count} tool(s) are yours now: ");
        sb.AppendLine(string.Join(", ", tools.Select(t => t.Name)));
        foreach (var t in tools.Take(40))
            sb.Append("- ").Append(t.Name).Append(": ").AppendLine(t.Description.Split('\n')[0].Clip(160));
        if (status.Instructions is { Length: > 0 } instructions)
            sb.AppendLine("Its instructions: " + instructions.Clip(2000));
        return sb.ToString().TrimEnd();
    }

    private static string List(McpSetup mcp)
    {
        var statuses = mcp.Host.Statuses;
        return statuses.Count == 0 ? "No MCP servers are set up." : "MCP servers:\n" + string.Join("\n", statuses.Select(s => "- " + s.Summary));
    }
}
