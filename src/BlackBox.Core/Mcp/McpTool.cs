using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BlackBox.Core.Tools;
using ModelContextProtocol.Protocol;

namespace BlackBox.Core.Mcp;

/// <summary>
/// A tool of an MCP server, offered to the model next to BlackBox's own tools. The model sees it
/// as <c>server__tool</c>, so two servers with a <c>search</c> tool, or a file server's
/// <c>read_file</c> next to BlackBox's own, don't collide.
/// </summary>
public sealed partial class McpTool : Tools.Tool
{
    public delegate Task<CallToolResult> CallHandler(IReadOnlyDictionary<string, object?> arguments, CancellationToken ct);

    /// <summary>Longest a single call may take; the user's Stop ends it earlier.</summary>
    internal static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Same cap as read_file: more would flood the context.</summary>
    internal const int MaxTextChars = 60_000;

    /// <summary>OpenAI's limit for function names; local servers don't care, hosted APIs do.</summary>
    internal const int MaxNameLength = 64;

    internal const string Separator = "__";

    private readonly ModelContextProtocol.Protocol.Tool _tool;
    private readonly CallHandler _call;

    public McpTool(string serverName, string exposedName, ModelContextProtocol.Protocol.Tool tool, CallHandler call)
    {
        ServerName = serverName;
        Name = exposedName;
        _tool = tool;
        _call = call;
        Parameters = NormalizeSchema(tool.InputSchema);
        var description = string.IsNullOrWhiteSpace(tool.Description) ? tool.Title ?? "" : tool.Description.Trim();
        Description = description.Length > 0 ? description : $"Tool \"{tool.Name}\" of the MCP server \"{serverName}\".";
    }

    public string ServerName { get; }

    /// <summary>The name the server itself uses.</summary>
    public string OriginalName => _tool.Name;

    public override string Name { get; }
    public override string Description { get; }

    /// <summary>Servers can mark tools as read-only; everything else may change things, so it counts as risky.</summary>
    public override ToolRisk Risk => _tool.Annotations?.ReadOnlyHint == true ? ToolRisk.ReadOnly : ToolRisk.Risky;

    public override JsonObject Parameters { get; }

    public override string Describe(JsonObject args) =>
        $"{ServerName}: {OriginalName}" + (args.Count == 0 ? "" : " " + args.ToJsonString().Clip(200));

    public override async Task<ToolResult> ExecuteAsync(JsonObject args, ToolContext ctx, CancellationToken ct)
    {
        // JsonElement values: the SDK sends them as they are, whatever JSON the model wrote.
        var arguments = args.ToDictionary(kv => kv.Key, kv => (object?)JsonElement.Parse(kv.Value?.ToJsonString() ?? "null"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            var result = await _call(arguments, timeout.Token).ConfigureAwait(false);
            return ToToolResult(result);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolResult.Error($"The MCP server \"{ServerName}\" did not finish {OriginalName} within {CallTimeout.TotalMinutes:0} minutes.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A crashed or disconnected server must not end the run; the model is told and can go on another way.
            return ToolResult.Error($"The MCP server \"{ServerName}\" failed to run {OriginalName}: {ex.Message}");
        }
    }

    /// <summary>
    /// The name shown to the model: <c>server__tool</c>, with only the characters function names
    /// allow, and <paramref name="suffix"/> (a number that tells apart two names that came out the
    /// same) after the cut to <see cref="MaxNameLength"/>: cut off with the rest, it was lost, and
    /// naming two long tools that clashed went round for ever.
    /// </summary>
    public static string ExposedName(string serverName, string toolName, string suffix = "")
    {
        var name = Prefix(serverName) + Clean(toolName);
        var room = MaxNameLength - suffix.Length;
        return (name.Length <= room ? name : name[..room].TrimEnd('_', '-')) + suffix;
    }

    /// <summary>What all of a server's tool names start with: <c>server__</c>.</summary>
    public static string Prefix(string serverName) => Clean(serverName) + Separator;

    private static string Clean(string s)
    {
        var cleaned = Unsafe().Replace(s.Trim(), "_").Trim('_');
        return cleaned.Length > 0 ? cleaned : "x";
    }

    /// <summary>
    /// The server's input schema as the request wants it: an object with a <c>properties</c> map
    /// (the argument repair relies on it) and without the <c>$schema</c> field strict servers reject.
    /// </summary>
    internal static JsonObject NormalizeSchema(JsonElement schema)
    {
        var obj = schema.ValueKind == JsonValueKind.Object ? JsonNode.Parse(schema.GetRawText()) as JsonObject : null;
        obj ??= [];
        obj["type"] = "object";
        if (obj["properties"] is not JsonObject)
            obj["properties"] = new JsonObject();
        if (obj["required"] is not null && obj["required"] is not JsonArray)
            obj.Remove("required");
        obj.Remove("$schema");
        FlattenAlternatives(obj);
        return obj;
    }

    /// <summary>
    /// A schema whose top is anyOf, oneOf or allOf (as zod writes one for a union of argument
    /// sets) made one object: Anthropic refuses such a tool outright ("input_schema does not
    /// support oneOf, allOf, or anyOf at the top level"), and every request with it failed. The
    /// alternatives' properties are put together; required are those all of anyOf's or oneOf's
    /// require, and every one of allOf's.
    /// </summary>
    private static void FlattenAlternatives(JsonObject schema)
    {
        foreach (var key in new[] { "allOf", "anyOf", "oneOf" })
        {
            if (schema[key] is not JsonArray alternatives)
                continue;
            schema.Remove(key);
            var properties = (JsonObject)schema["properties"]!;
            var requiredBy = new List<HashSet<string>>();
            foreach (var alternative in alternatives.OfType<JsonObject>())
            {
                if (alternative["properties"] is JsonObject more)
                    foreach (var (name, property) in more)
                        if (!properties.ContainsKey(name))
                            properties[name] = property?.DeepClone();
                requiredBy.Add(Names(alternative["required"]));
            }
            var required = Names(schema["required"]);
            if (requiredBy.Count > 0)
                required.UnionWith(key == "allOf" ? requiredBy.SelectMany(r => r) : requiredBy.Skip(1).Aggregate(requiredBy[0], (all, r) => [.. all.Intersect(r)]));
            if (required.Count > 0)
                schema["required"] = new JsonArray([.. required.Select(r => JsonValue.Create(r))]);
        }

        static HashSet<string> Names(JsonNode? node) =>
            node is JsonArray names ? names.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToHashSet() : [];
    }

    /// <summary>Text blocks become the result text; the first image travels with it like a screenshot does.</summary>
    internal static ToolResult ToToolResult(CallToolResult result)
    {
        var sb = new StringBuilder();
        byte[]? image = null;
        var imageMime = "image/jpeg";
        var imagesLeftOut = 0;

        foreach (var block in result.Content ?? [])
        {
            switch (block)
            {
                case TextContentBlock t:
                    Line(t.Text);
                    break;
                case ImageContentBlock i:
                    if (!TakeImage(i.DecodedData, i.MimeType)) imagesLeftOut++;
                    break;
                case AudioContentBlock a:
                    Line($"(audio {a.MimeType} left out)");
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents tr }:
                    Line(tr.Text);
                    break;
                case EmbeddedResourceBlock { Resource: BlobResourceContents br }:
                    if (br.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true && TakeImage(br.DecodedData, br.MimeType))
                        break;
                    Line($"(binary resource {br.Uri}, {br.MimeType ?? "unknown type"}, {br.DecodedData.Length} bytes, left out)");
                    break;
                case ResourceLinkBlock l:
                    Line($"Resource: {l.Uri}" + (l.Name is { Length: > 0 } ? $" ({l.Name})" : "") + (l.Description is { Length: > 0 } ? $" - {l.Description}" : ""));
                    break;
                default:
                    Line($"({block.Type} content left out)");
                    break;
            }
        }

        if (sb.Length == 0 && result.StructuredContent is { } structured)
            sb.Append(structured.GetRawText());
        if (imagesLeftOut > 0)
            Line($"({imagesLeftOut} more image{(imagesLeftOut == 1 ? "" : "s")} left out)");

        var text = sb.ToString().TrimEnd();
        if (text.Length > MaxTextChars)
            text = text[..MaxTextChars] + "\n[output truncated]";
        if (text.Length == 0)
            text = image is null ? "(no output)" : "(image attached)";
        // Not called a screenshot: it is whatever the server made (a viewport render, a chart), and
        // a context reset put the last screenshot after its note as the screen.
        return new ToolResult(text, image, imageMime, IsError: result.IsError == true, ImageCaption: image is null ? null : "The picture the tool gave back:");

        void Line(string? s)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(s);
        }

        bool TakeImage(ReadOnlyMemory<byte> bytes, string? mime)
        {
            if (image is not null) return false;
            // Scaled and sent as JPEG, as a screenshot is: a server's image may be of any size (a
            // generated picture's full PNG) or kind (SVG, TIFF), and one the model's server can't
            // take fails every request after it, since the conversation keeps it.
            if (Tools.Picture.Load(bytes.ToArray()) is not { } shown)
            {
                Line($"(an image of type {mime ?? "unknown"} left out: it isn't a picture the model can be shown)");
                return true;
            }
            image = shown.Jpeg;
            imageMime = "image/jpeg";
            return true;
        }
    }

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex Unsafe();
}
