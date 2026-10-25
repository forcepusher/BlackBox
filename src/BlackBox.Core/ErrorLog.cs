using System.Globalization;
using BlackBox.Core.Settings;

namespace BlackBox.Core;

/// <summary>
/// Every error BlackBox shows, written down in Data/errors.log: where it was shown, what it said,
/// and the whole exception behind it when there was one. The chat keeps only the line, and a
/// window gone or a chat deleted took even that: a task that seemed to end on a server's error
/// left nothing to tell what the server had said, nor whether the task was done.
/// </summary>
public static class ErrorLog
{
    /// <summary>The log is cut back to its newer half beyond twice this.</summary>
    private const int MaxChars = 512 * 1024;

    private static readonly object Gate = new();
    private static string? _file;

    public static string FileName(AppPaths paths) => Path.Combine(paths.DataDir, "errors.log");

    /// <summary>Starts writing to Data/errors.log; before (in the tests, the command-line runner) nothing is written.</summary>
    public static void Start(AppPaths paths) => _file = FileName(paths);

    /// <summary>Writes nothing from now on.</summary>
    public static void Stop() => _file = null;

    /// <summary>
    /// Adds what was shown <paramref name="where"/> ("chat \"Build it\"", "Video editor") to the log,
    /// with the exception behind it, if the Data folder takes it.
    /// </summary>
    public static void Write(string where, string shown, Exception? error = null)
    {
        if (_file is not { } file)
            return;
        var nl = Environment.NewLine;
        var entry = $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ({where}){nl}{shown.Trim()}{nl}"
                    + (error is null ? "" : error + nl) + nl;
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.AppendAllText(file, entry);
                if (new FileInfo(file).Length > MaxChars * 2L)
                    File.WriteAllText(file, Newest(File.ReadAllText(file)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The newest entries of <paramref name="log"/> that fit in <see cref="MaxChars"/>, from the start of one.</summary>
    internal static string Newest(string log)
    {
        if (log.Length <= MaxChars)
            return log;
        var tail = log[^MaxChars..];
        var nl = Environment.NewLine;
        var next = tail.IndexOf(nl + nl, StringComparison.Ordinal);
        return next < 0 ? tail : tail[(next + 2 * nl.Length)..];
    }
}
