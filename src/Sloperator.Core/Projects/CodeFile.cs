using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Sloperator.Core.Tools;

namespace Sloperator.Core.Projects;

/// <summary>
/// A text file opened in the Code editor panel: its text as read_file reads it, and the encoding it is
/// written back in (a byte order mark kept or left out as it was, the ANSI code page kept), so saving
/// changes only what was typed. Line breaks stay as they are in the text: the editor keeps them. Another
/// encoding picked is written on the next save; other line breaks are an edit of the text.
/// </summary>
public sealed partial class CodeFile
{
    /// <summary>A bigger file isn't opened: it is no code to edit by hand, and the editor would crawl.</summary>
    public const long MaxBytes = 5_000_000;

    private CodeFile(string path, string text, Encoding encoding)
    {
        Path = path;
        Text = text;
        Encoding = SavedEncoding = encoding;
    }

    public string Path { get; }

    /// <summary>What the file held when it was read.</summary>
    public string Text { get; }

    /// <summary>The encoding it is written in on the next save: the file's own, or another picked.</summary>
    public Encoding Encoding { get; set; }

    /// <summary>The encoding the file is in on disk.</summary>
    public Encoding SavedEncoding { get; private set; }

    /// <summary>Another encoding was picked than the one the file is in: it is to be saved.</summary>
    public bool EncodingChanged => !Same(Encoding, SavedEncoding);

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
    /// Writes <paramref name="text"/> over the file in <see cref="Encoding"/> (through a file beside it,
    /// as edit_file does); a PowerShell script typed past ASCII in UTF-8 without the signature gets it,
    /// as Windows PowerShell would read it in the ANSI code page without one.
    /// </summary>
    public async Task SaveAsync(string text, CancellationToken ct)
    {
        if (TextFiles.NeedsSignature(Path, Encoding, text))
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await TextFiles.WriteAllAsync(Path, text, Encoding, ct).ConfigureAwait(false);
        SavedEncoding = Encoding;
    }

    /// <summary>
    /// The file's text on disk now (written again by the agent, another program), in its encoding, which
    /// it is saved in from now on unless another was picked; null when it can't be read (gone, held).
    /// </summary>
    public async Task<string?> ReadAgainAsync(CancellationToken ct)
    {
        try
        {
            var (text, encoding) = await TextFiles.ReadAllAsync(Path, ct).ConfigureAwait(false);
            if (!EncodingChanged)
                Encoding = encoding;
            SavedEncoding = encoding;
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---- the encodings and line breaks offered ---------------------------------------------------------

    /// <summary>
    /// The encodings a file can be saved in, as Visual Studio's and VS Code's lists start: UTF-8 with the
    /// signature and without, UTF-16 both ways round, and this PC's ANSI code page. They are the ones
    /// read_file tells files apart by, so every file opened is in one of them.
    /// </summary>
    public static IReadOnlyList<Encoding> Encodings { get; } =
    [
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        TextFiles.Ansi,
    ];

    /// <summary>"UTF-8", "UTF-8 with BOM", "UTF-16 LE", "UTF-16 BE", "Windows-1252".</summary>
    public static string NameOf(Encoding encoding) => encoding.CodePage switch
    {
        65001 => encoding.GetPreamble().Length > 0 ? "UTF-8 with BOM" : "UTF-8",
        1200 => "UTF-16 LE",
        1201 => "UTF-16 BE",
        _ => encoding.WebName.StartsWith("windows-", StringComparison.Ordinal) ? "Windows-" + encoding.WebName[8..] : encoding.WebName.ToUpperInvariant(),
    };

    /// <summary>The same encoding: code page and whether it writes a signature (a UTF-8 made with other fallbacks is still UTF-8).</summary>
    public static bool Same(Encoding a, Encoding b) => a.CodePage == b.CodePage && a.GetPreamble().Length > 0 == b.GetPreamble().Length > 0;

    /// <summary>The first character of <paramref name="text"/> that <paramref name="encoding"/> can't write (a "✓" in Windows-1252), with its line; null when it writes all of them.</summary>
    public static (char Char, int Line)? Unwritable(string text, Encoding encoding)
    {
        if (encoding.CodePage is 65001 or 1200 or 1201)
            return null;
        var strict = Encoding.GetEncoding(encoding.CodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            strict.GetByteCount(text);
            return null;
        }
        catch (EncoderFallbackException ex)
        {
            return (ex.CharUnknown, text.AsSpan(0, Math.Max(0, ex.Index)).Count('\n') + 1);
        }
    }

    /// <summary>The line breaks a file's lines can end in: Windows' (CRLF) and Unix's and macOS's (LF).</summary>
    public static IReadOnlyList<(string Name, string Break)> LineBreaks { get; } = [("CRLF", "\r\n"), ("LF", "\n")];

    /// <summary>
    /// The line break <paramref name="text"/> ends its lines in, the one most of its lines do ("\r\n",
    /// "\n", or an old Mac's "\r"), and whether some end in another; Windows' "\r\n" for a text of one line,
    /// as the editor puts in a new line there.
    /// </summary>
    public static (string Break, bool Mixed) LineBreakOf(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    crlf++;
                    i++;
                }
                else
                    cr++;
            }
            else if (text[i] == '\n')
                lf++;
        }
        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        var most = crlf >= lf && crlf >= cr ? "\r\n" : lf >= cr ? "\n" : "\r";
        return (kinds == 0 ? "\r\n" : most, kinds > 1);
    }

    /// <summary>"CRLF", "LF", "CR".</summary>
    public static string NameOfBreak(string lineBreak) => lineBreak switch { "\r\n" => "CRLF", "\n" => "LF", _ => "CR" };

    /// <summary><paramref name="text"/> with every line ending in <paramref name="lineBreak"/>.</summary>
    public static string WithLineBreaks(string text, string lineBreak) => AnyBreak().Replace(text, lineBreak);

    [GeneratedRegex(@"\r\n|\r|\n")]
    private static partial Regex AnyBreak();
}
