using System.Text.Json;
using System.Text.Json.Serialization;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;

namespace BlackBox.Core.Chats;

public enum ChatEntryKind { User, Assistant, Tool, Notice }

/// <summary>One line of the chat as the window shows it: a message, a tool call or a notice.</summary>
public sealed class ChatEntry
{
    public ChatEntryKind Kind { get; set; }
    public string? Text { get; set; }
    public string? Reasoning { get; set; }

    // Tool calls
    public string? CallId { get; set; }
    public string? Name { get; set; }
    public string? Summary { get; set; }
    public string? State { get; set; }

    public bool IsError { get; set; }
}

/// <summary>
/// A chat saved in Data/chats: what the window showed, and the conversation the model had, so it
/// can be picked up again. Screenshots are not kept: they would make every file megabytes long,
/// and the model takes a new one when it carries on.
/// </summary>
public sealed class SavedChat
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Updated { get; set; }
    public List<ChatEntry> Entries { get; set; } = [];
    public List<ChatMessage> History { get; set; } = [];
}

/// <summary>What the history dropdown lists: the start of each file, without its contents.</summary>
public sealed class ChatHeader
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Updated { get; set; }
}

/// <summary>Every chat is its own file, Data/chats/&lt;id&gt;.json, written whenever the chat changes.</summary>
public sealed class ChatStore(string dir)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Folder { get; } = dir;

    public static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];

    /// <summary>The first line of the first message, cut to fit the dropdown.</summary>
    public static string TitleFrom(string firstMessage)
    {
        var line = firstMessage.Trim().Split('\n')[0].Trim();
        return line.Length <= 80 ? line : line[..77].TrimEnd() + "…";
    }

    /// <summary>Saved chats, the most recently changed first. Files that don't parse are left out.</summary>
    public List<ChatHeader> List()
    {
        if (!Directory.Exists(Folder))
            return [];
        var headers = new List<ChatHeader>();
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                using var stream = File.OpenRead(file);
                if (JsonSerializer.Deserialize<ChatHeader>(stream, JsonOptions) is { } header)
                {
                    header.Id = Path.GetFileNameWithoutExtension(file);
                    headers.Add(header);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return headers.OrderByDescending(h => h.Updated).ToList();
    }

    public SavedChat? Load(string id)
    {
        try
        {
            var chat = JsonSerializer.Deserialize<SavedChat>(File.ReadAllText(PathOf(id)), JsonOptions);
            if (chat is not null) chat.Id = id;
            return chat;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the chat; through a temporary file, so a crash mid-write leaves the previous version.</summary>
    public void Save(SavedChat chat)
    {
        Directory.CreateDirectory(Folder);
        var path = PathOf(chat.Id);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(chat, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public void Delete(string id)
    {
        try { File.Delete(PathOf(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string PathOf(string id) => Path.Combine(Folder, Path.GetFileName(id) + ".json");

    /// <summary>
    /// A copy of the model's conversation fit for saving: screenshots replaced by a note, and tool
    /// calls a stop left unanswered given placeholder results, so carrying on is a valid request.
    /// </summary>
    public static List<ChatMessage> ForSaving(IEnumerable<ChatMessage> history)
    {
        var copy = history.Select(m => new ChatMessage
        {
            Role = m.Role,
            Content = m.Parts is null
                ? m.Content
                : string.Join("\n", m.Parts.Where(p => !p.IsImage).Select(p => p.Text)) + (m.Parts.Any(p => p.IsImage) ? "\n(Screenshot not kept in the saved chat.)" : ""),
            Reasoning = m.Reasoning,
            ToolCalls = m.ToolCalls?.Select(c => new ToolCall { Id = c.Id, Name = c.Name, Arguments = c.Arguments }).ToList(),
            ToolCallId = m.ToolCallId,
            FromUser = m.FromUser,
        }).ToList();
        AgentSession.RepairDanglingToolCalls(copy);
        return copy;
    }
}
