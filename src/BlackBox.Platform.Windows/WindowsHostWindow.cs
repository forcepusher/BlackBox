using System.Runtime.Versioning;
using BlackBox.Core.Platform;
using static BlackBox.Platform.Windows.Native;

namespace BlackBox.Platform.Windows;

/// <summary>
/// Keeps BlackBox's windows visible to the user but out of the agent's way: the main window, and
/// the full-screen viewer while it is open.
/// <list type="bullet">
/// <item>Screenshots show what is behind them (see <see cref="WindowsScreenCapture"/>).</item>
/// <item>When a click or drag lands on one, it is pushed to the bottom of the z-order for that action.</item>
/// <item>Before keystrokes, focus moves from it to the window underneath that isn't BlackBox's.</item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsHostWindow : IHostWindow
{
    private readonly List<nint> _windows = [];

    public void Attach(nint handle)
    {
        lock (_windows)
            if (handle != 0 && !_windows.Contains(handle))
                _windows.Add(handle);
    }

    public void Detach(nint handle)
    {
        lock (_windows)
            _windows.Remove(handle);
    }

    private List<nint> Windows()
    {
        lock (_windows)
            return [.. _windows];
    }

    public IDisposable StepAside(params (int X, int Y)[] points)
    {
        var moved = new List<nint>();
        foreach (var hwnd in Windows())
        {
            if (IsIconic(hwnd) || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var r))
                continue;
            if (!points.Any(p => p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom))
                continue;
            SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            moved.Add(hwnd);
        }
        if (moved.Count == 0)
            return Restore.None;
        Thread.Sleep(60); // let the window underneath become the hit-test target
        return new Restore(moved);
    }

    public void YieldFocus()
    {
        var foreground = GetForegroundWindow();
        if (!Windows().Contains(foreground))
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
    /// BlackBox's own: from the viewer, the chat's window was next, and the agent's keys went into it.
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
            // The chat first, so the viewer that was over it ends up over it again.
            foreach (var hwnd in windows)
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
    }
}
