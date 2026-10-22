using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace BlackBox.App;

/// <summary>
/// One BlackBox at a time per Data folder: started again (a second double-click on the exe, a
/// shortcut), it asks the one that runs to open another window, and ends. Windows side by side
/// are then one process, which shares the mouse and keyboard, Pause/Break and the chats among them
/// without any talk between programs (see <see cref="ChatWindows"/>).
/// </summary>
/// <remarks>
/// The one that runs is found by a named mutex and asked over a named pipe of the same name, open
/// to this Windows user alone. The name stands for the user, the Windows session, the Data folder
/// and this build: another build in the folder (a newer download started beside the old one) runs
/// on its own, as it would before.
/// </remarks>
internal static class SingleInstance
{
    /// <summary>Kept for as long as the process runs: while it exists, BlackBox runs from the folder.</summary>
    private static Mutex? _running;

    private static string _name = "";

    private static readonly Lock Gate = new();

    /// <summary>Asked for a window before the first one was up: opened once it is.</summary>
    private static int _early;

    private static Action? _openWindow;

    /// <summary>Set once BlackBox is ending: a start then runs by itself, rather than wait for a window that would never open.</summary>
    private static volatile bool _ending;

    /// <summary>
    /// True when a BlackBox runs from the Data folder already and has opened another window: this
    /// one is done. Otherwise this one is the BlackBox for the folder, and takes the next starts'
    /// requests from now on (see <see cref="OnOpenWindow"/>).
    /// </summary>
    public static bool HandedOff(string dataDir)
    {
        _name = Name(dataDir);
        var first = true;
        try { _running = new Mutex(false, @"Local\" + _name, out first); }
        // Made by a BlackBox run as administrator, which this one may not open: that one runs.
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException) { first = false; }
        // One that doesn't answer (hung, or closing) is left be: this one runs beside it.
        if (!first && AskForWindow())
            return true;
        _ = Task.Run(ListenAsync);
        return false;
    }

    /// <summary>What opens another window when a start asks for one; called on the thread pool.</summary>
    public static void OnOpenWindow(Action open)
    {
        int early;
        lock (Gate)
            (_openWindow, early, _early) = (open, _early, 0);
        for (; early > 0; early--)
            open();
    }

    /// <summary>BlackBox is ending (its last window closed): starts are no longer answered.</summary>
    public static void Ending() => _ending = true;

    private static string Name(string dataDir)
    {
        var build = typeof(SingleInstance).Assembly.ManifestModule.ModuleVersionId;
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var key = $"{Environment.UserDomainName}\\{Environment.UserName}|{self.SessionId}|{Path.GetFullPath(dataDir).TrimEnd('\\', '/').ToUpperInvariant()}|{build}";
        return "BlackBox-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
    }

    /// <summary>Asks the BlackBox that runs for another window; false when it doesn't answer.</summary>
    private static bool AskForWindow()
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            // It may be starting up itself.
            pipe.Connect(10_000);
            using var answers = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            // This start has the foreground, being what the user just opened: it lets the one that
            // runs bring its new window to the front, which Windows doesn't let a program do by itself.
            if (int.TryParse(reader.ReadLineAsync(answers.Token).AsTask().GetAwaiter().GetResult(), out var process))
                AllowToFront(process);
            writer.WriteLine("window");
            return reader.ReadLineAsync(answers.Token).AsTask().GetAwaiter().GetResult() == "ok";
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task ListenAsync()
    {
        while (true)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                // One that connects and says nothing doesn't hold up the next.
                using var talk = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                await writer.WriteLineAsync(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture).AsMemory(), talk.Token).ConfigureAwait(false);
                if (await reader.ReadLineAsync(talk.Token).ConfigureAwait(false) != "window" || _ending)
                    continue;
                Action? open;
                lock (Gate)
                    if ((open = _openWindow) is null)
                        _early++;
                open?.Invoke();
                await writer.WriteLineAsync("ok".AsMemory(), talk.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // A start that gave up waiting.
            }
            catch (UnauthorizedAccessException)
            {
                // The name is another user's: no start of this user's finds this one then either.
                return;
            }
        }
    }

    private static void AllowToFront(int process)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
            Platform.Windows.WindowsPlatform.AllowToFront(process);
#endif
    }
}
