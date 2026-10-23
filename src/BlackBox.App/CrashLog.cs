using System.Globalization;
using System.Runtime.InteropServices;
using BlackBox.Core.Settings;

namespace BlackBox.App;

/// <summary>
/// What went wrong where no code caught it. BlackBox runs for hours on its own; without this, an
/// exception nothing caught closed the window and left no trace of why. Each one goes into
/// Data/crash.log; one that ended BlackBox is also shown in the chat at the next start, and one
/// that kept it from starting in a message box.
/// </summary>
internal static class CrashLog
{
    /// <summary>The log is cut back to its newer half beyond twice this.</summary>
    private const int MaxChars = 256 * 1024;

    /// <summary>The same exception again within this is not written again (one thrown at every layout pass would fill the disk).</summary>
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, DateTime> LastWritten = [];
    private static AppPaths? _paths;

    public static string FileName(AppPaths paths) => Path.Combine(paths.DataDir, "crash.log");

    /// <summary>Where the summary of a crash that ended BlackBox waits for the next start.</summary>
    private static string LastCrashFile(AppPaths paths) => Path.Combine(paths.DataDir, "crashed.txt");

    /// <summary>Writes down every exception that nothing caught, from any thread.</summary>
    public static void Watch(AppPaths paths)
    {
        _paths = paths;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Ended(ex);
        };
        // A task nobody waited for that failed: not fatal, but the reason a check or a save quietly didn't happen.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("a task nobody waited for", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>An exception that ends BlackBox: written down, and kept for the chat to show at the next start.</summary>
    public static void Ended(Exception ex)
    {
        Write("ended BlackBox", ex);
        Core.ErrorLog.Write("BlackBox", "BlackBox closed on an error.", ex);
        if (_paths is not { } paths)
            return;
        try { File.WriteAllText(LastCrashFile(paths), $"{DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} · {Summary(ex)}"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Adds the exception to the log, if the Data folder takes it.</summary>
    public static void Write(string where, Exception ex)
    {
        if (_paths is not { } paths)
            return;
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            var summary = Summary(ex);
            if (LastWritten.TryGetValue(summary, out var last) && now - last < Quiet)
                return;
            LastWritten[summary] = now;
            try
            {
                Directory.CreateDirectory(paths.DataDir);
                var file = FileName(paths);
                File.AppendAllText(file, $"{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} ({where}){Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
                if (new FileInfo(file).Length > MaxChars * 2L)
                    File.WriteAllText(file, File.ReadAllText(file)[^MaxChars..]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The crash that ended BlackBox last time, once ("14:05:09 · InvalidOperationException: …"); null if there was none.</summary>
    public static string? TakeLastCrash(AppPaths paths)
    {
        var file = LastCrashFile(paths);
        try
        {
            if (!File.Exists(file))
                return null;
            var text = File.ReadAllText(file).Trim();
            File.Delete(file);
            return text.Length > 0 ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>What happened, in a line: the exception's type and message.</summary>
    public static string Summary(Exception ex) => $"{ex.GetType().Name}: {ex.Message}".ReplaceLineEndings(" ");

    /// <summary>BlackBox failed before its window was up: double-clicked, nothing happened, and no way to tell why.</summary>
    public static void ShowCouldNotStart(Exception ex)
    {
        Write("could not start", ex);
        Core.ErrorLog.Write("BlackBox", "BlackBox could not start.", ex);
        if (!OperatingSystem.IsWindows())
            return;
        // Put in Program Files (or another folder Windows keeps users out of), it said only
        // "UnauthorizedAccessException: Access to the path '…\Data\runtime' is denied."
        if (ex is UnauthorizedAccessException && _paths is { } paths && !CanWrite(paths.DataDir))
            MessageBoxW(0, $"BlackBox keeps its settings and chats in a Data folder next to BlackBox.exe, and Windows doesn't let it write in {paths.Root}.\n\n" +
                           "Move BlackBox.exe to a folder of your own, such as Documents or the Desktop, and start it from there.", "BlackBox", 0x10 /* MB_ICONERROR */);
        else
            MessageBoxW(0, $"BlackBox could not start.\n\n{Summary(ex)}" + (_paths is { } p ? $"\n\nDetails: {FileName(p)}" : ""), "BlackBox", 0x10 /* MB_ICONERROR */);
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint owner, string text, string caption, uint type);
}
