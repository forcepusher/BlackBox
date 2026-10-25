using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// Keeps BlackBox's windows visible to the user but out of the agent's way: the main window, the
/// full-screen viewer, Video editor and dialogs, of every chat window.
/// <list type="bullet">
/// <item>Screenshots show what is behind them (see <see cref="WindowsScreenCapture"/>).</item>
/// <item>When a click or drag lands on one, it is pushed to the bottom of the z-order for that action.</item>
/// <item>Before keystrokes, focus moves from it to the window underneath that isn't BlackBox's.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHostWindow : IHostWindow
{
    public IDisposable StepAside(params (int X, int Y)[] points)
    {
        // Top to bottom, as EnumWindows lists them; each one left out of the screenshots, so the
        // model aimed at what is behind it.
        var moved = new List<nint>();
        foreach (var hwnd in BlackBoxWindows())
        {
            if (!GetWindowRect(hwnd, out var r) || !points.Any(p => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom))
                continue;
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            moved.Add(hwnd);
        }
        if (moved.Count == 0)
            return Restore.None;
        Thread.Sleep(60); // let the window underneath become the hit-test target
        return new Restore(moved);
    }

    /// <summary>
    /// BlackBox's visible top-level windows, top to bottom, but for those that let clicks through
    /// (the click log and its marker, which stay on top by design).
    /// </summary>
    private static List<nint> BlackBoxWindows()
    {
        var process = (uint)Environment.ProcessId;
        var windows = new List<nint>();
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && !IsIconic(hwnd) && GetWindowThreadProcessId(hwnd, out var owner) != 0 && owner == process
                && (GetWindowLongPtr(hwnd, GWL_EXSTYLE) & (nint)WS_EX_TRANSPARENT) == 0)
                windows.Add(hwnd);
            return true;
        }, 0);
        return windows;
    }

    public void YieldFocus()
    {
        var foreground = GetForegroundWindow();
        if (foreground == 0 || GetWindowThreadProcessId(foreground, out var owner) == 0 || owner != Environment.ProcessId)
            return;
        if (NextWindowBelow(foreground) is var next and not 0)
        {
            // A window with a modal dialog open (Save As, a message box) is disabled, and keys sent
            // to it are lost; they belong to the dialog, as when Alt+Tab goes back to it.
            SetForegroundWindow(GetLastActivePopup(next) is var popup and not 0 ? popup : next);
            Thread.Sleep(60);
        }
    }

    /// <summary>
    /// The next normal, visible top-level window under <paramref name="hwnd"/> in z-order that isn't
    /// BlackBox's own: from the viewer, the chat's window was next, and the agent's keys went into it;
    /// from one chat window, another.
    /// </summary>
    internal static nint NextWindowBelow(nint hwnd)
    {
        var process = (uint)Environment.ProcessId;
        for (var w = GetWindow(hwnd, GW_HWNDNEXT); w != 0; w = GetWindow(w, GW_HWNDNEXT))
        {
            if (!IsWindowVisible(w) || IsIconic(w) || GetWindow(w, GW_OWNER) != 0) continue;
            if ((GetWindowLongPtr(w, GWL_EXSTYLE) & (nint)WS_EX_TOOLWINDOW) != 0) continue;
            if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (GetWindowTextLength(w) == 0) continue;
            if (GetWindowThreadProcessId(w, out var owner) != 0 && owner == process) continue;
            return w;
        }
        return 0;
    }

    private sealed class Restore(List<nint> windows) : IDisposable
    {
        public static readonly IDisposable None = new Restore([]);

        public void Dispose()
        {
            if (windows.Count == 0) return;
            // Give the target a moment to react (open a menu, gain focus) before coming back on top.
            Thread.Sleep(150);
            // The lowest first, so the viewer that was over the chat ends up over it again.
            for (var i = windows.Count - 1; i >= 0; i--)
                SetWindowPos(windows[i], HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }
}
