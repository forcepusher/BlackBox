using System.Text;
using BlackBox.Core.Llm;
using BlackBox.Core.Media;
using BlackBox.Core.Tools;

namespace BlackBox.Core.Agent;

/// <summary>
/// What the user sends: the text they typed and the files that came with it, dropped on the
/// window or pasted into the box (a picture pasted from the clipboard is saved as a file first).
/// </summary>
public sealed record UserInput(string Text, IReadOnlyList<string> Files)
{
    public UserInput(string text) : this(text, []) { }

    /// <summary>How many attached pictures the model sees in one message; the others it opens with read_file.</summary>
    private const int MaxShown = 6;

    /// <summary>
    /// The message the model gets: the text, then each file's full path, for its tools to work on
    /// (a picture to start a ComfyUI job from, a document to read, a folder to list), and the
    /// pictures among them, shown as read_file shows a picture. A model that can't see images gets
    /// the paths alone.
    /// </summary>
    /// <param name="grid">The grid the model points on (1000), or null when it points in pixels.</param>
    public ChatMessage ToMessage(bool seesImages, int? grid = null)
    {
        if (Files.Count == 0)
            return new ChatMessage { Role = ChatRole.User, Content = Text, FromUser = true };
        var list = new StringBuilder(Text.Trim());
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
                    .Append(shown.ShownWidth == shown.Width && shown.ShownHeight == shown.Height ? "" : $" at {shown.ShownWidth}x{shown.ShownHeight}")
                    .Append(Picture.GridNote(shown, grid));
                pictures.Add(ContentPart.FromText(file + ":"));
                pictures.Add(ContentPart.FromImage(shown.Jpeg, "image/jpeg"));
            }
            else
                list.Append(Describe(file));
            list.Append(')');
        }
        return pictures.Count == 0
            ? new ChatMessage { Role = ChatRole.User, Content = list.ToString(), FromUser = true }
            : new ChatMessage { Role = ChatRole.User, Parts = [ContentPart.FromText(list.ToString()), .. pictures], FromUser = true };
    }

    /// <summary>What a file is, for the list: "picture 1920x1080, 1.2 MB", "video, 14.0 MB", "a .pdf file, 220 KB".</summary>
    private static string Describe(string file)
    {
        string size;
        try { size = MediaPreview.Size(new FileInfo(file).Length); }
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

    /// <summary>Several messages sent together, as one: their texts one after another, and all their files.</summary>
    public static UserInput Join(IEnumerable<UserInput> inputs)
    {
        var all = inputs.ToList();
        return new UserInput(string.Join("\n\n", all.Select(i => i.Text).Where(t => t.Length > 0)),
            all.SelectMany(i => i.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
