using System.Globalization;
using System.Text;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Projects;

/// <summary>
/// A text file opened in the Code editor panel: its text as read_file reads it, and the encoding it is
/// written back in (a byte order mark kept or left out as it was, the ANSI code page kept), so saving
/// changes only what was typed. Line breaks stay as they are in the text: the editor keeps them.
/// </summary>
public sealed class CodeFile
{
    /// <summary>A bigger file isn't opened: it is no code to edit by hand, and the editor would crawl.</summary>
    public const long MaxBytes = 5_000_000;

    private CodeFile(string path, string text, Encoding encoding)
    {
        Path = path;
        Text = text;
        Encoding = encoding;
    }

    public string Path { get; }

    /// <summary>What the file held when it was read.</summary>
    public string Text { get; }

    public Encoding Encoding { get; private set; }

    /// <summary>The file read to be edited, or why it isn't ("Not a text file.").</summary>
    public static async Task<(CodeFile? File, string? Why)> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var length = new FileInfo(path).Length;
            if (length > MaxBytes)
                return (null, string.Create(CultureInfo.InvariantCulture, $"Too big to edit here ({length / 1_000_000.0:0.#} MB)."));
            if (TextFiles.IsBinary(path))
                return (null, "Not a text file.");
            var (text, encoding) = await TextFiles.ReadAllAsync(path, ct).ConfigureAwait(false);
            return (new CodeFile(path, text, encoding), null);
        }
        catch (FileNotFoundException)
        {
            return (null, "The file isn't there any more.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>
    /// Writes <paramref name="text"/> over the file in the encoding it was read in (through a file beside
    /// it, as edit_file does); a PowerShell script typed past ASCII gets UTF-8's signature, as Windows
    /// PowerShell would read it in the ANSI code page without one.
    /// </summary>
    public async Task SaveAsync(string text, CancellationToken ct)
    {
        if (TextFiles.NeedsSignature(Path, Encoding, text))
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await TextFiles.WriteAllAsync(Path, text, Encoding, ct).ConfigureAwait(false);
    }

    /// <summary>The file's text on disk now (written again by the agent, another program), whose encoding it is saved in from now on; null when it can't be read (gone, held).</summary>
    public async Task<string?> ReadAgainAsync(CancellationToken ct)
    {
        try
        {
            var (text, encoding) = await TextFiles.ReadAllAsync(Path, ct).ConfigureAwait(false);
            Encoding = encoding;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
