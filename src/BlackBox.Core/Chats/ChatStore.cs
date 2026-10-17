using System.Text.Json;
using System.Text.Json.Serialization;
using BlackBox.Core.Agent;
using BlackBox.Core.Llm;
using BlackBox.Core.Settings;

namespace BlackBox.Core.Chats;

public enum ChatEntryKind { User, Assistant, Tool, Notice }

/// <summary>One line of the chat as the window shows it: a message, a tool call or a notice, with when it happened.</summary>
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

    /// <summary>The files a tool call made or showed, previewed under it; for a message of the user's, the files that came with it.</summary>
    public List<string>? Files { get; set; }

    public bool IsError { get; set; }

    /// <summary>For a notice: a run ended here before its task was done (stopped, or an error), so the chat offers to carry on.</summary>
    public bool Stopped { get; set; }

    /// <summary>For such a notice: the run ended before the model had the user's message, so carrying on sends it again.</summary>
    public bool Unsent { get; set; }

    /// <summary>When it happened: a message sent, a step or a tool call begun, a notice shown. Chats saved before the chat kept times have none.</summary>
    public DateTime? At { get; set; }

    /// <summary>For a reply or a tool call: when it ended, so the chat shows how long it took.</summary>
    public DateTime? Ended { get; set; }

    /// <summary>For a reply: when its request had all gone out to the server, and when the model's first words came.</summary>
    public DateTime? Sent { get; set; }

    /// <inheritdoc cref="Sent"/>
    public DateTime? FirstWords { get; set; }

    /// <summary>On the line a run ended with: how long the run took, from the message that started it.</summary>
    public TimeSpan? RunTook { get; set; }
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
    public string Folder { get; } = dir;

    // Invariant: a Thai Windows wrote the Buddhist year (25691017-…) and a Saudi one the Hijri date.
    public static string NewId() => DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];

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
            if (ReadHeader(file) is { } header)
                headers.Add(header);
        return headers.OrderByDescending(h => h.Updated).ToList();
    }

    /// <summary>
    /// The title and time from the start of the file, where <see cref="Save"/> puts them: a long
    /// run's chat is megabytes, and reading every file whole made the list slower with each chat.
    /// </summary>
    private static ChatHeader? ReadHeader(string file)
    {
        try
        {
            var head = new byte[4096];
            int length;
            using (var stream = File.OpenRead(file))
                length = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            var json = new Utf8JsonReader(head.AsSpan(0, length), isFinalBlock: length < head.Length, state: default);
            if (!json.Read() || json.TokenType != JsonTokenType.StartObject)
                return null;
            var header = new ChatHeader { Id = Path.GetFileNameWithoutExtension(file) };
            while (json.Read() && json.TokenType == JsonTokenType.PropertyName)
            {
                var name = json.GetString();
                if (name is not ("id" or "title" or "updated") || !json.Read())
                    break; // the contents follow
                if (name == "title") header.Title = json.GetString() ?? "";
                if (name == "updated") header.Updated = json.GetDateTime();
            }
            return header;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    public SavedChat? Load(string id)
    {
        try
        {
            var chat = JsonSerializer.Deserialize(File.ReadAllText(PathOf(id)), ChatJson.Default.SavedChat);
            if (chat is not null) chat.Id = id;
            return chat;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the chat; through a temporary file, so a crash mid-write leaves the previous version.</summary>
    public void Save(SavedChat chat) => AtomicFile.Write(PathOf(chat.Id), JsonSerializer.Serialize(chat, ChatJson.Default.SavedChat));

    public void Delete(string id)
    {
        try { File.Delete(PathOf(id)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string PathOf(string id) => Path.Combine(Folder, Path.GetFileName(id) + ".json");

    /// <summary>
    /// A copy of the model's conversation fit for saving: screenshots replaced by a note, and tool
    /// calls a stop left unanswered given placeholder results, so carrying on is a valid request.
    /// The pictures a user's message came with are left out too: their paths are in its text.
    /// </summary>
    public static List<ChatMessage> ForSaving(IEnumerable<ChatMessage> history)
    {
        var copy = history.Select(m => new ChatMessage
        {
            Role = m.Role,
            Content = m.Parts is null
                ? m.Content
                : m.FromUser
                    ? m.Parts[0].Text + "\n(The pictures aren't kept in the saved chat: read_file shows them again.)"
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

/// <summary>
/// Writes chats in the background, one at a time, the newest version of each: a long run's chat is
/// megabytes, and turning it into JSON and onto the disk at every step held the window still.
/// What is handed over must not change afterwards (the window hands over copies).
/// </summary>
public sealed class ChatSaver(ChatStore store)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, SavedChat> _pending = [];
    private bool _writing;
    private Task _writer = Task.CompletedTask;

    /// <summary>Writes the chat soon; a version still waiting for the same chat is replaced by it.</summary>
    public void Save(SavedChat chat)
    {
        lock (_gate)
        {
            _pending[chat.Id] = chat;
            if (_writing)
                return;
            _writing = true;
            _writer = Task.Run(WriteAll);
        }
    }

    /// <summary>Drops a version of the chat still waiting to be written (it is being deleted).</summary>
    public void Forget(string id)
    {
        lock (_gate)
            _pending.Remove(id);
    }

    /// <summary>Waits until what was handed over is on the disk, for up to <paramref name="timeout"/>; false when it isn't yet.</summary>
    public bool Flush(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            Task writer;
            lock (_gate)
            {
                if (!_writing)
                    return true;
                writer = _writer;
            }
            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero || !writer.Wait(left))
                return false;
        }
    }

    private void WriteAll()
    {
        while (true)
        {
            SavedChat chat;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _writing = false;
                    return;
                }
                var (id, next) = _pending.First();
                _pending.Remove(id);
                chat = next;
            }
            // A full or read-only disk: the chat goes on, unsaved. Whatever fails, the next ones are still written.
            try { store.Save(chat); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }
}

/// <summary>The saved chats' format, generated at build time so trimmed builds can read and write it without reflection.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, IgnoreReadOnlyProperties = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(SavedChat))]
internal sealed partial class ChatJson : JsonSerializerContext;
