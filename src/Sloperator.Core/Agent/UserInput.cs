using System.Text;
using Sloperator.Core.Llm;
using Sloperator.Core.Media;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Agent;

/// <summary>
/// What the user sends: the text they typed and the files that came with it, dropped on the
/// window or pasted into the box (a picture pasted from the clipboard is saved as a file first).
/// </summary>
public sealed record UserInput(string Text, IReadOnlyList<string> Files)
{
    public UserInput(string text) : this(text, []) { }

    /// <summary>
    /// Where it was written, when not in the chat's own box ("Video generator · The Fox and the Moon ·
    /// clip 2"): shown over it in the chat. Null for a message typed in the chat.
    /// </summary>
    public string? Via { get; init; }

    /// <summary>
    /// What the model is told with it, before the user's words, and the chat doesn't show: where it
    /// was written and what it is about (the video and the piece picked in the Video generator panel).
    /// </summary>
    public string? Context { get; init; }

    /// <summary>
    /// What of the open side panel went with it, as cards like its files' in its bubble: what was picked
    /// there, or what it is about with nothing picked ("notes.md", "whole document"). Null for a
    /// message sent with no panel open.
    /// </summary>
    public IReadOnlyList<SidePanelCard>? Cards { get; init; }

    /// <summary>
    /// Which message of the user's this is, the same in the chat and in the model's conversation
    /// (<see cref="ChatMessage.Sent"/>), so Undo finds where the model had it (see <see cref="ChatCut"/>);
    /// several for messages sent together. Empty for one from a chat saved before messages had them.
    /// </summary>
    public IReadOnlyList<string> Ids { get; init; } = [NewId()];

    internal static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// Typed text as it is sent: without the empty lines and spaces around it, and nothing else
    /// changed. The empty lines inside stay, and so does the first line's indentation (code pasted
    /// in, a list), which a plain Trim took off.
    /// </summary>
    public static string Tidy(string text)
    {
        text = text.TrimEnd();
        var start = 0;
        for (var i = 0; i < text.Length && char.IsWhiteSpace(text[i]); i++)
            if (text[i] == '\n')
                start = i + 1;
        return text[start..];
    }

    /// <summary>The words as the model reads them: the context first, when there is one.</summary>
    internal string ModelText => Context is null ? Text : Text.Trim().Length == 0 ? Context : $"{Context}\n{Text}";

    /// <summary>How many attached pictures the model sees in one message; the others it opens with read_file.</summary>
    private const int MaxShown = 6;

    /// <summary>
    /// The message the model gets: the text, then each file's full path, for its tools to work on
    /// (a picture to start a ComfyUI job from, a document to read, a folder to list), and the
    /// pictures among them, shown as read_file shows a picture. A model that can't see images gets
    /// the paths alone.
    /// </summary>
    public ChatMessage ToMessage(bool seesImages)
    {
        if (Files.Count == 0)
            return new ChatMessage { Role = ChatRole.User, Content = ModelText, FromUser = true, Sent = [.. Ids] };
        var list = new StringBuilder(Tidy(ModelText));
        if (list.Length > 0)
            list.Append("\n\n");
        list.Append(Files.Count == 1 ? "[The user attached this file:]" : "[The user attached these files:]");
        var pictures = new List<ContentPart>();
        var shownCount = 0;
        foreach (var file in Files)
        {
            list.Append("\n- ").Append(file).Append(" (");
            if (Directory.Exists(file))
                list.Append("a folder");
            else if (!File.Exists(file))
                list.Append("no longer there");
            else if (seesImages && shownCount < MaxShown && Picture.Handles(file) && Picture.Load(file) is { } shown)
            {
                shownCount++;
                list.Append($"a {shown.Width}x{shown.Height} picture, shown below")
                    .Append(shown.ShownWidth == shown.Width && shown.ShownHeight == shown.Height ? "" : $" at {shown.ShownWidth}x{shown.ShownHeight}");
                pictures.Add(ContentPart.FromText(file + ":"));
                pictures.Add(ContentPart.FromImage(shown.Jpeg, "image/jpeg"));
            }
            else
                list.Append(Describe(file));
            list.Append(')');
        }
        return pictures.Count == 0
            ? new ChatMessage { Role = ChatRole.User, Content = list.ToString(), FromUser = true, Sent = [.. Ids] }
            : new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(list.ToString()), .. pictures], FromUser = true, Sent = [.. Ids] };
    }

    /// <summary>What a file is, for the list: "picture 1920x1080, 1.2 MB", "video, 14.0 MB", "a .pdf file, 220 KB".</summary>
    private static string Describe(string file)
    {
        string size;
        try { size = Strings.Bytes(new FileInfo(file).Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "can't be read: " + ex.Message; }
        var kind = MediaPreview.KindOf(file);
        if (kind is "image" or "animation")
            return MediaPreview.Look(file, null, makeTile: false).Line;
        if (kind is "video" or "audio" or "3d")
            return $"{(kind == "3d" ? "3D model" : kind == "audio" ? "sound" : kind)}, {size}";
        var extension = Path.GetExtension(file);
        return extension.Length > 1 ? $"a {extension.ToLowerInvariant()} file, {size}" : $"a file, {size}";
    }

    /// <summary>
    /// The pictures of the user's latest message in <paramref name="history"/> that came with any,
    /// read back from the list <see cref="ToMessage"/> writes ("- C:\x\a.png (a 2945x2160 picture,
    /// …)"): only files that are still there. A path can hold " (" itself, so the longest one that
    /// is a file is taken.
    /// </summary>
    public static IReadOnlyList<string> PicturesIn(IEnumerable<ChatMessage> history)
    {
        foreach (var m in history.Where(m => m.FromUser).Reverse())
        {
            var text = (m.Parts is { Count: > 0 } parts ? parts[0].Text : m.Content) ?? "";
            var list = text.IndexOf("[The user attached ", StringComparison.Ordinal);
            if (list < 0)
                continue;
            var pictures = new List<string>();
            foreach (var line in text[list..].Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal)))
            {
                var entry = line[2..];
                for (var at = entry.LastIndexOf(" (", StringComparison.Ordinal); at > 0; at = entry.LastIndexOf(" (", at - 1, StringComparison.Ordinal))
                    if (File.Exists(entry[..at]))
                    {
                        if (Picture.Handles(entry[..at]))
                            pictures.Add(entry[..at]);
                        break;
                    }
            }
            if (pictures.Count > 0)
                return pictures;
        }
        return [];
    }

    /// <summary>Several messages sent together, as one: their texts one after another (each with its context), and all their files.</summary>
    public static UserInput Join(IEnumerable<UserInput> inputs)
    {
        var all = inputs.ToList();
        if (all.Count == 1)
            return all[0];
        return new UserInput(string.Join("\n\n", all.Select(i => i.ModelText).Where(t => t.Length > 0)),
            all.SelectMany(i => i.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList()) { Ids = [.. all.SelectMany(i => i.Ids)] };
    }
}

/// <summary>
/// A card of what a message carried from a side panel: a pick (its file, and what it is there:
/// "Lines 12-18 · DiffTool.cs") or, with nothing picked, what the message is about there ("notes.md",
/// "whole document", with the icon's path data; null for a file's).
/// </summary>
public sealed record SidePanelCard(string Title, string? Details = null, string? Path = null, string? Icon = null);
