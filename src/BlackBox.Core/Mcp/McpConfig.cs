using System.Text.Json;
using System.Text.Json.Nodes;

namespace BlackBox.Core.Mcp;

public enum McpTransport
{
    /// <summary>A local program started by BlackBox, talking over its standard input and output.</summary>
    Stdio,
    /// <summary>A URL; Streamable HTTP, falling back to the older SSE protocol if the server only speaks that.</summary>
    Http,
    StreamableHttp,
    Sse,
}

/// <summary>One entry of the <c>mcpServers</c> map.</summary>
public sealed class McpServerConfig
{
    public required string Name { get; init; }
    public McpTransport Transport { get; init; }
    public bool Disabled { get; init; }

    // stdio
    public string? Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
    public string? Cwd { get; init; }

    // http
    public string? Url { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public bool IsStdio => Transport == McpTransport.Stdio;
}

public sealed class McpConfigException(string message) : Exception(message);

/// <summary>
/// The MCP server list, in the file format every MCP README shows (Claude Desktop's
/// <c>claude_desktop_config.json</c>, LM Studio's and Cursor's <c>mcp.json</c>):
/// <code>
/// {
///   "mcpServers": {
///     "files":  { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "C:\\Docs"] },
///     "remote": { "url": "https://example.com/mcp", "headers": { "Authorization": "Bearer …" } }
///   }
/// }
/// </code>
/// Kept in <c>Data/mcp.json</c> next to the app. Comments and trailing commas are tolerated, and
/// the file is stored as the user typed it.
/// </summary>
public sealed class McpConfig
{
    public const string Template = "{\n  \"mcpServers\": {\n  }\n}\n";

    public IReadOnlyList<McpServerConfig> Servers { get; private init; } = [];

    public static McpConfig Empty { get; } = new();

    /// <summary>The file's text, or the empty template if there is no file yet.</summary>
    public static string Load(string path) => File.Exists(path) ? File.ReadAllText(path) : Template;

    public static void Save(string path, string text) => Settings.AtomicFile.Write(path, text);

    /// <summary>The file's text with this server in it, in place of one of the same name; the rest stays, its comments aside.</summary>
    public static string WithServer(string json, string name, JsonObject entry)
    {
        var (root, servers) = ServerMap(json);
        var existing = servers.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) servers.Remove(existing);
        servers[name] = entry;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>The file's text without the server of that name; null when it has none.</summary>
    public static string? WithoutServer(string json, string name)
    {
        var (root, servers) = ServerMap(json);
        var existing = servers.Select(kv => kv.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return null;
        servers.Remove(existing);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    /// <summary>
    /// The user's edit of the file (<paramref name="mine"/>, begun from <paramref name="original"/>)
    /// with what changed in the file since carried over (<paramref name="current"/>: a server the
    /// agent added, replaced or removed meanwhile), unless the user changed that same server too.
    /// When nothing changed underneath, <paramref name="mine"/> comes back as typed.
    /// </summary>
    public static string Merge(string original, string current, string mine)
    {
        if (current == original)
            return mine;
        Dictionary<string, JsonNode?> before, now, edited;
        try
        {
            (before, now, edited) = (Entries(original), Entries(current), Entries(mine));
        }
        catch (McpConfigException)
        {
            return mine;
        }
        var text = mine;
        foreach (var name in before.Keys.Union(now.Keys, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var was = before.GetValueOrDefault(name);
            var isNow = now.GetValueOrDefault(name);
            if (JsonNode.DeepEquals(was, isNow) || !JsonNode.DeepEquals(was, edited.GetValueOrDefault(name)))
                continue;
            text = isNow is JsonObject entry ? WithServer(text, name, (JsonObject)entry.DeepClone()) : WithoutServer(text, name) ?? text;
        }
        return text;
    }

    private static Dictionary<string, JsonNode?> Entries(string json)
    {
        var entries = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entry) in ServerMap(json).Servers)
            entries.TryAdd(name, entry);
        return entries;
    }

    /// <summary>The parsed file and its map of servers, made when it has none.</summary>
    private static (JsonObject Root, JsonObject Servers) ServerMap(string json)
    {
        Parse(json); // says what is wrong with a file that can't be changed
        var root = string.IsNullOrWhiteSpace(json) ? new JsonObject()
            : JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject ?? new JsonObject();
        if ((root["mcpServers"] ?? root["servers"]) is JsonObject map)
            return (root, map);
        // Just the inner map, pasted on its own.
        if (root.Count > 0 && root.All(kv => kv.Value is JsonObject v && (v["command"] is not null || v["url"] is not null)))
            return (root, root);
        var servers = new JsonObject();
        root["mcpServers"] = servers;
        return (root, servers);
    }

    /// <summary>Parses the file text; a readable <see cref="McpConfigException"/> says what is wrong with it.</summary>
    public static McpConfig Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Empty;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new McpConfigException("This is not valid JSON: " + ex.Message);
        }
        if (root is not JsonObject obj)
            throw new McpConfigException("The file must be a JSON object with an \"mcpServers\" entry.");

        var map = obj["mcpServers"] ?? obj["servers"];
        if (map is null)
        {
            // Just the inner map pasted ({"name": {"command": ...}}) is accepted too.
            if (obj.Count > 0 && obj.All(kv => kv.Value is JsonObject v && (v["command"] is not null || v["url"] is not null)))
                map = obj;
            else if (obj.Count == 0)
                return Empty;
            else
                throw new McpConfigException("No \"mcpServers\" entry found. The file should look like {\"mcpServers\": {\"name\": {\"command\": ...}}}.");
        }
        if (map is not JsonObject servers)
            throw new McpConfigException("\"mcpServers\" must be an object with one entry per server.");

        var list = new List<McpServerConfig>();
        foreach (var (name, node) in servers)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new McpConfigException("A server has an empty name.");
            if (node is not JsonObject s)
                throw new McpConfigException($"Server \"{name}\" must be an object with \"command\" or \"url\".");
            list.Add(ParseServer(name, s));
        }
        return new McpConfig { Servers = list };
    }

    private static McpServerConfig ParseServer(string name, JsonObject s)
    {
        var command = Str(s, "command", name);
        var url = Str(s, "url", name);
        var type = (Str(s, "type", name) ?? Str(s, "transport", name))?.Trim().ToLowerInvariant();
        var disabled = Flag(s, "disabled", name) || (s["enabled"] is not null && !Flag(s, "enabled", name));

        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url))
            throw new McpConfigException($"Server \"{name}\" needs either \"command\" (a program to start) or \"url\" (a server to connect to).");
        if (!string.IsNullOrWhiteSpace(command) && !string.IsNullOrWhiteSpace(url))
            throw new McpConfigException($"Server \"{name}\" has both \"command\" and \"url\"; keep one.");

        var transport = type switch
        {
            null or "" => url is not null ? McpTransport.Http : McpTransport.Stdio,
            "stdio" => McpTransport.Stdio,
            "http" or "streamable-http" or "streamable_http" or "streamablehttp" or "streamable" => McpTransport.StreamableHttp,
            "sse" => McpTransport.Sse,
            _ => throw new McpConfigException($"Server \"{name}\": unknown type \"{type}\". Use \"stdio\", \"http\" or \"sse\", or leave it out."),
        };
        if (transport == McpTransport.Stdio && command is null)
            throw new McpConfigException($"Server \"{name}\" is of type stdio but has no \"command\".");
        if (transport != McpTransport.Stdio && url is null)
            throw new McpConfigException($"Server \"{name}\" is of type {type} but has no \"url\".");
        if (url is not null && (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new McpConfigException($"Server \"{name}\": \"url\" must be a full http:// or https:// address.");

        return new McpServerConfig
        {
            Name = name.Trim(),
            Transport = transport,
            Disabled = disabled,
            Command = command?.Trim(),
            Args = StrList(s, "args", name),
            Env = StrMap(s, "env", name),
            Cwd = Str(s, "cwd", name)?.Trim(),
            Url = url?.Trim(),
            Headers = StrMap(s, "headers", name),
        };
    }

    private static string? Str(JsonObject s, string key, string server) => s[key] switch
    {
        null => null,
        JsonValue v when v.TryGetValue<string>(out var text) => text,
        JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
        _ => throw new McpConfigException($"Server \"{server}\": \"{key}\" must be a string."),
    };

    private static bool Flag(JsonObject s, string key, string server) => s[key] switch
    {
        null => false,
        JsonValue v when v.GetValueKind() == JsonValueKind.True => true,
        JsonValue v when v.GetValueKind() == JsonValueKind.False => false,
        JsonValue v when v.TryGetValue<string>(out var text) => text.Trim().ToLowerInvariant() is "true" or "yes" or "1",
        _ => throw new McpConfigException($"Server \"{server}\": \"{key}\" must be true or false."),
    };

    private static List<string> StrList(JsonObject s, string key, string server)
    {
        switch (s[key])
        {
            case null:
                return [];
            case JsonArray a:
                var list = new List<string>();
                foreach (var item in a)
                {
                    if (item is JsonValue v && v.TryGetValue<string>(out var text)) list.Add(text);
                    else if (item is JsonValue n && n.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) list.Add(n.ToJsonString());
                    else throw new McpConfigException($"Server \"{server}\": every entry of \"{key}\" must be a string.");
                }
                return list;
            case JsonValue single when single.TryGetValue<string>(out var one):
                // A single string instead of a list: split on spaces, like a shell would.
                return one.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            default:
                throw new McpConfigException($"Server \"{server}\": \"{key}\" must be a list of strings.");
        }
    }

    private static Dictionary<string, string> StrMap(JsonObject s, string key, string server)
    {
        var map = new Dictionary<string, string>();
        switch (s[key])
        {
            case null:
                break;
            case JsonObject o:
                foreach (var (k, _) in o)
                    map[k] = Str(o, k, server) ?? "";
                break;
            default:
                throw new McpConfigException($"Server \"{server}\": \"{key}\" must be an object of name/value strings.");
        }
        return map;
    }
}
