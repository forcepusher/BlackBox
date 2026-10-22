using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// The other BlackBoxes running on the PC, a window each in a process of its own. Every BlackBox
/// keeps a hidden message-only window of a known class, through which the others ask it to stay
/// out of a screenshot for the moment it is taken, and whether the user is typing to it.
/// </summary>
/// <remarks>
/// A process can leave only its own windows out of a capture (SetWindowDisplayAffinity), so a
/// second BlackBox showed in the first one's screenshots, its chat and all, and its agent read
/// another task in it. Asked through a window message, which takes a fraction of a millisecond,
/// each one leaves itself out of the other's screenshots as it does out of its own.
/// </remarks>
[SupportedOSPlatform("windows")]
internal static class WindowsNeighbours
{
    private const string ClassName = "BlackBox.Neighbour";
    private const uint WM_HIDE = WM_APP + 1, WM_SHOW = WM_APP + 2, WM_TYPING = WM_APP + 3, WM_STOP = WM_APP + 4;

    /// <summary>How long another BlackBox gets to answer; one whose window hangs is left as it is.</summary>
    private const uint AnswerMs = 250;

    private static readonly Lock Gate = new();
    private static WndProc? _proc; // kept alive for as long as the window exists
    private static Func<bool>? _typing;
    private static Func<bool>? _stop;
    private static List<nint> _hidden = [];

    /// <summary>Puts up this process's own message window, once.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_proc is not null)
                return;
            _proc = Receive;
        }
        using var ready = new ManualResetEventSlim();
        new Thread(() =>
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = GetModuleHandle(null),
                lpszClassName = ClassName,
            };
            RegisterClassEx(ref wc);
            CreateWindowEx(0, ClassName, "", 0, 0, 0, 0, 0, HWND_MESSAGE, 0, wc.hInstance, 0);
            ready.Set();
            while (GetMessage(out var msg, 0, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }) { IsBackground = true, Name = "BlackBox neighbours" }.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>What this BlackBox answers when another asks whether the user is typing to it.</summary>
    public static void AnswerTyping(Func<bool> typing) => _typing = typing;

    /// <summary>The message windows of the other BlackBoxes, with their processes.</summary>
    private static List<(nint Window, uint Process)> Others()
    {
        var others = new List<(nint, uint)>();
        var self = (uint)Environment.ProcessId;
        for (nint w = 0; (w = FindWindowEx(HWND_MESSAGE, w, ClassName, null)) != 0;)
            if (GetWindowThreadProcessId(w, out var process) != 0 && process != self)
                others.Add((w, process));
        return others;
    }

    /// <summary>The processes of every BlackBox on the PC, this one's included.</summary>
    public static HashSet<uint> Processes()
    {
        var all = Others().Select(o => o.Process).ToHashSet();
        all.Add((uint)Environment.ProcessId);
        return all;
    }

    /// <summary>
    /// Asks the other BlackBoxes to leave their windows out of screen capture; returns them all, to
    /// be asked back by <see cref="ShowOthers"/>: one that answers too late still hides, and would
    /// otherwise stay out of recordings and screen sharing.
    /// </summary>
    public static List<nint> HideOthers()
    {
        var asked = Others().Select(o => o.Window).ToList();
        foreach (var window in asked)
            Ask(window, WM_HIDE);
        return asked;
    }

    /// <summary>
    /// Shows again the windows <see cref="HideOthers"/> hid. Sent and waited for, not posted: a
    /// message sent for the next screenshot is handled before one posted earlier, and the windows
    /// would have stayed out of every capture after it.
    /// </summary>
    public static void ShowOthers(List<nint> hidden)
    {
        foreach (var window in hidden)
            Ask(window, WM_SHOW);
    }

    /// <summary>Whether the user is typing to another BlackBox right now.</summary>
    public static bool OthersTyping() => Others().Any(o => Ask(o.Window, WM_TYPING) == 1);

    /// <summary>
    /// What stops this BlackBox's run when Pause/Break stopped another's (see <see cref="WindowsStopKey"/>);
    /// null, from the one given as <paramref name="instead"/>, when it no longer watches.
    /// </summary>
    public static void OnStop(Func<bool>? stop, Func<bool>? instead = null)
    {
        if (stop is not null || ReferenceEquals(_stop, instead))
            _stop = stop;
    }

    /// <summary>Stops the other BlackBoxes' runs: one Pause/Break stops them all.</summary>
    public static void StopOthers()
    {
        foreach (var (window, _) in Others())
            Ask(window, WM_STOP);
    }

    private static nint Ask(nint window, uint message) =>
        SendMessageTimeout(window, message, 0, 0, SMTO_ABORTIFHUNG, AnswerMs, out var result) != 0 ? result : 0;

    private static nint Receive(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_HIDE:
                    lock (Gate)
                        _hidden.AddRange(WindowsScreenCapture.HideOwnWindows(null));
                    return 1;
                case WM_SHOW:
                    List<nint> shown;
                    lock (Gate)
                        (shown, _hidden) = (_hidden, []);
                    foreach (var window in shown)
                        SetWindowDisplayAffinity(window, WDA_NONE);
                    return 1;
                case WM_TYPING:
                    return _typing?.Invoke() == true ? 1 : 0;
                case WM_STOP:
                    return _stop?.Invoke() == true ? 1 : 0;
            }
        }
        catch
        {
            // A window procedure must never throw into the system.
            return 0;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }
}
