namespace BlackBox.Core.Media;

/// <summary>Markdown files (.md), which the chat previews formatted, as it does a reply.</summary>
public static class MarkdownFiles
{
    /// <summary>The most of a file the chat shows: a long one shows its start, Open shows the rest.</summary>
    public const int MaxChars = 100_000;

    public static bool Handles(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd";

    /// <summary>
    /// The file's text in the encoding it is written in (UTF-8, the PC's ANSI code page, UTF-16), up to
    /// <paramref name="max"/> characters ending on a whole line; <c>Cut</c> when there is more.
    /// </summary>
    public static (string Text, bool Cut) ReadStart(string path, int max = MaxChars)
    {
        using var reader = Tools.TextFiles.OpenText(path);
        var buffer = new char[max + 1];
        var read = reader.ReadBlock(buffer, 0, buffer.Length);
        if (read <= max)
            return (new string(buffer, 0, read), false);
        var text = new string(buffer, 0, max);
        var lineEnd = text.LastIndexOf('\n');
        return (lineEnd > 0 ? text[..lineEnd] : text, true);
    }
}
