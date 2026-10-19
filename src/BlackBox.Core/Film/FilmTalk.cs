using System.Text.Json;
using System.Text.Json.Serialization;

namespace BlackBox.Core.Film;

/// <summary>One thing said about a piece of a film: by the user, or by BlackBox back.</summary>
/// <param name="Who"><see cref="FilmTalk.You"/> or <see cref="FilmTalk.BlackBox"/>.</param>
public sealed record FilmTalkLine(string Who, string Text, DateTime At);

/// <summary>
/// What the user and BlackBox said about each piece of a film (a frame, a shot, the song) and about
/// the whole film, kept in talk.json beside the storyboard: the window shows a piece's when it is
/// picked, and BlackBox reads it back so "darker again" means something.
/// </summary>
public sealed class FilmTalk
{
    public const string You = "you", BlackBox = "blackbox";

    /// <summary>What the whole film's talk is kept under; a piece's is under its id.</summary>
    public const string Film = "film", Song = "song";

    public const string FileName = "talk.json";

    private readonly Dictionary<string, List<FilmTalkLine>> _lines;

    private FilmTalk(Dictionary<string, List<FilmTalkLine>> lines) => _lines = lines;

    public IReadOnlyList<FilmTalkLine> About(string key) => _lines.TryGetValue(key, out var lines) ? lines : [];

    public void Add(string key, string who, string text)
    {
        if (!_lines.TryGetValue(key, out var lines))
            _lines[key] = lines = [];
        lines.Add(new FilmTalkLine(who, text, DateTime.UtcNow));
    }

    /// <summary>Changes the last thing BlackBox said about a piece (how a job it is doing is getting on).</summary>
    public void ReplaceLast(string key, string text)
    {
        if (_lines.TryGetValue(key, out var lines) && lines.Count > 0 && lines[^1].Who == BlackBox)
            lines[^1] = lines[^1] with { Text = text };
        else
            Add(key, BlackBox, text);
    }

    public void Forget(string key) => _lines.Remove(key);

    public static FilmTalk Load(string folder)
    {
        try
        {
            var file = Path.Combine(folder, FileName);
            if (File.Exists(file) && JsonSerializer.Deserialize(File.ReadAllText(file), FilmTalkJson.Default.DictionaryStringListFilmTalkLine) is { } lines)
                return new FilmTalk(new Dictionary<string, List<FilmTalkLine>>(lines, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new FilmTalk(new Dictionary<string, List<FilmTalkLine>>(StringComparer.OrdinalIgnoreCase));
    }

    public void Save(string folder)
    {
        if (folder.Length == 0)
            return;
        Directory.CreateDirectory(folder);
        Settings.AtomicFile.Write(Path.Combine(folder, FileName), JsonSerializer.Serialize(_lines, Readable.DictionaryStringListFilmTalkLine));
    }

    /// <summary>Written as people read it (the user may open the file).</summary>
    private static readonly FilmTalkJson Readable = new(new JsonSerializerOptions(FilmTalkJson.Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, List<FilmTalkLine>>))]
internal sealed partial class FilmTalkJson : JsonSerializerContext;
