using System.Text;

namespace BlackBox.Core.Tools;

/// <summary>
/// The encoding a text file is written in, so that read_file shows it right and edit_file and
/// write_file's append keep the rest of it as it was. Windows PowerShell, the agent's own shell,
/// writes the PC's ANSI code page with Set-Content and UTF-16 with Out-File and "&gt;", as do Excel's
/// CSV export and most .bat and .ini files; read as UTF-8, every é and € of such a file became
/// U+FFFD, and edit_file wrote that back over the whole file.
/// </summary>
internal static class TextFiles
{
    /// <summary>How much of a file is looked at to tell UTF-8 from the ANSI code page.</summary>
    private const int Sample = 64 * 1024;

    static TextFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The PC's ANSI code page (Windows-1252 on an English or Western European Windows); Latin-1 where there is none.</summary>
    private static Encoding Ansi
    {
        get
        {
            try { return Encoding.GetEncoding(0) is { CodePage: not 65001 } ansi ? ansi : Encoding.Latin1; }
            catch (Exception ex) when (ex is NotSupportedException or ArgumentException) { return Encoding.Latin1; }
        }
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The encoding of a file that starts with <paramref name="head"/>: its signature's, else UTF-8 when
    /// those bytes are UTF-8 (a character cut off at the end of a sample is allowed), else ANSI.
    /// An encoding found from a signature writes it again; the others write none.
    /// </summary>
    public static Encoding Detect(ReadOnlySpan<byte> head, bool whole)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        // A sample may end in the middle of a character: the last few bytes are left out of the test.
        var test = whole ? head : head[..Math.Max(0, head.Length - 3)];
        try
        {
            StrictUtf8.GetCharCount(test);
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        catch (DecoderFallbackException)
        {
            return Ansi;
        }
    }

    /// <summary>The encoding of the file at <paramref name="path"/>, from its first bytes.</summary>
    public static Encoding Detect(string path)
    {
        using var file = OpenRead(path);
        var head = new byte[Sample];
        var read = file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return Detect(head.AsSpan(0, read), whole: read < head.Length);
    }

    /// <summary>A whole text file and the encoding it is in.</summary>
    public static async Task<(string Text, Encoding Encoding)> ReadAllAsync(string path, CancellationToken ct)
    {
        byte[] bytes;
        await using (var file = OpenRead(path))
        {
            bytes = new byte[file.Length];
            await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        }
        var encoding = Detect(bytes, whole: true);
        var preamble = encoding.GetPreamble().Length;
        return (encoding.GetString(bytes, preamble, bytes.Length - preamble), encoding);
    }

    /// <summary>Adds <paramref name="text"/> to the end of a file, in the encoding the file is in.</summary>
    public static async Task AppendAsync(string path, string text, CancellationToken ct)
    {
        var encoding = Detect(path);
        var bytes = encoding.GetBytes(text); // GetBytes writes no signature
        await using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        await file.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    /// <summary>A reader for the file in its own encoding, line by line, however big it is.</summary>
    public static StreamReader OpenText(string path) => new(OpenRead(path), Detect(path), detectEncodingFromByteOrderMarks: true);

    /// <summary>
    /// Opens a file to read while other programs have it open for writing too (a server's log, a
    /// database): Windows refuses a plain read then, whatever the writer allows.
    /// </summary>
    public static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
