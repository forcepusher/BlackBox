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
    public ChatMessage ToMessage(bool seesImages)
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
                    .Append(shown.ShownWidth == shown.Width && shown.ShownHeight == shown.Height ? "" : $" at {shown.ShownWidth}x{shown.ShownHeight}");
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

    /// <summary>Several messages sent together, as one: their texts one after another, and all their files.</summary>
    public static UserInput Join(IEnumerable<UserInput> inputs)
    {
        var all = inputs.ToList();
        return new UserInput(string.Join("\n\n", all.Select(i => i.Text).Where(t => t.Length > 0)),
            all.SelectMany(i => i.Files).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }
}
